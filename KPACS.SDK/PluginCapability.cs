namespace KPACS.SDK;

/// <summary>
/// Flags that describe what a plugin can do.
/// A single plugin may implement any combination of capabilities.
/// </summary>
[Flags]
public enum PluginCapability
{
    None = 0,

    /// <summary>Produces labelled volumetric masks from images (e.g. organ segmentation).</summary>
    Segmentation = 1 << 0,

    /// <summary>Transforms pixel / voxel data (denoising, super-resolution, virtual non-contrast, …).</summary>
    ImageProcessing = 1 << 1,

    /// <summary>Analyses DICOM metadata and/or pixel statistics (contrast-phase detection, modality classification, QA, …).</summary>
    DicomAnalysis = 1 << 2,

    /// <summary>Participates in DICOM network operations (routing, pre-fetch, auto-forward, …).</summary>
    DicomCommunication = 1 << 3,
}
