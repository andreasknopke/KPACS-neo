using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure diameter-chart data helpers (Phase C1): building the
/// (arc-length, diameter) curve from a centerline path or metrics samples, and the
/// chart-click → station mapping. Pure model surface — no Avalonia, no I/O.
/// </summary>
public class VascularDiameterChartHelperTests
{
    private static CenterlinePath MakePath(double[] arcMm, double?[] radiiMm)
    {
        List<CenterlinePathPoint> points = [];
        for (int i = 0; i < arcMm.Length; i++)
        {
            points.Add(new CenterlinePathPoint
            {
                PatientPoint = new Vector3D(0, 0, arcMm[i]),
                ArcLengthMm = arcMm[i],
                RadiusMm = radiiMm[i],
            });
        }

        double[]? radii = radiiMm.All(r => r is null) ? null : radiiMm.Select(r => r ?? 0).ToArray();
        return new CenterlinePath
        {
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = points,
            RadiiMm = radii,
        };
    }

    [Fact]
    public void BuildFromPath_diameter_is_twice_radius()
    {
        CenterlinePath path = MakePath([0, 10, 20], [5.0, 4.0, 3.0]);

        IReadOnlyList<VascularDiameterChartHelper.ChartPoint> pts =
            VascularDiameterChartHelper.BuildFromPath(path);

        Assert.Equal(3, pts.Count);
        Assert.Equal(0.0, pts[0].ArcLengthMm);
        Assert.Equal(10.0, pts[0].DiameterMm, 6);
        Assert.Equal(8.0, pts[1].DiameterMm, 6);
        Assert.Equal(6.0, pts[2].DiameterMm, 6);
    }

    [Fact]
    public void BuildFromPath_skips_points_without_radius()
    {
        // RadiiMm array present but a zero entry (no data) is skipped.
        CenterlinePath path = MakePath([0, 10, 20], [5.0, 0.0, 3.0]);

        IReadOnlyList<VascularDiameterChartHelper.ChartPoint> pts =
            VascularDiameterChartHelper.BuildFromPath(path);

        Assert.Equal(2, pts.Count);
        Assert.Equal(0.0, pts[0].ArcLengthMm);
        Assert.Equal(20.0, pts[1].ArcLengthMm);
    }

    [Fact]
    public void BuildFromPath_no_radii_yields_empty()
    {
        CenterlinePath path = MakePath([0, 10, 20], [null, null, null]);

        Assert.Empty(VascularDiameterChartHelper.BuildFromPath(path));
    }

    [Fact]
    public void BuildFromPath_null_or_short_returns_empty()
    {
        Assert.Empty(VascularDiameterChartHelper.BuildFromPath(null));

        CenterlinePath single = MakePath([0], [5.0]);
        Assert.False(single.HasRenderablePath);
        Assert.Empty(VascularDiameterChartHelper.BuildFromPath(single));
    }

    [Fact]
    public void BuildFromSamples_uses_equivalent_diameter()
    {
        VascularDiameterSample[] samples =
        [
            new() { StationIndex = 0, ArcLengthMm = 0, EquivalentDiameterMm = 12 },
            new() { StationIndex = 1, ArcLengthMm = 5, EquivalentDiameterMm = 0 },
            new() { StationIndex = 2, ArcLengthMm = 10, EquivalentDiameterMm = 9 },
        ];

        IReadOnlyList<VascularDiameterChartHelper.ChartPoint> pts =
            VascularDiameterChartHelper.BuildFromSamples(samples);

        Assert.Equal(2, pts.Count);
        Assert.Equal(12.0, pts[0].DiameterMm, 6);
        Assert.Equal(9.0, pts[1].DiameterMm, 6);
    }

    [Fact]
    public void ResolveStationIndex_nearest_arc_length()
    {
        CenterlinePath path = MakePath([0, 10, 20, 30], [5.0, 5.0, 5.0, 5.0]);

        Assert.Equal(0, VascularDiameterChartHelper.ResolveStationIndex(path, 1.0));
        Assert.Equal(2, VascularDiameterChartHelper.ResolveStationIndex(path, 19.0));
        Assert.Equal(3, VascularDiameterChartHelper.ResolveStationIndex(path, 100.0));
    }

    [Fact]
    public void ResolveStationIndex_unusable_path_returns_minus_one()
    {
        Assert.Equal(-1, VascularDiameterChartHelper.ResolveStationIndex(null, 5));
        CenterlinePath single = MakePath([0], [5.0]);
        Assert.Equal(-1, VascularDiameterChartHelper.ResolveStationIndex(single, 5));
    }

    [Fact]
    public void FormatXAxisLabel_is_invariant()
    {
        string label = VascularDiameterChartHelper.FormatXAxisLabel(123.4);
        Assert.Contains("123", label);
        Assert.DoesNotContain("123,4", label);
    }
}
