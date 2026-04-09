// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - PluginProxyServiceImpl
//
// gRPC service that exposes server-side plugins to thin clients.
// The render server discovers and manages plugins locally; thin clients
// invoke them transparently over the existing render-server channel.
// ------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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
            // Publish the volume through a named shared-memory region so the
            // local plugin sidecar can read it directly without temp files.
            using SharedRawVolumeHandle sharedVolume = await Task.Run(() => CreateSharedRawVolume(volume), ct);
            Dictionary<string, string> requestParameters = CreateRawVolumeParameters(volume);
            requestParameters["kpacs.raw.transport"] = "shm";
            requestParameters["kpacs.raw.map_name"] = sharedVolume.MapName;

            await responseStream.WriteAsync(new ProxySegmentationEvent
            {
                Progress = new ProxySegProgress
                {
                    Step = 0, TotalSteps = 4, PercentComplete = 5,
                    StatusMessage = "Shared-memory volume published — starting plugin…",
                },
            }, ct);

            // Build the SDK request.
            var sdkRequest = new SegmentationRequest
            {
                Volume = new VolumeDescriptor
                {
                    FilePath = sharedVolume.MapName,
                    Format = "raw",
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
                Parameters = request.Parameters
                    .Concat(requestParameters)
                    .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
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
            int structureIndex = 0;
            int structureCount = Math.Max(1, result.Structures.Count);
            foreach (SegmentedStructure structure in result.Structures)
            {
                structureIndex++;
                int convertPercent = 82 + (structureIndex * 16 / structureCount);
                await responseStream.WriteAsync(new ProxySegmentationEvent
                {
                    Progress = new ProxySegProgress
                    {
                        Step = 3,
                        TotalSteps = 4,
                        PercentComplete = convertPercent,
                        StatusMessage = $"Converting mask {structureIndex}/{structureCount}…",
                    },
                }, ct);

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

    // ── Volume / NIfTI helpers ────────────────────────────────

    private static Dictionary<string, string> CreateRawVolumeParameters(SeriesVolume volume)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kpacs.raw.dtype"] = "int16-le",
            ["kpacs.raw.origin_lps"] = FormatRawVector(volume.Origin.X, volume.Origin.Y, volume.Origin.Z),
            ["kpacs.raw.row_lps"] = FormatRawVector(volume.RowDirection.X, volume.RowDirection.Y, volume.RowDirection.Z),
            ["kpacs.raw.column_lps"] = FormatRawVector(volume.ColumnDirection.X, volume.ColumnDirection.Y, volume.ColumnDirection.Z),
            ["kpacs.raw.normal_lps"] = FormatRawVector(volume.Normal.X, volume.Normal.Y, volume.Normal.Z),
        };
    }

    private static string FormatRawVector(double x, double y, double z) =>
        string.Join(";",
            x.ToString("R", CultureInfo.InvariantCulture),
            y.ToString("R", CultureInfo.InvariantCulture),
            z.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Publish the volume through a named shared-memory mapping.
    /// The plugin opens the same mapping by name and wraps it as a NumPy array.
    /// </summary>
    private static SharedRawVolumeHandle CreateSharedRawVolume(SeriesVolume volume)
    {
        if (!string.IsNullOrWhiteSpace(volume.SharedRawMapName))
        {
            return new SharedRawVolumeHandle(volume.SharedRawMapName, mapping: null);
        }

        short[] voxelArray = volume.GetVoxelsArrayForInterop();
        long capacityBytes = checked((long)voxelArray.Length * sizeof(short));
        string mapName = $@"Local\kpacs-seg-{Guid.NewGuid():N}";
        MemoryMappedFile mmf = MemoryMappedFile.CreateNew(
            mapName,
            capacityBytes,
            MemoryMappedFileAccess.ReadWrite,
            MemoryMappedFileOptions.None,
            HandleInheritability.None);

        using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, capacityBytes, MemoryMappedFileAccess.Write))
        {
            accessor.WriteArray(0, voxelArray, 0, voxelArray.Length);
            accessor.Flush();
        }

        return new SharedRawVolumeHandle(mapName, mmf);
    }

    /// <summary>
    /// Write a <see cref="SeriesVolume"/> as a raw little-endian INT16 file.
    /// The plugin reconstructs the NIfTI image in memory from this payload.
    /// </summary>
    private static void ExportVolumeAsRawInt16(SeriesVolume volume, string outputPath)
    {
        using FileStream fs = File.Create(outputPath);
        short[] voxels = volume.GetVoxelsArrayForInterop();
        fs.Write(MemoryMarshal.AsBytes<short>(voxels.AsSpan()));
    }

    /// <summary>
    /// Convert a per-structure binary NIfTI mask into a packed-bit array
    /// aligned with the original source volume.
    /// </summary>
    private static byte[] ConvertNiftiMaskToPackedBits(string niftiPath, SeriesVolume volume)
    {
        int nx = volume.SizeX, ny = volume.SizeY, nz = volume.SizeZ;
        short[] maskData = ReadNiftiInt16(niftiPath, nx, ny, nz);

        long totalVoxels = (long)nx * ny * nz;
        int byteCount = (int)((totalVoxels + 7) / 8);
        byte[] packed = new byte[byteCount];

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

    private sealed class SharedRawVolumeHandle : IDisposable
    {
        private readonly MemoryMappedFile? _mapping;

        public SharedRawVolumeHandle(string mapName, MemoryMappedFile? mapping)
        {
            MapName = mapName;
            _mapping = mapping;
        }

        public string MapName { get; }

        public void Dispose()
        {
            _mapping?.Dispose();
        }
    }

    private sealed record MaskDataEntry(
        byte[] PackedBits,
        SeriesVolume Volume,
        DateTimeOffset CreatedUtc);
}
