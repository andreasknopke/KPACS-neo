using KPACS.SDK.Models;

namespace KPACS.SDK.Contracts;

/// <summary>
/// Contract for plugins that transform image / voxel data.
/// Covers AI denoising, super-resolution, virtual non-contrast,
/// histogram equalization, artifact removal, etc.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Operations this processor supports.</summary>
    IReadOnlyList<ImageOperationInfo> AvailableOperations { get; }

    /// <summary>
    /// Process a 2-D slice or 3-D volume and return the result.
    /// The output may overwrite the input file or produce a new file
    /// depending on the <see cref="ImageProcessingRequest.OutputPath"/> setting.
    /// </summary>
    Task<ImageProcessingResult> ProcessAsync(
        ImageProcessingRequest request,
        IProgress<ProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ImageOperationInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Whether this operation works on full 3-D volumes (vs. single 2-D slices).</summary>
    public bool SupportsVolumetric { get; init; }

    /// <summary>Parameter schema: name → default value (all string-encoded).</summary>
    public IReadOnlyDictionary<string, string> ParameterDefaults { get; init; } = new Dictionary<string, string>();
}
