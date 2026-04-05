namespace KPACS.SDK.Models;

/// <summary>
/// Request to run a segmentation task on a volume.
/// </summary>
public sealed class SegmentationRequest
{
    /// <summary>Volume to segment.</summary>
    public required VolumeDescriptor Volume { get; init; }

    /// <summary>Task identifier (e.g. "total", "total_fast", "lung_vessels").</summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// Directory where the plugin should write output files (masks, statistics).
    /// The host creates and cleans up this directory.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Preferred compute device: "gpu", "cpu", "gpu:0", etc.</summary>
    public string Device { get; init; } = "gpu";

    /// <summary>
    /// Optional: restrict output to these structure names.
    /// Empty or null = produce all structures for the task.
    /// </summary>
    public IReadOnlyList<string>? RoiSubset { get; init; }

    /// <summary>
    /// Whether to produce a single multilabel NIfTI file
    /// in addition to (or instead of) per-structure binary masks.
    /// </summary>
    public bool ProduceMultilabel { get; init; } = true;

    /// <summary>Arbitrary extra parameters (task-specific).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Result returned after segmentation completes.
/// </summary>
public sealed class SegmentationResult
{
    /// <summary>Whether the segmentation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable error message when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the multilabel NIfTI file (if requested).</summary>
    public string? MultilabelPath { get; init; }

    /// <summary>Per-structure results.</summary>
    public IReadOnlyList<SegmentedStructure> Structures { get; init; } = [];

    /// <summary>Wall-clock time in seconds.</summary>
    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// One segmented anatomical structure.
/// </summary>
public sealed class SegmentedStructure
{
    /// <summary>Integer label in the multilabel volume (0 = background).</summary>
    public int Label { get; init; }

    /// <summary>Machine-readable name (e.g. "liver").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name (e.g. "Liver").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Anatomy region (e.g. "Upper abdomen").</summary>
    public string? Region { get; init; }

    /// <summary>
    /// Path to the binary mask file for this structure (NIfTI).
    /// Null when only a multilabel file was produced.
    /// </summary>
    public string? MaskPath { get; init; }

    /// <summary>Volume of the structure in mm³ (negative if unavailable).</summary>
    public double VolumeMm3 { get; init; } = -1;

    /// <summary>Axis-aligned bounding box in voxel coordinates: [minX, minY, minZ, maxX, maxY, maxZ].</summary>
    public int[]? BoundingBoxVoxels { get; init; }
}
