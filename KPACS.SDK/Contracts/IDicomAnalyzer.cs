using KPACS.SDK.Models;

namespace KPACS.SDK.Contracts;

/// <summary>
/// Contract for plugins that analyse DICOM metadata and/or pixel-level statistics.
/// Examples: contrast-phase detection, modality classification, image-quality scoring,
/// protocol compliance checks.
/// </summary>
public interface IDicomAnalyzer
{
    /// <summary>Analyses this provider supports.</summary>
    IReadOnlyList<DicomAnalysisInfo> AvailableAnalyses { get; }

    /// <summary>
    /// Run one analysis on the supplied tags (and optionally pixel data).
    /// </summary>
    Task<DicomAnalysisResult> AnalyzeAsync(
        DicomAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DicomAnalysisInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>When true the plugin requires a file path to pixel data, not just tags.</summary>
    public bool RequiresPixelData { get; init; }
}
