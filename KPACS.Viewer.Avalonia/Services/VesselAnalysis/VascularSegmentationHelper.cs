using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Pure, deterministic helpers for the EVAR segmentation step (Phase B2): union of
/// per-structure masks into a single lumen mask, filtering of TotalSegmentator output
/// down to the aorta + iliac arteries, and status-card statistics (volume, covered
/// Z-slices). No Avalonia, no I/O — fully unit-testable.
/// </summary>
internal static class VascularSegmentationHelper
{
    /// <summary>
    /// True when a mask name refers to the aorta or an iliac <em>artery</em> (the EVAR
    /// target tree). Matches both the machine id form ("iliac_artery_left") and the
    /// display form ("Left Iliac Artery"); venous iliac structures ("iliac_vena",
    /// "Iliac Vena") are rejected because they lack the "arter" token.
    /// </summary>
    public static bool IsVascularStructure(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lower = name.ToLowerInvariant();
        if (lower.Contains("aorta", StringComparison.Ordinal))
        {
            return true;
        }

        return lower.Contains("iliac", StringComparison.Ordinal)
            && lower.Contains("arter", StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps only masks whose name refers to the aorta / iliac arteries. Used to reduce a
    /// full TotalSegmentator multilabel import to the EVAR-relevant lumen candidates.
    /// </summary>
    public static IReadOnlyList<SegmentationMask3D> FilterVascular(IReadOnlyList<SegmentationMask3D> masks)
    {
        ArgumentNullException.ThrowIfNull(masks);
        List<SegmentationMask3D> result = [];
        foreach (SegmentationMask3D mask in masks)
        {
            if (IsVascularStructure(mask.Name))
            {
                result.Add(mask);
            }
        }

        return result;
    }

    /// <summary>
    /// Union of a set of masks that share one grid geometry into a single binary mask.
    /// Voxels set in any input mask become foreground in the result. Returns <c>null</c>
    /// when there are no masks or the union is empty. The result carries
    /// <see cref="SegmentationMaskSourceKind.Derived"/> provenance.
    /// </summary>
    public static SegmentationMask3D? Union(
        VolumeGridGeometry geometry,
        string name,
        string sourceSeriesInstanceUid,
        string sourceFrameOfReferenceUid,
        string sourceStudyInstanceUid,
        IReadOnlyList<SegmentationMask3D> masks)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(masks);

        if (masks.Count == 0)
        {
            return null;
        }

        SegmentationMaskBuffer union = new(geometry);
        foreach (SegmentationMask3D mask in masks)
        {
            if (!GeometryMatches(geometry, mask.Geometry))
            {
                throw new ArgumentException(
                    "All masks in a union must share the same grid geometry.",
                    nameof(masks));
            }

            SegmentationMaskBuffer source = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
            foreach (int linear in source.EnumerateForegroundLinearIndices())
            {
                union.SetLinear(linear, true);
            }
        }

        int foreground = union.CountForeground();
        if (foreground == 0)
        {
            return null;
        }

        return BuildMask(geometry, name, sourceSeriesInstanceUid, sourceFrameOfReferenceUid,
            sourceStudyInstanceUid, union, foreground, SegmentationMaskSourceKind.Derived);
    }

    /// <summary>
    /// Builds a <see cref="SegmentationMask3D"/> from a buffer with derived provenance.
    /// </summary>
    public static SegmentationMask3D FromBuffer(
        VolumeGridGeometry geometry,
        string name,
        string sourceSeriesInstanceUid,
        string sourceFrameOfReferenceUid,
        string sourceStudyInstanceUid,
        SegmentationMaskBuffer buffer,
        SegmentationMaskSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(buffer);

        int foreground = buffer.CountForeground();
        return BuildMask(geometry, name, sourceSeriesInstanceUid, sourceFrameOfReferenceUid,
            sourceStudyInstanceUid, buffer, foreground, sourceKind);
    }

    private static SegmentationMask3D BuildMask(
        VolumeGridGeometry geometry,
        string name,
        string sourceSeriesInstanceUid,
        string sourceFrameOfReferenceUid,
        string sourceStudyInstanceUid,
        SegmentationMaskBuffer buffer,
        int foreground,
        SegmentationMaskSourceKind sourceKind)
    {
        SegmentationMaskStorage storage = buffer.ToStorage();
        SegmentationMaskStatistics? stats = buffer.ComputeStatistics();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var metadata = new SegmentationMaskMetadata(
            sourceKind,
            now,
            now,
            sourceMeasurementId: null,
            notes: null,
            revision: 0,
            statistics: stats);

        return new SegmentationMask3D(
            Guid.NewGuid(),
            name,
            sourceSeriesInstanceUid,
            sourceFrameOfReferenceUid,
            sourceStudyInstanceUid,
            geometry,
            storage,
            metadata);
    }

    /// <summary>
    /// Number of distinct Z-slices (0..SizeZ-1) that contain at least one foreground voxel.
    /// Used by the segmentation status card to show cranio-caudal mask coverage.
    /// </summary>
    public static int CountCoveredZSlices(SegmentationMask3D mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        int sizeX = buffer.SizeX;
        int sizeY = buffer.SizeY;
        int plane = sizeX * sizeY;
        bool[] flags = new bool[buffer.SizeZ];

        foreach (int linear in buffer.EnumerateForegroundLinearIndices())
        {
            int z = linear / plane;
            flags[z] = true;
        }

        int count = 0;
        foreach (bool flag in flags)
        {
            if (flag)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Mask volume in cm³ from the stored statistics (falls back to a fresh scan when the
    /// mask carries no statistics).
    /// </summary>
    public static double GetVolumeCubicCentimeters(SegmentationMask3D mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        double mm3 = mask.Metadata.Statistics?.VolumeCubicMillimeters
            ?? SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage)
                .ComputeStatistics()?.VolumeCubicMillimeters
            ?? 0.0;
        return mm3 / 1000.0;
    }

    private static bool GeometryMatches(VolumeGridGeometry a, VolumeGridGeometry b) =>
        a.SizeX == b.SizeX && a.SizeY == b.SizeY && a.SizeZ == b.SizeZ &&
        Math.Abs(a.SpacingX - b.SpacingX) < 1e-6 &&
        Math.Abs(a.SpacingY - b.SpacingY) < 1e-6 &&
        Math.Abs(a.SpacingZ - b.SpacingZ) < 1e-6;
}
