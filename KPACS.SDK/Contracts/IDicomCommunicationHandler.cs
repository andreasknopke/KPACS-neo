using KPACS.SDK.Models;

namespace KPACS.SDK.Contracts;

/// <summary>
/// Contract for plugins that hook into DICOM network operations.
/// Allows external logic to participate in routing, filtering,
/// auto-forwarding, or pre-fetch decisions.
///
/// <para><b>Reserved for future implementation.</b>
/// The interface is published so that plugin authors can prepare,
/// but the host will not query this capability until a later release.</para>
/// </summary>
public interface IDicomCommunicationHandler
{
    /// <summary>
    /// Called when a C-STORE request arrives at the local SCP.
    /// The plugin may inspect the incoming instance and return routing decisions.
    /// </summary>
    /// <remarks>
    /// The <paramref name="instance"/> carries only non-PHI technical tags
    /// (modality, SOP class, body part, series description, image dimensions)
    /// plus a scratch-file path to the pixel data.
    /// Patient-identifying fields are never forwarded to plugins.
    /// </remarks>
    Task<CStoreDecision> OnCStoreAsync(
        DicomInstanceDescriptor instance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when a C-FIND response set has been collected.
    /// The plugin may re-rank, filter, or annotate the result list.
    /// </summary>
    Task<IReadOnlyList<DicomQueryResultAnnotation>> OnCFindResultsAsync(
        IReadOnlyList<DicomInstanceDescriptor> results,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-PHI descriptor of an incoming DICOM instance.
/// </summary>
public sealed class DicomInstanceDescriptor
{
    /// <summary>SOP Class UID (e.g. "1.2.840.10008.5.1.4.1.1.2" for CT).</summary>
    public required string SopClassUid { get; init; }

    /// <summary>Modality tag value (e.g. "CT", "MR").</summary>
    public string? Modality { get; init; }

    /// <summary>Body part examined tag value.</summary>
    public string? BodyPartExamined { get; init; }

    /// <summary>Series description tag value.</summary>
    public string? SeriesDescription { get; init; }

    /// <summary>Number of pixel rows.</summary>
    public int Rows { get; init; }

    /// <summary>Number of pixel columns.</summary>
    public int Columns { get; init; }

    /// <summary>
    /// Optional path to a temporary copy of the pixel data.
    /// Set only when the plugin manifest declares <c>RequiresPixelData = true</c>.
    /// The file is deleted after the handler returns.
    /// </summary>
    public string? PixelDataPath { get; init; }
}

public sealed class CStoreDecision
{
    /// <summary>Whether the instance should be accepted into storage.</summary>
    public bool Accept { get; init; } = true;

    /// <summary>Optional list of AE titles to auto-forward this instance to.</summary>
    public IReadOnlyList<string> ForwardTo { get; init; } = [];

    /// <summary>Optional tags the host should add/overwrite before storing.</summary>
    public IReadOnlyDictionary<string, string> TagOverrides { get; init; } = new Dictionary<string, string>();
}

public sealed class DicomQueryResultAnnotation
{
    /// <summary>Zero-based index in the original result list.</summary>
    public int ResultIndex { get; init; }

    /// <summary>Free-form label the host may display (e.g. "Likely contrast-enhanced").</summary>
    public string? Annotation { get; init; }

    /// <summary>Numeric relevance score (higher = more relevant). Host may re-sort by this.</summary>
    public double? RelevanceScore { get; init; }
}
