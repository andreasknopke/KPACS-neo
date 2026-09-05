using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Phase C3: pure, unit-testable access-route and sub-mask metrics that the workspace can compute
/// from a <see cref="VesselTree"/> plus the lumen/calcium/thrombus sub-masks produced in step 1.
/// These fill the <see cref="VascularPlanningMetrics.AccessPaths"/>,
/// <see cref="VascularPlanningMetrics.NeckCalciumVolumeCm3"/> and
/// <see cref="VascularPlanningMetrics.NeckThrombusVolumeCm3"/> fields that the base metrics service
/// (which only sees a single path + lumen mask) cannot derive on its own.
/// No Avalonia, no volume HU sampling — only mask buffers and centerline geometry.
/// </summary>
internal static class VascularAccessPathHelper
{
    /// <summary>Radial margin (mm) beyond the local lumen radius when attributing a voxel to a path.</summary>
    private const double RadialAttributionMarginMm = 2.0;

    /// <summary>
    /// Build a fully-classified access-path record for one iliac side from its centerline plus the
    /// lumen and calcium sub-masks. Minimum diameter comes from the path radii; tortuosity from the
    /// arc/chord ratio; calcium fraction from voxels attributed to the path. Any missing input
    /// degrades that single criterion to null (Unknown) without failing the whole record.
    /// </summary>
    public static VascularAccessPathMetrics BuildAccessPath(
        string side,
        CenterlinePath? path,
        SegmentationMaskBuffer? lumen,
        SegmentationMaskBuffer? calcium)
    {
        double? minDiameterMm = ComputeMinDiameterFromPath(path);
        double? lengthMm = path?.HasRenderablePath == true && path!.TotalLengthMm > 0
            ? path.TotalLengthMm
            : null;
        double? tortuosity = VascularExtendedMetricsHelper.ComputeTortuosity(path);
        double? calciumFraction = ComputeCalciumFraction(path, lumen, calcium);

        return VascularExtendedMetricsHelper.BuildAccessPath(side, minDiameterMm, lengthMm, tortuosity, calciumFraction);
    }

    /// <summary>
    /// Minimum equivalent diameter (mm) implied by the path's per-station radii (Ø = 2·r). Null when
    /// the path carries no positive radii.
    /// </summary>
    public static double? ComputeMinDiameterFromPath(CenterlinePath? path)
    {
        if (path is null || path.Points.Count == 0)
        {
            return null;
        }

        List<double> diameters = [];
        if (path.RadiiMm is double[] radii)
        {
            for (int i = 0; i < radii.Length && i < path.Points.Count; i++)
            {
                if (radii[i] > 0)
                {
                    diameters.Add(2.0 * radii[i]);
                }
            }
        }
        else
        {
            foreach (CenterlinePathPoint p in path.Points)
            {
                if (p.RadiusMm is double r && r > 0)
                {
                    diameters.Add(2.0 * r);
                }
            }
        }

        return VascularExtendedMetricsHelper.ComputeMinDiameterMm(diameters);
    }

    /// <summary>
    /// Calcium fraction (0..1) within the swept region of a path: calcium voxels attributed to the
    /// path divided by lumen voxels attributed to the same path. A voxel is attributed when its
    /// nearest centerline point lies within the local radius plus <see cref="RadialAttributionMarginMm"/>.
    /// Null when no path/lumen is available or the lumen region is empty.
    /// </summary>
    public static double? ComputeCalciumFraction(
        CenterlinePath? path,
        SegmentationMaskBuffer? lumen,
        SegmentationMaskBuffer? calcium)
    {
        if (path?.HasRenderablePath != true || lumen is null)
        {
            return null;
        }

        int lumenInPath = CountVoxelsAttributedToPath(lumen, path);
        if (lumenInPath <= 0)
        {
            return null;
        }

        int calciumInPath = calcium is null ? 0 : CountVoxelsAttributedToPath(calcium, path);
        return Math.Clamp(calciumInPath / (double)lumenInPath, 0.0, 1.0);
    }

    /// <summary>
    /// Volume (cm³) of a sub-mask restricted to the arc-length window [startArc, endArc] of a path.
    /// Voxels are attributed by nearest-centerline-point arc length. Returns 0 for an empty window.
    /// </summary>
    public static double ComputeSubMaskVolumeCm3WithinSpan(
        SegmentationMaskBuffer? subMask,
        CenterlinePath? path,
        double startArcMm,
        double endArcMm)
    {
        if (subMask is null || path?.HasRenderablePath != true)
        {
            return 0;
        }

        double lo = Math.Min(startArcMm, endArcMm);
        double hi = Math.Max(startArcMm, endArcMm);
        int count = CountVoxelsWithinArcSpan(subMask, path, lo, hi);
        double mm3 = count * subMask.Geometry.VoxelVolumeCubicMillimeters;
        return mm3 / 1000.0;
    }

    /// <summary>
    /// Count foreground voxels of a buffer whose nearest centerline point has an arc length inside
    /// [startArc, endArc]. Exposed for tests and reuse by the neck-volume metrics.
    /// </summary>
    public static int CountVoxelsWithinArcSpan(
        SegmentationMaskBuffer buffer,
        CenterlinePath path,
        double startArcMm,
        double endArcMm)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Points.Count == 0)
        {
            return 0;
        }

        double lo = Math.Min(startArcMm, endArcMm);
        double hi = Math.Max(startArcMm, endArcMm);
        VolumeGridGeometry g = buffer.Geometry;
        int count = 0;
        foreach (int linear in buffer.EnumerateForegroundLinearIndices())
        {
            Vector3D patient = VoxelToPatient(g, linear);
            double arc = NearestArcLengthOnPath(path, patient);
            if (arc >= lo && arc <= hi)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVoxelsAttributedToPath(SegmentationMaskBuffer buffer, CenterlinePath path)
    {
        VolumeGridGeometry g = buffer.Geometry;
        int count = 0;
        foreach (int linear in buffer.EnumerateForegroundLinearIndices())
        {
            Vector3D patient = VoxelToPatient(g, linear);
            if (IsWithinPathRegion(path, patient))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsWithinPathRegion(CenterlinePath path, Vector3D patient)
    {
        (double distance, int index) = NearestPointOnPath(path, patient);
        double radius = RadiusAt(path, index);
        double limit = (radius > 0 ? radius : RadialAttributionMarginMm) + RadialAttributionMarginMm;
        return distance <= limit;
    }

    private static (double Distance, int Index) NearestPointOnPath(CenterlinePath path, Vector3D patient)
    {
        double best = double.PositiveInfinity;
        int bestIndex = 0;
        for (int i = 0; i < path.Points.Count; i++)
        {
            double d = (path.Points[i].PatientPoint - patient).Length;
            if (d < best)
            {
                best = d;
                bestIndex = i;
            }
        }

        return (best, bestIndex);
    }

    private static double NearestArcLengthOnPath(CenterlinePath path, Vector3D patient)
    {
        (_, int index) = NearestPointOnPath(path, patient);
        return path.Points[index].ArcLengthMm;
    }

    private static double RadiusAt(CenterlinePath path, int index)
    {
        if (path.RadiiMm is double[] radii && index < radii.Length)
        {
            return radii[index];
        }

        return path.Points[index].RadiusMm ?? 0;
    }

    private static Vector3D VoxelToPatient(VolumeGridGeometry g, int linearIndex)
    {
        int sx = g.SizeX;
        int sy = g.SizeY;
        int x = linearIndex % sx;
        int y = (linearIndex / sx) % sy;
        int z = linearIndex / (sx * sy);
        return g.Origin
            + (g.RowDirection * (x * g.SpacingX))
            + (g.ColumnDirection * (y * g.SpacingY))
            + (g.Normal * (z * g.SpacingZ));
    }
}
