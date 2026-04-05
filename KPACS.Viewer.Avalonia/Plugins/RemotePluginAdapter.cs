using Grpc.Core;
using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.SDK;
using KPACS.SDK.Contracts;
using KPACS.SDK.Models;
using KPACS.Viewer.Models;

using SdkSegmentationRequest = KPACS.SDK.Models.SegmentationRequest;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Plugin adapter that forwards all calls to a remote K-PACS Render Server's
/// <see cref="PluginProxyService"/>. This allows thin clients to run plugins
/// on the server without any local plugin process or GPU.
/// </summary>
internal sealed class RemotePluginAdapter : IPlugin, ISegmentationProvider, IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly GrpcChannel _channel;
    private readonly PluginProxyService.PluginProxyServiceClient _client;
    private PluginState _state = PluginState.Ready;

    // Cached task list — populated lazily.
    private IReadOnlyList<SegmentationTaskInfo>? _segTasks;

    /// <summary>Render-server session ID used to resolve volumes.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Volume ID of the currently loaded volume in the session.</summary>
    public string VolumeId { get; set; } = string.Empty;

    public RemotePluginAdapter(PluginManifest manifest, GrpcChannel channel)
    {
        _manifest = manifest;
        _channel = channel;
        _client = new PluginProxyService.PluginProxyServiceClient(channel);
    }

    // ── IPlugin ─────────────────────────────────────────────────

    public string Id => _manifest.Id;
    public PluginManifest Manifest => _manifest;
    public PluginState State => _state;

    public Task InitializeAsync(PluginHostContext context, CancellationToken cancellationToken = default)
    {
        // Remote plugins are managed by the render server — no local init needed.
        _state = PluginState.Ready;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _state = PluginState.Stopped;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Do NOT dispose the channel — it is shared with the render session.
        return ValueTask.CompletedTask;
    }

    // ── ISegmentationProvider ───────────────────────────────────

    public IReadOnlyList<SegmentationTaskInfo> AvailableTasks
    {
        get
        {
            if (_segTasks is not null)
            {
                return _segTasks;
            }

            // Fallback to manifest entries.
            if (_manifest.SegmentationTasks is not null)
            {
                _segTasks = _manifest.SegmentationTasks
                    .Select(e => new SegmentationTaskInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Description = e.Description,
                        SupportedModalities = e.SupportedModalities,
                        StructureCount = e.StructureCount,
                        RequiresLicense = e.RequiresLicense,
                    })
                    .ToList();
                return _segTasks;
            }

            return [];
        }
    }

    /// <summary>
    /// Fetch detailed task catalogue from the render server. Call this
    /// once after the adapter is created to populate <see cref="AvailableTasks"/>.
    /// </summary>
    public async Task RefreshTaskCatalogAsync(CancellationToken ct = default)
    {
        ProxyGetSegTasksResponse response = await _client.GetSegmentationTasksAsync(
            new ProxyGetSegTasksRequest { PluginId = Id }, cancellationToken: ct);

        _segTasks = response.Tasks.Select(t => new SegmentationTaskInfo
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            SupportedModalities = [.. t.Modalities],
            StructureCount = t.StructureCount,
            RequiresLicense = t.RequiresLicense,
            Structures = t.Structures.Select(s => new StructureCatalogEntry
            {
                Label = s.Label,
                Id = s.Id,
                DisplayName = s.DisplayName,
                Region = s.Region,
            }).ToList(),
        }).ToList();
    }

    public async Task<SegmentationResult> RunAsync(
        SdkSegmentationRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var grpcRequest = new ProxySegmentationRequest
        {
            SessionId = SessionId,
            VolumeId = VolumeId,
            PluginId = Id,
            TaskId = request.TaskId,
            Device = request.Device,
            ProduceMultilabel = request.ProduceMultilabel,
        };

        if (request.RoiSubset is not null)
        {
            grpcRequest.RoiSubset.AddRange(request.RoiSubset);
        }

        foreach ((string key, string value) in request.Parameters)
        {
            grpcRequest.Parameters.Add(key, value);
        }

        using var call = _client.RunSegmentation(grpcRequest, cancellationToken: cancellationToken);

        var structures = new List<SegmentedStructure>();
        double elapsed = 0;

        await foreach (ProxySegmentationEvent evt in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            switch (evt.EventCase)
            {
                case ProxySegmentationEvent.EventOneofCase.Progress:
                    progress?.Report(new ProgressReport
                    {
                        Step = evt.Progress.Step,
                        TotalSteps = evt.Progress.TotalSteps,
                        PercentComplete = evt.Progress.PercentComplete,
                        StatusMessage = evt.Progress.StatusMessage,
                    });
                    break;

                case ProxySegmentationEvent.EventOneofCase.Structure:
                    structures.Add(new SegmentedStructure
                    {
                        Label = evt.Structure.Label,
                        Id = evt.Structure.Id,
                        DisplayName = evt.Structure.DisplayName,
                        Region = evt.Structure.Region,
                        VolumeMm3 = evt.Structure.VolumeMm3,
                        BoundingBoxVoxels = evt.Structure.BoundingBoxVoxels.Count > 0
                            ? [.. evt.Structure.BoundingBoxVoxels]
                            : null,
                        // Store the mask_token in the MaskPath field — the caller
                        // will use DownloadMaskAsync to fetch the actual bits.
                        MaskPath = evt.Structure.MaskToken,
                    });
                    break;

                case ProxySegmentationEvent.EventOneofCase.Complete:
                    elapsed = evt.Complete.ElapsedSeconds;
                    break;

                case ProxySegmentationEvent.EventOneofCase.Error:
                    return new SegmentationResult
                    {
                        Success = false,
                        ErrorMessage = evt.Error.Message,
                        Structures = structures,
                    };
            }
        }

        return new SegmentationResult
        {
            Success = true,
            Structures = structures,
            ElapsedSeconds = elapsed,
        };
    }

    // ── Mask download ───────────────────────────────────────────

    /// <summary>
    /// Download a packed-bit mask from the render server using a token
    /// returned in <see cref="SegmentedStructure.MaskPath"/>.
    /// </summary>
    public async Task<SegmentationMask3D?> DownloadMaskAsync(
        string maskToken,
        string structureName,
        string seriesInstanceUid,
        string studyInstanceUid,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(maskToken))
        {
            return null;
        }

        GetMaskDataResponse response = await _client.GetMaskDataAsync(
            new GetMaskDataRequest { MaskToken = maskToken, Encoding = "bit-packed" },
            cancellationToken: ct);

        if (!response.Success || response.Geometry is null)
        {
            return null;
        }

        ProxyVolumeGrid g = response.Geometry;

        var geometry = new VolumeGridGeometry(
            g.SizeX, g.SizeY, g.SizeZ,
            g.SpacingX, g.SpacingY, g.SpacingZ,
            new Vector3D(g.Origin.X, g.Origin.Y, g.Origin.Z),
            new Vector3D(g.RowDirection.X, g.RowDirection.Y, g.RowDirection.Z),
            new Vector3D(g.ColumnDirection.X, g.ColumnDirection.Y, g.ColumnDirection.Z),
            new Vector3D(g.Normal.X, g.Normal.Y, g.Normal.Z),
            g.FrameOfReferenceUid);

        byte[] maskData = response.Data.ToByteArray();
        var storage = new SegmentationMaskStorage(
            SegmentationMaskStorageKind.PackedBits,
            response.ForegroundCount,
            "bit-packed",
            maskData);

        var metadata = new SegmentationMaskMetadata(
            SegmentationMaskSourceKind.Imported,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            sourceMeasurementId: null,
            notes: $"Plugin: {Id}",
            revision: 0);

        return new SegmentationMask3D(
            Guid.NewGuid(),
            structureName,
            seriesInstanceUid,
            g.FrameOfReferenceUid,
            studyInstanceUid,
            geometry,
            storage,
            metadata);
    }
}
