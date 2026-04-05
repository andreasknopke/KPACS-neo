using KPACS.SDK.Models;

namespace KPACS.SDK.Contracts;

/// <summary>
/// Contract for plugins that produce labelled volumetric segmentation masks
/// (e.g. TotalSegmentator, lung-nodule detectors, cardiac chamber segmentation).
/// </summary>
public interface ISegmentationProvider
{
    /// <summary>Tasks this provider supports (mirrors <see cref="PluginManifest.SegmentationTasks"/>).</summary>
    IReadOnlyList<SegmentationTaskInfo> AvailableTasks { get; }

    /// <summary>
    /// Run segmentation on a volume.
    /// The result is streamed back — progress events first, then one
    /// <see cref="SegmentationResult"/> when complete.
    /// </summary>
    /// <param name="request">Describes the input volume, task, and runtime options.</param>
    /// <param name="progress">Optional progress sink (percentage + status text).</param>
    /// <param name="cancellationToken">Cancellation support — the host cancels when the user
    /// navigates away or explicitly aborts.</param>
    Task<SegmentationResult> RunAsync(
        SegmentationRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Detailed description of a segmentation task (richer than the manifest entry).
/// </summary>
public sealed class SegmentationTaskInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> SupportedModalities { get; init; } = [];
    public int StructureCount { get; init; }
    public bool RequiresLicense { get; init; }

    /// <summary>
    /// Full catalogue of structures this task can produce, with their integer labels.
    /// </summary>
    public IReadOnlyList<StructureCatalogEntry> Structures { get; init; } = [];
}

public sealed class StructureCatalogEntry
{
    /// <summary>Integer label in the multilabel mask volume.</summary>
    public int Label { get; init; }

    /// <summary>Machine-readable structure name (e.g. "liver", "kidney_right").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name (e.g. "Liver", "Right Kidney").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Anatomy region this structure belongs to (e.g. "Upper abdomen").</summary>
    public string? Region { get; init; }
}
