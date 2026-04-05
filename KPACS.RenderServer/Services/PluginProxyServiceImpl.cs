// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - PluginProxyServiceImpl
//
// gRPC service that exposes server-side plugins to thin clients.
// The render server discovers and manages plugins locally; thin clients
// invoke them transparently over the existing render-server channel.
// ------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using Grpc.Core;
using KPACS.RenderServer.Protos;
using KPACS.SDK;
using KPACS.SDK.Contracts;
using KPACS.SDK.Models;
using KPACS.Viewer.Models;
using KPACS.Viewer.Plugins;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.RenderServer.Services;

public sealed class PluginProxyServiceImpl : PluginProxyService.PluginProxyServiceBase
{
    private readonly PluginManager _pluginManager;
    private readonly VolumeManager _volumeManager;
    private readonly ILogger<PluginProxyServiceImpl> _logger;

    /// <summary>
    /// Transient mask store keyed by server-generated tokens.
    /// Masks are held until the client pulls them via GetMaskData,
    /// or until a background reaper cleans up stale entries.
    /// </summary>
    private readonly ConcurrentDictionary<string, MaskDataEntry> _maskStore = new();

    public PluginProxyServiceImpl(
        PluginManager pluginManager,
        VolumeManager volumeManager,
        ILogger<PluginProxyServiceImpl> logger)
    {
        _pluginManager = pluginManager;
        _volumeManager = volumeManager;
        _logger = logger;
    }

    // ── ListPlugins ────────────────────────────────────────────

    public override Task<ListPluginsResponse> ListPlugins(
        ListPluginsRequest request, ServerCallContext context)
    {
        var response = new ListPluginsResponse();

        foreach (PluginInstance plugin in _pluginManager.Plugins)
        {
            response.Plugins.Add(new PluginSummary
            {
                Id = plugin.Manifest.Id,
                Name = plugin.Manifest.Name,
                Version = plugin.Manifest.Version,
                Author = plugin.Manifest.Author ?? string.Empty,
                Description = plugin.Manifest.Description ?? string.Empty,
                License = plugin.Manifest.License ?? string.Empty,
                Capabilities = (uint)plugin.Manifest.Capabilities,
                State = MapState(plugin.State),
            });
        }

        return Task.FromResult(response);
    }

    // ── GetSegmentationTasks ───────────────────────────────────

    public override async Task<ProxyGetSegTasksResponse> GetSegmentationTasks(
        ProxyGetSegTasksRequest request, ServerCallContext context)
    {
        ISegmentationProvider provider =
            await _pluginManager.GetSegmentationProviderAsync(request.PluginId, context.CancellationToken);

        var response = new ProxyGetSegTasksResponse();

        foreach (SegmentationTaskInfo task in provider.AvailableTasks)
        {
            var taskMsg = new ProxySegTaskInfo
            {
                Id = task.Id,
                Name = task.Name,
                Description = task.Description ?? string.Empty,
                StructureCount = task.StructureCount,
                RequiresLicense = task.RequiresLicense,
            };
            taskMsg.Modalities.AddRange(task.SupportedModalities);

            foreach (StructureCatalogEntry s in task.Structures)
            {
                taskMsg.Structures.Add(new ProxyStructureEntry
                {
                    Label = s.Label,
                    Id = s.Id,
                    DisplayName = s.DisplayName ?? string.Empty,
                    Region = s.Region ?? string.Empty,
                });
            }

            response.Tasks.Add(taskMsg);
        }

        return response;
    }

    // ── RunSegmentation ────────────────────────────────────────

    public override async Task RunSegmentation(
        ProxySegmentationRequest request,
        IServerStreamWriter<ProxySegmentationEvent> responseStream,
        ServerCallContext context)
    {
        CancellationToken ct = context.CancellationToken;

        // Resolve the loaded volume from the session.
        LoadedVolume? loadedVolume = _volumeManager.GetVolume(request.VolumeId);
        SeriesVolume? volume = loadedVolume?.Volume;
        if (volume is null)
        {
            await responseStream.WriteAsync(new ProxySegmentationEvent
            {
                Error = new ProxySegError { Message = $"Volume '{request.VolumeId}' not found or not loaded." },
            }, ct);
            return;
        }

        // Prepare a temp directory for the plugin to write its output.
        string outputDir = Path.Combine(Path.GetTempPath(), "kpacs-plugin-seg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        try
        {
            // Export the volume as a NIfTI file the plugin can read.
            string niftiPath = Path.Combine(outputDir, "input.nii.gz");
            await Task.Run(() => ExportVolumeAsNifti(volume, niftiPath), ct);

            await responseStream.WriteAsync(new ProxySegmentationEvent
            {
                Progress = new ProxySegProgress
                {
                    Step = 0, TotalSteps = 4, PercentComplete = 5,
                    StatusMessage = "Volume exported — starting plugin…",
                },
            }, ct);

            // Build the SDK request.
            var sdkRequest = new SegmentationRequest
            {
                Volume = new VolumeDescriptor
                {
                    FilePath = niftiPath,
                    Format = "nifti",
                    Dimensions = [volume.SizeX, volume.SizeY, volume.SizeZ],
                    SpacingMm = [volume.SpacingX, volume.SpacingY, volume.SpacingZ],
                    Modality = string.Empty, // server doesn't track modality on the volume
                    SeriesInstanceUid = volume.SeriesInstanceUid,
                },
                TaskId = request.TaskId,
                OutputDirectory = outputDir,
                Device = string.IsNullOrEmpty(request.Device) ? "gpu" : request.Device,
                ProduceMultilabel = request.ProduceMultilabel,
                RoiSubset = request.RoiSubset.Count > 0 ? [.. request.RoiSubset] : null,
                Parameters = request.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
            };

            // Start the plugin and run segmentation.
            ISegmentationProvider provider =
                await _pluginManager.GetSegmentationProviderAsync(request.PluginId, ct);

            var progress = new Progress<ProgressReport>(p =>
            {
                // Fire-and-forget the write since Progress<T> posts to the sync context.
                _ = responseStream.WriteAsync(new ProxySegmentationEvent
                {
                    Progress = new ProxySegProgress
                    {
                        Step = p.Step,
                        TotalSteps = p.TotalSteps,
                        PercentComplete = p.PercentComplete,
                        StatusMessage = p.StatusMessage ?? string.Empty,
                    },
                }, ct);
            });

            SegmentationResult result = await provider.RunAsync(sdkRequest, progress, ct);

            if (!result.Success)
            {
                await responseStream.WriteAsync(new ProxySegmentationEvent
                {
                    Error = new ProxySegError { Message = result.ErrorMessage ?? "Segmentation failed." },
                }, ct);
                return;
            }

            // Convert each structure result into a packed-bit mask and store it
            // for later retrieval by the thin client.
            foreach (SegmentedStructure structure in result.Structures)
            {
                string maskToken = Guid.NewGuid().ToString("N");

                // If the plugin produced per-structure NIfTI files, convert them to
                // packed-bit masks aligned to the original volume grid.
                if (!string.IsNullOrEmpty(structure.MaskPath) && File.Exists(structure.MaskPath))
                {
                    byte[] packedBits = await Task.Run(
                        () => ConvertNiftiMaskToPackedBits(structure.MaskPath, volume), ct);

                    _maskStore[maskToken] = new MaskDataEntry(
                        packedBits, volume, DateTimeOffset.UtcNow);
                }
                else if (!string.IsNullOrEmpty(result.MultilabelPath) && File.Exists(result.MultilabelPath))
                {
                    // Extract this structure's label from the multilabel volume.
                    byte[] packedBits = await Task.Run(
                        () => ExtractLabelFromMultilabel(result.MultilabelPath, structure.Label, volume), ct);

                    _maskStore[maskToken] = new MaskDataEntry(
                        packedBits, volume, DateTimeOffset.UtcNow);
                }

                var structMsg = new ProxySegStructure
                {
                    Label = structure.Label,
                    Id = structure.Id,
                    DisplayName = structure.DisplayName ?? string.Empty,
                    Region = structure.Region ?? string.Empty,
                    VolumeMm3 = structure.VolumeMm3,
                    MaskToken = maskToken,
                };

                if (structure.BoundingBoxVoxels is not null)
                {
                    structMsg.BoundingBoxVoxels.AddRange(structure.BoundingBoxVoxels);
                }

                await responseStream.WriteAsync(new ProxySegmentationEvent
                {
                    Structure = structMsg,
                }, ct);
            }

            // Final completion event.
            await responseStream.WriteAsync(new ProxySegmentationEvent
            {
                Complete = new ProxySegComplete
                {
                    ElapsedSeconds = result.ElapsedSeconds,
                    StructureCount = result.Structures.Count,
                    MultilabelToken = string.Empty, // multilabel stays on server for now
                },
            }, ct);

            _logger.LogInformation(
                "Plugin proxy segmentation '{PluginId}/{TaskId}' completed in {Elapsed:F1}s — {Count} structures.",
                request.PluginId, request.TaskId, result.ElapsedSeconds, result.Structures.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Plugin proxy segmentation cancelled by client.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin proxy segmentation failed.");
            await responseStream.WriteAsync(new ProxySegmentationEvent
            {
                Error = new ProxySegError { Message = ex.Message },
            }, ct);
        }
        finally
        {
            // Clean up temp files (best-effort).
            _ = Task.Run(() =>
            {
                try { Directory.Delete(outputDir, recursive: true); }
                catch { /* best-effort */ }
            });
        }
    }

    // ── GetMaskData ────────────────────────────────────────────

    public override Task<GetMaskDataResponse> GetMaskData(
        GetMaskDataRequest request, ServerCallContext context)
    {
        if (!_maskStore.TryRemove(request.MaskToken, out MaskDataEntry? entry))
        {
            return Task.FromResult(new GetMaskDataResponse
            {
                Success = false,
                ErrorMessage = $"Mask token '{request.MaskToken}' not found or already consumed.",
            });
        }

        SeriesVolume vol = entry.Volume;

        var geometry = new ProxyVolumeGrid
        {
            SizeX = vol.SizeX,
            SizeY = vol.SizeY,
            SizeZ = vol.SizeZ,
            SpacingX = vol.SpacingX,
            SpacingY = vol.SpacingY,
            SpacingZ = vol.SpacingZ,
            Origin = new Vec3 { X = vol.Origin.X, Y = vol.Origin.Y, Z = vol.Origin.Z },
            RowDirection = new Vec3 { X = vol.RowDirection.X, Y = vol.RowDirection.Y, Z = vol.RowDirection.Z },
            ColumnDirection = new Vec3 { X = vol.ColumnDirection.X, Y = vol.ColumnDirection.Y, Z = vol.ColumnDirection.Z },
            Normal = new Vec3 { X = vol.Normal.X, Y = vol.Normal.Y, Z = vol.Normal.Z },
            FrameOfReferenceUid = vol.FrameOfReferenceUid,
        };

        int foreground = CountBits(entry.PackedBits);

        return Task.FromResult(new GetMaskDataResponse
        {
            Success = true,
            Data = Google.Protobuf.ByteString.CopyFrom(entry.PackedBits),
            Geometry = geometry,
            ForegroundCount = foreground,
        });
    }

    // ── NIfTI conversion helpers ───────────────────────────────

    /// <summary>
    /// Write a <see cref="SeriesVolume"/> as a minimal NIfTI-1 .nii.gz file
    /// so that plugins receive a standard neuroimaging input format.
    /// </summary>
    private static void ExportVolumeAsNifti(SeriesVolume volume, string outputPath)
    {
        using FileStream fs = File.Create(outputPath);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        using var bw = new BinaryWriter(gz);

        int nx = volume.SizeX, ny = volume.SizeY, nz = volume.SizeZ;

        // NIfTI-1 header (348 bytes).
        bw.Write(348);              // sizeof_hdr
        bw.Write(new byte[28]);     // data_type (10) + db_name (18)
        bw.Write(0);                // extents
        bw.Write((short)0);         // session_error
        bw.Write((byte)'r');        // regular
        bw.Write((byte)0);          // dim_info

        // dim[0..7]
        bw.Write((short)3);         // ndim
        bw.Write((short)nx);
        bw.Write((short)ny);
        bw.Write((short)nz);
        bw.Write((short)1); bw.Write((short)1); bw.Write((short)1); bw.Write((short)1);

        bw.Write(0f); bw.Write(0f); bw.Write(0f); // intent_p1/2/3
        bw.Write((short)0);         // intent_code
        bw.Write((short)4);         // datatype = INT16
        bw.Write((short)16);        // bitpix
        bw.Write((short)0);         // slice_start

        // pixdim[0..7]
        bw.Write(1f);               // qfac
        bw.Write((float)volume.SpacingX);
        bw.Write((float)volume.SpacingY);
        bw.Write((float)volume.SpacingZ);
        bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f);

        bw.Write(352f);             // vox_offset
        bw.Write(1f);               // scl_slope
        bw.Write(0f);               // scl_inter
        bw.Write((short)0);         // slice_end
        bw.Write((byte)0);          // slice_code
        bw.Write((byte)2);          // xyzt_units = NIFTI_UNITS_MM

        bw.Write(0f); bw.Write(0f); // cal_max, cal_min
        bw.Write(0f); bw.Write(0f); // slice_duration, toffset
        bw.Write(0); bw.Write(0);   // glmax, glmin
        bw.Write(new byte[80]);     // descrip
        bw.Write(new byte[24]);     // aux_file

        // Use sform (method 2) to encode the full affine.
        bw.Write((short)0);         // qform_code = 0 (unknown)
        bw.Write((short)1);         // sform_code = 1 (scanner anat)

        // quatern (unused since qform_code = 0)
        bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(0f); bw.Write(0f); bw.Write(0f);

        // srow_x, srow_y, srow_z (4 floats each = 48 bytes)
        //
        // DICOM uses LPS (Left-Posterior-Superior) patient coordinates;
        // NIfTI expects RAS (Right-Anterior-Superior).  Convert by
        // negating the X and Y rows of the affine matrix.
        SpatialVector3D row = volume.RowDirection;
        SpatialVector3D col = volume.ColumnDirection;
        SpatialVector3D nrm = volume.Normal;
        SpatialVector3D orig = volume.Origin;

        // srow_x  (RAS X = −LPS X)
        bw.Write((float)(-row.X * volume.SpacingX));
        bw.Write((float)(-col.X * volume.SpacingY));
        bw.Write((float)(-nrm.X * volume.SpacingZ));
        bw.Write((float)(-orig.X));
        // srow_y  (RAS Y = −LPS Y)
        bw.Write((float)(-row.Y * volume.SpacingX));
        bw.Write((float)(-col.Y * volume.SpacingY));
        bw.Write((float)(-nrm.Y * volume.SpacingZ));
        bw.Write((float)(-orig.Y));
        // srow_z  (RAS Z = LPS Z — unchanged)
        bw.Write((float)(row.Z * volume.SpacingX));
        bw.Write((float)(col.Z * volume.SpacingY));
        bw.Write((float)(nrm.Z * volume.SpacingZ));
        bw.Write((float)orig.Z);

        bw.Write(new byte[16]);     // intent_name
        // magic: "n+1\0" — written directly (GZipStream does not support Seek).
        bw.Write((byte)'n'); bw.Write((byte)'+'); bw.Write((byte)'1'); bw.Write((byte)0);

        // 4-byte extension pad.
        bw.Write(new byte[4]);

        // Voxel data (INT16, x-fastest).
        // NIfTI uses x-fastest order which matches our volume indexing.
        for (int z = 0; z < nz; z++)
        {
            int sliceBase = z * ny * nx;
            for (int y = 0; y < ny; y++)
            {
                int rowBase = sliceBase + y * nx;
                for (int x = 0; x < nx; x++)
                {
                    bw.Write(volume.Voxels[rowBase + x]);
                }
            }
        }
    }

    /// <summary>
    /// Convert a per-structure binary NIfTI mask into a packed-bit array
    /// aligned to the original volume grid.
    /// </summary>
    private static byte[] ConvertNiftiMaskToPackedBits(string niftiPath, SeriesVolume volume)
    {
        int nx = volume.SizeX, ny = volume.SizeY, nz = volume.SizeZ;
        long totalVoxels = (long)nx * ny * nz;
        int byteCount = (int)((totalVoxels + 7) / 8);
        byte[] packed = new byte[byteCount];

        short[] maskData = ReadNiftiInt16(niftiPath, nx, ny, nz);

        for (int i = 0; i < totalVoxels && i < maskData.Length; i++)
        {
            if (maskData[i] > 0)
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return packed;
    }

    /// <summary>
    /// Extract one label from a multilabel NIfTI volume into a packed-bit mask.
    /// </summary>
    private static byte[] ExtractLabelFromMultilabel(
        string multilabelPath, int label, SeriesVolume volume)
    {
        int nx = volume.SizeX, ny = volume.SizeY, nz = volume.SizeZ;
        long totalVoxels = (long)nx * ny * nz;
        int byteCount = (int)((totalVoxels + 7) / 8);
        byte[] packed = new byte[byteCount];

        short[] data = ReadNiftiInt16(multilabelPath, nx, ny, nz);

        for (int i = 0; i < totalVoxels && i < data.Length; i++)
        {
            if (data[i] == label)
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return packed;
    }

    /// <summary>
    /// Minimal NIfTI reader — reads the voxel payload as INT16.
    /// Supports .nii and .nii.gz.
    /// </summary>
    private static short[] ReadNiftiInt16(string path, int expectedX, int expectedY, int expectedZ)
    {
        using Stream fs = File.OpenRead(path);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress)
            : fs;

        using var br = new BinaryReader(stream);

        // Read header.
        int headerSize = br.ReadInt32(); // sizeof_hdr (348 for NIfTI-1)
        if (headerSize != 348)
        {
            throw new InvalidDataException($"Unexpected NIfTI header size: {headerSize}");
        }

        br.ReadBytes(36);           // skip to dim[0] at offset 40
        short ndim = br.ReadInt16();
        short dimX = br.ReadInt16();
        short dimY = br.ReadInt16();
        short dimZ = br.ReadInt16();
        br.ReadBytes(8);            // skip remaining dims

        br.ReadBytes(12);           // skip intent_p1/2/3
        br.ReadInt16();             // intent_code
        short datatype = br.ReadInt16();
        short bitpix = br.ReadInt16();

        // Read vox_offset.
        br.ReadInt16();             // slice_start
        br.ReadBytes(32);           // pixdim[0..7]
        float voxOffset = br.ReadSingle();
        float sclSlope = br.ReadSingle();
        float sclInter = br.ReadSingle();

        // Seek to voxel data.
        // We need to read from the beginning of the stream, so reset via counting bytes read.
        int headerBytesRead = 4 + 36 + 2 + 2 + 2 + 2 + 8 + 12 + 2 + 2 + 2 + 2 + 32 + 4 + 4 + 4;
        int skipBytes = (int)voxOffset - headerBytesRead;
        if (skipBytes > 0)
        {
            br.ReadBytes(skipBytes);
        }

        // Read voxel data.
        long voxelCount = (long)dimX * dimY * dimZ;
        short[] result = new short[voxelCount];

        int bytesPerVoxel = bitpix / 8;

        if (datatype == 4) // INT16
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = br.ReadInt16();
            }
        }
        else if (datatype == 2) // UINT8
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = br.ReadByte();
            }
        }
        else if (datatype == 8) // INT32
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = (short)Math.Clamp(br.ReadInt32(), short.MinValue, short.MaxValue);
            }
        }
        else if (datatype == 16) // FLOAT32
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = (short)Math.Clamp(br.ReadSingle(), short.MinValue, short.MaxValue);
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported NIfTI datatype: {datatype}");
        }

        // Apply slope/intercept if present.
        if (sclSlope != 0 && sclSlope != 1)
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = (short)Math.Clamp(result[i] * sclSlope + sclInter, short.MinValue, short.MaxValue);
            }
        }

        return result;
    }

    private static int CountBits(byte[] data)
    {
        int total = 0;
        foreach (byte b in data)
        {
            total += System.Numerics.BitOperations.PopCount(b);
        }

        return total;
    }

    private static PluginProxyState MapState(PluginState state) => state switch
    {
        PluginState.Discovered => PluginProxyState.Discovered,
        PluginState.Starting => PluginProxyState.Starting,
        PluginState.Ready => PluginProxyState.Ready,
        PluginState.Busy => PluginProxyState.Busy,
        PluginState.Faulted => PluginProxyState.Faulted,
        PluginState.Stopped => PluginProxyState.Stopped,
        _ => PluginProxyState.Discovered,
    };

    // ── Mask data entry ────────────────────────────────────────

    private sealed record MaskDataEntry(
        byte[] PackedBits,
        SeriesVolume Volume,
        DateTimeOffset CreatedUtc);
}
