namespace KPACS.SDK.Models;

/// <summary>
/// Request to run a DICOM analysis.
/// </summary>
public sealed class DicomAnalysisRequest
{
    /// <summary>Analysis type identifier (e.g. "contrast_phase", "modality_detection").</summary>
    public required string AnalysisId { get; init; }

    /// <summary>
    /// Non-PHI DICOM tags: keyword → value.
    /// The host strips patient-identifying tags before forwarding.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Optional path to pixel data (NIfTI or raw file).
    /// Only set when the analysis declares <see cref="Contracts.DicomAnalysisInfo.RequiresPixelData"/>.
    /// </summary>
    public string? PixelDataPath { get; init; }
}

/// <summary>
/// Result of a DICOM analysis.
/// </summary>
public sealed class DicomAnalysisResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Primary finding as a structured key-value map.</summary>
    public IReadOnlyDictionary<string, string> Findings { get; init; } = new Dictionary<string, string>();

    /// <summary>Classification label (e.g. "arterial", "venous", "non-contrast").</summary>
    public string? Classification { get; init; }

    /// <summary>Confidence score (0.0 – 1.0).</summary>
    public double? Confidence { get; init; }

    public double ElapsedSeconds { get; init; }
}
