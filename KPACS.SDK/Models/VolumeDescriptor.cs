namespace KPACS.SDK.Models;

/// <summary>
/// Describes a 3-D volume that a plugin should process.
/// Carries only technical metadata — never patient-identifying information.
/// </summary>
public sealed class VolumeDescriptor
{
    /// <summary>
    /// Absolute path to the volume data.
    /// May be a directory of DICOM slices, a single NIfTI file, or a raw binary file,
    /// depending on <see cref="Format"/>.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>Data format: "dicom", "nifti", "raw".</summary>
    public string Format { get; init; } = "dicom";

    /// <summary>Volume dimensions in voxels [x, y, z].</summary>
    public int[]? Dimensions { get; init; }

    /// <summary>Voxel spacing in mm [x, y, z].</summary>
    public double[]? SpacingMm { get; init; }

    /// <summary>Imaging modality (e.g. "CT", "MR", "PT").</summary>
    public string? Modality { get; init; }

    /// <summary>Series Instance UID (for DICOM provenance tracking, not PHI).</summary>
    public string? SeriesInstanceUid { get; init; }
}
