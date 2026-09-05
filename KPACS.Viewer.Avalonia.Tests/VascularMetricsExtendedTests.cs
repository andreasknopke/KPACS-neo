using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure extended EVAR metrics (Phase C2): neck conicity (least-squares
/// diameter slope per 10 mm), access-route tortuosity, and the reference-range status
/// classification at the boundaries. Pure model surface — no Avalonia, no volume I/O.
/// </summary>
public class VascularMetricsExtendedTests
{
    private static CenterlinePath MakePath(params (double X, double Y, double Z)[] pts)
    {
        List<CenterlinePathPoint> points = [];
        double arc = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            if (i > 0)
            {
                Vector3D prev = points[i - 1].PatientPoint;
                Vector3D cur = new(pts[i].X, pts[i].Y, pts[i].Z);
                arc += (cur - prev).Length;
            }

            points.Add(new CenterlinePathPoint
            {
                PatientPoint = new Vector3D(pts[i].X, pts[i].Y, pts[i].Z),
                ArcLengthMm = arc,
            });
        }

        return new CenterlinePath
        {
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = points,
            TotalLengthMm = arc,
        };
    }

    [Fact]
    public void Conicity_cone_exact_slope_per_10mm()
    {
        // Diameter falls linearly 20 -> 10 mm over 0..50 mm arc: slope = -10/50 = -0.2 mm/mm
        // => 2.0 mm per 10 mm.
        (double, double)[] samples =
        [
            (0, 20), (10, 18), (20, 16), (30, 14), (40, 12), (50, 10),
        ];

        double? conicity = VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(samples);

        Assert.NotNull(conicity);
        Assert.Equal(2.0, conicity!.Value, 6);
    }

    [Fact]
    public void Conicity_parallel_tube_is_zero()
    {
        (double, double)[] samples = [(0, 15), (10, 15), (20, 15), (30, 15)];

        double? conicity = VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(samples);

        Assert.NotNull(conicity);
        Assert.Equal(0.0, conicity!.Value, 6);
    }

    [Fact]
    public void Conicity_ignores_nonpositive_diameters()
    {
        // A single zero-diameter sample must not corrupt the fit; remaining points still slope 2/10mm.
        (double, double)[] samples = [(0, 20), (10, 0), (20, 16), (30, 14), (40, 12), (50, 10)];

        double? conicity = VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(samples);

        Assert.NotNull(conicity);
        Assert.Equal(2.0, conicity!.Value, 6);
    }

    [Fact]
    public void Conicity_too_few_samples_returns_null()
    {
        Assert.Null(VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm([(0, 20)]));
        Assert.Null(VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm((IReadOnlyList<(double, double)>)[]));
        Assert.Null(VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm((IReadOnlyList<(double, double)>)null!));
    }

    [Fact]
    public void Conicity_same_arc_length_returns_null()
    {
        // All samples at one arc length: no slope is defined (zero-variance denominator).
        (double, double)[] samples = [(5, 20), (5, 18), (5, 16)];

        Assert.Null(VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(samples));
    }

    [Fact]
    public void Conicity_chart_point_overload_matches_tuple()
    {
        VascularDiameterChartHelper.ChartPoint[] pts =
        [
            new(0, 20), new(10, 18), new(20, 16), new(30, 14), new(40, 12), new(50, 10),
        ];

        double? conicity = VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(pts);

        Assert.NotNull(conicity);
        Assert.Equal(2.0, conicity!.Value, 6);
    }

    [Fact]
    public void Tortuosity_straight_line_is_one()
    {
        CenterlinePath path = MakePath((0, 0, 0), (0, 0, 10), (0, 0, 20));

        double? tortuosity = VascularExtendedMetricsHelper.ComputeTortuosity(path);

        Assert.NotNull(tortuosity);
        Assert.Equal(1.0, tortuosity!.Value, 6);
    }

    [Fact]
    public void Tortuosity_bent_path_greater_than_one()
    {
        // Two legs of 10 mm at a right angle: arc = 20, chord = sqrt(200) ~ 14.14 => ratio ~1.414.
        CenterlinePath path = MakePath((0, 0, 0), (10, 0, 0), (10, 10, 0));

        double? tortuosity = VascularExtendedMetricsHelper.ComputeTortuosity(path);

        Assert.NotNull(tortuosity);
        Assert.Equal(20.0 / Math.Sqrt(200.0), tortuosity!.Value, 6);
    }

    [Fact]
    public void Tortuosity_collapsed_or_short_returns_null()
    {
        Assert.Null(VascularExtendedMetricsHelper.ComputeTortuosity(null));
        Assert.Null(VascularExtendedMetricsHelper.ComputeTortuosity(MakePath((0, 0, 0))));

        // Two coincident points: zero chord.
        CenterlinePath collapsed = MakePath((1, 2, 3), (1, 2, 3));
        Assert.Null(VascularExtendedMetricsHelper.ComputeTortuosity(collapsed));
    }

    [Fact]
    public void MinDiameter_skips_nonpositive()
    {
        double? min = VascularExtendedMetricsHelper.ComputeMinDiameterMm([8.0, 0.0, 5.5, 12.0, -1]);

        Assert.Equal(5.5, min!.Value, 6);
    }

    [Fact]
    public void MinDiameter_all_invalid_returns_null()
    {
        Assert.Null(VascularExtendedMetricsHelper.ComputeMinDiameterMm([0.0, -3.0]));
        Assert.Null(VascularExtendedMetricsHelper.ComputeMinDiameterMm([]));
        Assert.Null(VascularExtendedMetricsHelper.ComputeMinDiameterMm((IReadOnlyList<double>)null!));
    }

    [Theory]
    [InlineData(8.0, VascularMetricStatus.Critical)]   // <= 10
    [InlineData(10.0, VascularMetricStatus.Critical)]  // boundary
    [InlineData(12.0, VascularMetricStatus.Warning)]   // <= 15
    [InlineData(15.0, VascularMetricStatus.Warning)]   // boundary
    [InlineData(20.0, VascularMetricStatus.Ok)]
    [InlineData(null, VascularMetricStatus.Unknown)]
    public void NeckLength_classification(double? length, VascularMetricStatus expected) =>
        Assert.Equal(expected, VascularExtendedMetricsHelper.ClassifyNeckLength(length));

    [Theory]
    [InlineData(20.0, VascularMetricStatus.Ok)]
    [InlineData(25.0, VascularMetricStatus.Warning)]  // >= 25
    [InlineData(27.0, VascularMetricStatus.Warning)]
    [InlineData(28.0, VascularMetricStatus.Critical)] // >= 28
    [InlineData(null, VascularMetricStatus.Unknown)]
    public void NeckDiameter_classification(double? dia, VascularMetricStatus expected) =>
        Assert.Equal(expected, VascularExtendedMetricsHelper.ClassifyNeckDiameter(dia));

    [Theory]
    [InlineData(0.5, VascularMetricStatus.Ok)]
    [InlineData(1.0, VascularMetricStatus.Warning)]  // >= 1.0
    [InlineData(1.5, VascularMetricStatus.Warning)]
    [InlineData(2.0, VascularMetricStatus.Critical)] // >= 2.0
    [InlineData(null, VascularMetricStatus.Unknown)]
    public void Conicity_classification(double? c, VascularMetricStatus expected) =>
        Assert.Equal(expected, VascularExtendedMetricsHelper.ClassifyConicity(c));

    [Theory]
    [InlineData(45.0, VascularMetricStatus.Ok)]
    [InlineData(60.0, VascularMetricStatus.Warning)]  // >= 60
    [InlineData(75.0, VascularMetricStatus.Warning)]
    [InlineData(90.0, VascularMetricStatus.Critical)] // >= 90
    [InlineData(null, VascularMetricStatus.Unknown)]
    public void Angulation_classification(double? deg, VascularMetricStatus expected) =>
        Assert.Equal(expected, VascularExtendedMetricsHelper.ClassifyAngulation(deg));

    [Theory]
    [InlineData(5.0, VascularMetricStatus.Critical)]  // <= 6
    [InlineData(6.0, VascularMetricStatus.Critical)]  // boundary
    [InlineData(6.5, VascularMetricStatus.Warning)]   // <= 7
    [InlineData(7.0, VascularMetricStatus.Warning)]   // boundary
    [InlineData(9.0, VascularMetricStatus.Ok)]
    [InlineData(null, VascularMetricStatus.Unknown)]
    public void AccessDiameter_classification(double? dia, VascularMetricStatus expected) =>
        Assert.Equal(expected, VascularExtendedMetricsHelper.ClassifyAccessDiameter(dia));

    [Fact]
    public void AccessPath_status_is_worst_criterion()
    {
        // Diameter fine, tortuosity warning, calcium critical => overall critical.
        VascularAccessPathMetrics m = VascularExtendedMetricsHelper.BuildAccessPath(
            side: "Left", minDiameterMm: 9.0, lengthMm: 80.0, tortuosity: 1.4, calciumFraction: 0.6);

        Assert.Equal("Left", m.Side);
        Assert.Equal(9.0, m.MinEquivalentDiameterMm!.Value, 6);
        Assert.Equal(80.0, m.LengthMm!.Value, 6);
        Assert.Equal(VascularMetricStatus.Critical, m.Status);
    }

    [Fact]
    public void AccessPath_all_ok_when_within_limits()
    {
        VascularAccessPathMetrics m = VascularExtendedMetricsHelper.BuildAccessPath(
            side: "Right", minDiameterMm: 8.0, lengthMm: 90.0, tortuosity: 1.1, calciumFraction: 0.1);

        Assert.Equal(VascularMetricStatus.Ok, m.Status);
    }

    [Fact]
    public void AccessPath_unknown_when_no_data()
    {
        VascularAccessPathMetrics m = VascularExtendedMetricsHelper.BuildAccessPath(
            side: "Right", minDiameterMm: null, lengthMm: null, tortuosity: null, calciumFraction: null);

        Assert.Equal(VascularMetricStatus.Unknown, m.Status);
    }

    [Fact]
    public void BuildConicity_pairs_value_with_status()
    {
        VascularConicityMetrics m = VascularExtendedMetricsHelper.BuildConicity(2.5);

        Assert.Equal(2.5, m.ConicityMmPer10Mm!.Value, 6);
        Assert.Equal(VascularMetricStatus.Critical, m.Status);
    }
}
