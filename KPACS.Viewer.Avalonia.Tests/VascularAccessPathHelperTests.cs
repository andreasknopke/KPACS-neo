using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure access-route / sub-mask metrics (Phase C3): minimum diameter from
/// path radii, tortuosity wiring, calcium fraction and span-restricted sub-mask volume via
/// nearest-centerline-point voxel attribution. Pure model surface — no Avalonia, no HU sampling.
/// </summary>
public class VascularAccessPathHelperTests
{
    private static readonly Vector3D UnitX = new(1, 0, 0);
    private static readonly Vector3D UnitY = new(0, 1, 0);
    private static readonly Vector3D UnitZ = new(0, 0, 1);

    private const int Size = 20;
    private const double Spacing = 1.0;

    private static VolumeGridGeometry Geometry() =>
        new(Size, Size, Size, Spacing, Spacing, Spacing, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.access.for");

    // A straight centerline along Z at the grid centre (x=y=10), with a constant radius.
    private static CenterlinePath StraightPath(double radiusMm, int zStart, int zEnd)
    {
        List<CenterlinePathPoint> points = [];
        List<double> radii = [];
        double arc = 0;
        for (int z = zStart; z <= zEnd; z++)
        {
            points.Add(new CenterlinePathPoint
            {
                PatientPoint = new Vector3D(10, 10, z),
                ArcLengthMm = arc,
                RadiusMm = radiusMm,
            });
            radii.Add(radiusMm);
            arc += Spacing;
        }

        return new CenterlinePath
        {
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = points,
            RadiiMm = radii.ToArray(),
            TotalLengthMm = points[^1].ArcLengthMm,
        };
    }

    private static SegmentationMaskBuffer FillSphere(VolumeGridGeometry g, double cx, double cy, double cz, double radius)
    {
        SegmentationMaskBuffer buffer = new(g);
        for (int z = 0; z < g.SizeZ; z++)
        {
            for (int y = 0; y < g.SizeY; y++)
            {
                for (int x = 0; x < g.SizeX; x++)
                {
                    double dx = x - cx;
                    double dy = y - cy;
                    double dz = z - cz;
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= radius)
                    {
                        buffer.Set(x, y, z, true);
                    }
                }
            }
        }

        return buffer;
    }

    [Fact]
    public void MinDiameter_from_path_radii_is_twice_min_radius()
    {
        CenterlinePath path = StraightPath(4.0, 2, 17);

        double? min = VascularAccessPathHelper.ComputeMinDiameterFromPath(path);

        Assert.Equal(8.0, min!.Value, 6);
    }

    [Fact]
    public void MinDiameter_no_radii_returns_null()
    {
        CenterlinePath path = StraightPath(4.0, 2, 17) with { RadiiMm = null };
        // Strip per-point radii too.
        List<CenterlinePathPoint> stripped = path.Points.Select(p => p with { RadiusMm = null }).ToList();
        path = path with { Points = stripped };

        Assert.Null(VascularAccessPathHelper.ComputeMinDiameterFromPath(path));
        Assert.Null(VascularAccessPathHelper.ComputeMinDiameterFromPath(null));
    }

    [Fact]
    public void CalciumFraction_zero_when_no_calcium()
    {
        VolumeGridGeometry g = Geometry();
        CenterlinePath path = StraightPath(4.0, 2, 17);
        SegmentationMaskBuffer lumen = FillSphere(g, 10, 10, 10, 4);

        double? fraction = VascularAccessPathHelper.ComputeCalciumFraction(path, lumen, calcium: null);

        Assert.NotNull(fraction);
        Assert.Equal(0.0, fraction!.Value, 6);
    }

    [Fact]
    public void CalciumFraction_counts_calcium_inside_path_region()
    {
        VolumeGridGeometry g = Geometry();
        CenterlinePath path = StraightPath(4.0, 2, 17);
        SegmentationMaskBuffer lumen = FillSphere(g, 10, 10, 10, 4);
        // Calcium blob sits inside the same region as the lumen.
        SegmentationMaskBuffer calcium = FillSphere(g, 10, 10, 10, 2);

        double? fraction = VascularAccessPathHelper.ComputeCalciumFraction(path, lumen, calcium);

        Assert.NotNull(fraction);
        // Calcium sphere (r=2) is smaller than lumen sphere (r=4) → fraction strictly between 0 and 1.
        Assert.InRange(fraction!.Value, 0.05, 0.95);
    }

    [Fact]
    public void CalciumFraction_null_without_lumen()
    {
        CenterlinePath path = StraightPath(4.0, 2, 17);

        Assert.Null(VascularAccessPathHelper.ComputeCalciumFraction(path, lumen: null, calcium: null));
        Assert.Null(VascularAccessPathHelper.ComputeCalciumFraction(null, FillSphere(Geometry(), 10, 10, 10, 4), null));
    }

    [Fact]
    public void SpanVolume_counts_only_voxels_in_arc_window()
    {
        VolumeGridGeometry g = Geometry();
        // Path spans z=2..17 (arc 0..15). Restrict to the lower half (arc 0..6 → z 2..8).
        CenterlinePath path = StraightPath(4.0, 2, 17);
        SegmentationMaskBuffer mask = FillSphere(g, 10, 10, 10, 4);

        double full = VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(mask, path, 0, 15);
        double half = VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(mask, path, 0, 6);

        Assert.True(full > 0);
        Assert.True(half < full);
    }

    [Fact]
    public void SpanVolume_zero_without_mask_or_path()
    {
        CenterlinePath path = StraightPath(4.0, 2, 17);
        Assert.Equal(0, VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(null, path, 0, 15));
        Assert.Equal(0, VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(FillSphere(Geometry(), 10, 10, 10, 4), null, 0, 15));
    }

    [Fact]
    public void BuildAccessPath_populates_all_criteria()
    {
        VolumeGridGeometry g = Geometry();
        CenterlinePath path = StraightPath(4.0, 2, 17);
        SegmentationMaskBuffer lumen = FillSphere(g, 10, 10, 10, 4);
        SegmentationMaskBuffer calcium = FillSphere(g, 10, 10, 10, 1);

        VascularAccessPathMetrics m = VascularAccessPathHelper.BuildAccessPath("Left", path, lumen, calcium);

        Assert.Equal("Left", m.Side);
        Assert.Equal(8.0, m.MinEquivalentDiameterMm!.Value, 6);
        Assert.Equal(15.0, m.LengthMm!.Value, 6);
        Assert.Equal(1.0, m.Tortuosity!.Value, 6); // straight tube
        Assert.NotNull(m.CalciumFraction);
        // min Ø 8mm ok, tortuosity 1.0 ok, tiny calcium ok → Ok.
        Assert.Equal(VascularMetricStatus.Ok, m.Status);
    }

    [Fact]
    public void BuildAccessPath_null_path_degrades_to_unknown()
    {
        VascularAccessPathMetrics m = VascularAccessPathHelper.BuildAccessPath("Right", null, null, null);

        Assert.Equal("Right", m.Side);
        Assert.Null(m.MinEquivalentDiameterMm);
        Assert.Null(m.Tortuosity);
        Assert.Equal(VascularMetricStatus.Unknown, m.Status);
    }
}
