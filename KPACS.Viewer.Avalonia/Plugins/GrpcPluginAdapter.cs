using Grpc.Core;
using Grpc.Net.Client;
using KPACS.SDK;
using KPACS.SDK.Contracts;
using KPACS.SDK.Grpc;
using KPACS.SDK.Models;

// Aliases to disambiguate types that exist in both SDK.Models and SDK.Grpc.
using SdkSegmentationRequest = KPACS.SDK.Models.SegmentationRequest;
using SdkDicomAnalysisRequest = KPACS.SDK.Models.DicomAnalysisRequest;
using SdkDicomAnalysisInfo = KPACS.SDK.Contracts.DicomAnalysisInfo;

namespace KPACS.Viewer.Plugins;

/// <summary>
/// Adapts an out-of-process gRPC plugin into the in-process SDK interfaces.
/// The host creates one <see cref="GrpcPluginAdapter"/> per running plugin.
/// It implements <see cref="IPlugin"/> plus every capability interface that
/// the manifest declares, bridging calls to the child process.
/// </summary>
internal sealed class GrpcPluginAdapter : IPlugin, ISegmentationProvider, IImageProcessor, IDicomAnalyzer
{
    private readonly PluginManifest _manifest;
    private readonly ProcessPluginHost _processHost;
    private GrpcChannel? _channel;
    private PluginService.PluginServiceClient? _client;
    private PluginState _state = PluginState.Starting;

    // Cached task lists — populated lazily on first query.
    private IReadOnlyList<SegmentationTaskInfo>? _segTasks;
    private IReadOnlyList<ImageOperationInfo>? _imageOps;
    private IReadOnlyList<SdkDicomAnalysisInfo>? _dicomAnalyses;

    public GrpcPluginAdapter(PluginManifest manifest, ProcessPluginHost processHost)
    {
        _manifest = manifest;
        _processHost = processHost;
    }

    // ── IPlugin ─────────────────────────────────────────────────

    public string Id => _manifest.Id;
    public PluginManifest Manifest => _manifest;
    public PluginState State => _state;

    public async Task InitializeAsync(PluginHostContext context, CancellationToken cancellationToken = default)
    {
        _channel = GrpcChannel.ForAddress($"http://localhost:{_processHost.Port}");
        _client = new PluginService.PluginServiceClient(_channel);

        var request = new InitializeRequest
        {
            ScratchDirectory = context.ScratchDirectory,
            DataDirectory = context.DataDirectory ?? string.Empty,
            HostVersion = context.HostVersion ?? string.Empty,
        };

        foreach ((string key, string value) in context.Extra)
        {
            request.Extra.Add(key, value);
        }

        InitializeResponse response = await _client.InitializeAsync(request, cancellationToken: cancellationToken);

        if (!response.Ok)
        {
            _state = PluginState.Faulted;
            throw new InvalidOperationException(
                $"Plugin '{Id}' initialization failed: {response.ErrorMessage}");
        }

        _state = PluginState.Ready;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            try
            {
                await _client.ShutdownAsync(new ShutdownRequest(), cancellationToken: cancellationToken);
            }
            catch
            {
                // Best-effort — the process might already be gone.
            }
        }

        _state = PluginState.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            _channel.Dispose();
            _channel = null;
        }

        await _processHost.DisposeAsync();
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

            // Fallback to manifest entries if gRPC hasn't been queried yet.
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

    public async Task<SegmentationResult> RunAsync(
        SdkSegmentationRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();

        var grpcRequest = new KPACS.SDK.Grpc.SegmentationRequest
        {
            Volume = ToProto(request.Volume),
            TaskId = request.TaskId,
            OutputDirectory = request.OutputDirectory,
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

        using var call = _client!.RunSegmentation(grpcRequest, cancellationToken: cancellationToken);

        var structures = new List<SegmentedStructure>();
        string? multilabelPath = null;
        double elapsed = 0;

        await foreach (SegmentationEvent evt in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            switch (evt.EventCase)
            {
                case SegmentationEvent.EventOneofCase.Progress:
                    progress?.Report(new ProgressReport
                    {
                        Step = evt.Progress.Step,
                        TotalSteps = evt.Progress.TotalSteps,
                        PercentComplete = evt.Progress.PercentComplete,
                        StatusMessage = evt.Progress.StatusMessage,
                    });
                    break;

                case SegmentationEvent.EventOneofCase.Structure:
                    structures.Add(new SegmentedStructure
                    {
                        Label = evt.Structure.Label,
                        Id = evt.Structure.Id,
                        DisplayName = evt.Structure.DisplayName,
                        Region = evt.Structure.Region,
                        MaskPath = evt.Structure.MaskPath,
                        VolumeMm3 = evt.Structure.VolumeMm3,
                        BoundingBoxVoxels = evt.Structure.BoundingBoxVoxels.Count > 0
                            ? [.. evt.Structure.BoundingBoxVoxels]
                            : null,
                    });
                    break;

                case SegmentationEvent.EventOneofCase.Complete:
                    multilabelPath = evt.Complete.MultilabelPath;
                    elapsed = evt.Complete.ElapsedSeconds;
                    break;

                case SegmentationEvent.EventOneofCase.Error:
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
            MultilabelPath = multilabelPath,
            Structures = structures,
            ElapsedSeconds = elapsed,
        };
    }

    // ── IImageProcessor ─────────────────────────────────────────

    public IReadOnlyList<ImageOperationInfo> AvailableOperations
    {
        get
        {
            if (_imageOps is not null)
            {
                return _imageOps;
            }

            if (_manifest.ImageOperations is not null)
            {
                _imageOps = _manifest.ImageOperations
                    .Select(e => new ImageOperationInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Description = e.Description,
                        SupportsVolumetric = e.SupportsVolumetric,
                    })
                    .ToList();
                return _imageOps;
            }

            return [];
        }
    }

    public async Task<ImageProcessingResult> ProcessAsync(
        ImageProcessingRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();

        var grpcRequest = new ImageProcessRequest
        {
            Input = ToProto(request.Input),
            OperationId = request.OperationId,
            OutputPath = request.OutputPath ?? string.Empty,
            Device = request.Device,
        };

        foreach ((string key, string value) in request.Parameters)
        {
            grpcRequest.Parameters.Add(key, value);
        }

        ImageProcessResponse response = await _client!.ProcessImageAsync(grpcRequest, cancellationToken: cancellationToken);

        return new ImageProcessingResult
        {
            Success = response.Success,
            ErrorMessage = string.IsNullOrEmpty(response.ErrorMessage) ? null : response.ErrorMessage,
            OutputPath = string.IsNullOrEmpty(response.OutputPath) ? null : response.OutputPath,
            Metrics = response.Metrics.ToDictionary(kv => kv.Key, kv => kv.Value),
            ElapsedSeconds = response.ElapsedSeconds,
        };
    }

    // ── IDicomAnalyzer ──────────────────────────────────────────

    public IReadOnlyList<SdkDicomAnalysisInfo> AvailableAnalyses
    {
        get
        {
            if (_dicomAnalyses is not null)
            {
                return _dicomAnalyses;
            }

            if (_manifest.DicomAnalyses is not null)
            {
                _dicomAnalyses = _manifest.DicomAnalyses
                    .Select(e => new SdkDicomAnalysisInfo
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Description = e.Description,
                    })
                    .ToList();
                return _dicomAnalyses;
            }

            return [];
        }
    }

    public async Task<DicomAnalysisResult> AnalyzeAsync(
        SdkDicomAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();

        var grpcRequest = new KPACS.SDK.Grpc.DicomAnalysisRequest
        {
            AnalysisId = request.AnalysisId,
            PixelDataPath = request.PixelDataPath ?? string.Empty,
        };

        foreach ((string key, string value) in request.Tags)
        {
            grpcRequest.Tags.Add(key, value);
        }

        KPACS.SDK.Grpc.DicomAnalysisResponse response =
            await _client!.AnalyzeDicomAsync(grpcRequest, cancellationToken: cancellationToken);

        return new DicomAnalysisResult
        {
            Success = response.Success,
            ErrorMessage = string.IsNullOrEmpty(response.ErrorMessage) ? null : response.ErrorMessage,
            Findings = response.Findings.ToDictionary(kv => kv.Key, kv => kv.Value),
            Classification = string.IsNullOrEmpty(response.Classification) ? null : response.Classification,
            Confidence = response.Confidence > 0 ? response.Confidence : null,
            ElapsedSeconds = response.ElapsedSeconds,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void EnsureClient()
    {
        if (_client is null)
        {
            throw new InvalidOperationException($"Plugin '{Id}' is not initialized.");
        }
    }

    private static VolumeDescriptorMsg ToProto(VolumeDescriptor vol)
    {
        var msg = new VolumeDescriptorMsg
        {
            FilePath = vol.FilePath,
            Format = vol.Format,
            Modality = vol.Modality ?? string.Empty,
            SeriesInstanceUid = vol.SeriesInstanceUid ?? string.Empty,
        };

        if (vol.Dimensions is not null)
        {
            msg.Dimensions.AddRange(vol.Dimensions);
        }

        if (vol.SpacingMm is not null)
        {
            msg.SpacingMm.AddRange(vol.SpacingMm);
        }

        return msg;
    }
}
