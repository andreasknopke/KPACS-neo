using KPACS.Viewer.Models;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure annulus analysis (Phase G1): best-fit plane through
/// annulus points, SlicerHeart metric set (area, perimeter, derived diameters,
/// ellipse-fit min/max) on a synthetic ring, and LVOT metrics. Pure model surface —
/// no Avalonia, no volume I/O.
/// </summary>
public class AnnulusAnalysisTests
{
    // ── Plane fit ─────────────────────────────────────────────────────────────

    [Fact]
    public void FitPlane_three_points_in_xy_plane_returns_z_normal()
    {
        var points = new List<AnnulusPoint>
        {
            new() { PatientPoint = new Vector3D(1, 0, 0) },
            new() { PatientPoint = new Vector3D(0, 1, 0) },
            new() { PatientPoint = new Vector3D(-1, 0, 0) },
            new() { PatientPoint = new Vector3D(0, -1, 0) },
        };

        AnnulusPlane plane = AnnulusAnalysisService.FitPlane(points);

        Assert.Equal(0, plane.Center.X, 6);
        Assert.Equal(0, plane.Center.Y, 6);
        Assert.Equal(0, plane.Center.Z, 6);
        Assert.Equal(0, plane.Normal.X, 6);
        Assert.Equal(0, plane.Normal.Y, 6);
        Assert.Equal(1, plane.Normal.Z, 6);
    }

    [Fact]
    public void FitPlane_requires_at_least_three_points()
    {
        var points = new List<AnnulusPoint>
        {
            new() { PatientPoint = new Vector3D(1, 0, 0) },
            new() { PatientPoint = new Vector3D(0, 1, 0) },
        };

        Assert.Throws<ArgumentException>(() => AnnulusAnalysisService.FitPlane(points));
    }

    [Fact]
    public void FitPlane_normal_within_two_degrees_of_known_axis()
    {
        // Points on a plane tilted 30° about the X axis.
        double tilt = 30.0 * Math.PI / 180.0;
        var points = new List<AnnulusPoint>();
        for (int i = 0; i < 12; i++)
        {
            double angle = 2.0 * Math.PI * i / 12.0;
            double x = 10.0 * Math.Cos(angle);
            double y = 10.0 * Math.Sin(angle) * Math.Cos(tilt);
            double z = 10.0 * Math.Sin(angle) * Math.Sin(tilt);
            points.Add(new AnnulusPoint { PatientPoint = new Vector3D(x, y, z) });
        }

        AnnulusPlane plane = AnnulusAnalysisService.FitPlane(points);

        // Expected normal: rotate (0,0,1) by 30° about X → (0, -sin30, cos30).
        Vector3D expected = new(0, -Math.Sin(tilt), Math.Cos(tilt));
        double angleDeg = AngleBetween(plane.Normal, expected);
        Assert.True(angleDeg < 2.0, $"Normal angle {angleDeg:F2}° exceeds 2°.");
    }

    // ── Metrics on a synthetic ring ───────────────────────────────────────────

    [Fact]
    public void ComputeMetrics_circle_radius_10_matches_analytic()
    {
        // A circle of radius 10 in the XY plane, sampled at 512 points.
        var contour = new List<Vector3D>();
        for (int i = 0; i < 512; i++)
        {
            double angle = 2.0 * Math.PI * i / 512.0;
            contour.Add(new Vector3D(10.0 * Math.Cos(angle), 10.0 * Math.Sin(angle), 0));
        }

        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(0, 0, 1) };
        AnnulusMetrics m = AnnulusAnalysisService.ComputeMetrics(contour, plane);

        double expectedArea = Math.PI * 100.0;          // π·r²
        double expectedPerimeter = 2.0 * Math.PI * 10.0; // 2πr
        Assert.Equal(expectedArea, m.AreaMm2, 1);
        Assert.Equal(expectedPerimeter, m.PerimeterMm, 1);
        Assert.Equal(20.0, m.PerimeterDerivedDiameterMm, 1);
        Assert.Equal(20.0, m.AreaDerivedDiameterMm, 1);
        Assert.Equal(20.0, m.MinDiameterMm, 1);
        Assert.Equal(20.0, m.MaxDiameterMm, 1);
    }

    [Fact]
    public void ComputeMetrics_ellipse_min_max_diameters()
    {
        // Ellipse with semi-axes 12 (X) and 8 (Y).
        var contour = new List<Vector3D>();
        for (int i = 0; i < 64; i++)
        {
            double angle = 2.0 * Math.PI * i / 64.0;
            contour.Add(new Vector3D(12.0 * Math.Cos(angle), 8.0 * Math.Sin(angle), 0));
        }

        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(0, 0, 1) };
        AnnulusMetrics m = AnnulusAnalysisService.ComputeMetrics(contour, plane);

        Assert.Equal(24.0, m.MaxDiameterMm, 1);
        Assert.Equal(16.0, m.MinDiameterMm, 1);
    }

    [Fact]
    public void ComputeMetrics_less_than_three_points_returns_empty()
    {
        var contour = new List<Vector3D> { new(1, 0, 0), new(0, 1, 0) };
        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(0, 0, 1) };

        AnnulusMetrics m = AnnulusAnalysisService.ComputeMetrics(contour, plane);

        Assert.Equal(0, m.AreaMm2, 6);
        Assert.Equal(0, m.PerimeterMm, 6);
    }

    // ── Analyze + LVOT ────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_populates_result_and_lvot()
    {
        var points = new List<AnnulusPoint>
        {
            new() { PatientPoint = new Vector3D(10, 0, 0) },
            new() { PatientPoint = new Vector3D(0, 10, 0) },
            new() { PatientPoint = new Vector3D(-10, 0, 0) },
        };
        var contour = new List<Vector3D>();
        for (int i = 0; i < 32; i++)
        {
            double angle = 2.0 * Math.PI * i / 32.0;
            contour.Add(new Vector3D(10.0 * Math.Cos(angle), 10.0 * Math.Sin(angle), 0));
        }
        var lvotContour = new List<Vector3D>();
        for (int i = 0; i < 32; i++)
        {
            double angle = 2.0 * Math.PI * i / 32.0;
            lvotContour.Add(new Vector3D(8.0 * Math.Cos(angle), 8.0 * Math.Sin(angle), 0));
        }

        AnnulusAnalysisResult result = AnnulusAnalysisService.Analyze(points, contour, lvotContour, 10.0);

        Assert.Equal(3, result.Points.Count);
        Assert.NotNull(result.Annulus);
        Assert.NotNull(result.Lvot);
        Assert.Equal(10.0, result.LvotOffsetMm, 6);
        Assert.Equal(20.0, result.Annulus.PerimeterDerivedDiameterMm, 1);
        Assert.Equal(16.0, result.Lvot!.PerimeterDerivedDiameterMm, 1);
    }

    [Fact]
    public void Analyze_without_lvot_leaves_lvot_null()
    {
        var points = new List<AnnulusPoint>
        {
            new() { PatientPoint = new Vector3D(10, 0, 0) },
            new() { PatientPoint = new Vector3D(0, 10, 0) },
            new() { PatientPoint = new Vector3D(-10, 0, 0) },
        };
        var contour = new List<Vector3D>
        {
            new(10, 0, 0), new(0, 10, 0), new(-10, 0, 0), new(0, -10, 0),
        };

        AnnulusAnalysisResult result = AnnulusAnalysisService.Analyze(points, contour);

        Assert.Null(result.Lvot);
    }

    private static double AngleBetween(Vector3D a, Vector3D b)
    {
        double cos = Math.Clamp(a.Dot(b) / (a.Length * b.Length), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }
}
