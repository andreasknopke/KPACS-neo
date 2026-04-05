namespace KPACS.SDK.Models;

/// <summary>
/// Request to apply an image-processing operation.
/// </summary>
public sealed class ImageProcessingRequest
{
    /// <summary>Input volume or slice.</summary>
    public required VolumeDescriptor Input { get; init; }

    /// <summary>Operation identifier (e.g. "denoise", "super_resolution").</summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Where to write the output. If null, the plugin may overwrite the input.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>Preferred compute device.</summary>
    public string Device { get; init; } = "gpu";

    /// <summary>Operation-specific key-value parameters.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Result of an image-processing operation.
/// </summary>
public sealed class ImageProcessingResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Path to the output file.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Optional quality metrics (e.g. "psnr" → "32.5").</summary>
    public IReadOnlyDictionary<string, string> Metrics { get; init; } = new Dictionary<string, string>();

    public double ElapsedSeconds { get; init; }
}
