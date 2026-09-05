using KPACS.Viewer.Models;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure coronary ostium metrics (Phase G4): axial height along
/// the annulus axis, horizontal distance to the annulus center, and angle to the
/// annulus plane. Pure model surface — no Avalonia, no volume I/O.
/// </summary>
public class CoronaryOstiumTests
{
    private static AnnulusPlane XyPlane() => new()
    {
        Center = default,
        Normal = new Vector3D(0, 0, 1),
    };

    [Fact]
    public void Compute_ostium_above_center_has_axial_height_and_zero_horizontal()
    {
        AnnulusPlane plane = XyPlane();
        CoronaryOstiumResult r = CoronaryOstiumService.Compute("LCA", new Vector3D(0, 0, 12), plane);

        Assert.Equal("LCA", r.Label);
        Assert.Equal(12.0, r.AxialHeightMm, 6);
        Assert.Equal(0.0, r.HorizontalDistanceMm, 6);
        Assert.Equal(90.0, r.AngleToPlaneDegrees, 6);
    }

    [Fact]
    public void Compute_ostium_offset_horizontally_has_horizontal_distance()
    {
        AnnulusPlane plane = XyPlane();
        CoronaryOstiumResult r = CoronaryOstiumService.Compute("RCA", new Vector3D(5, 0, 10), plane);

        Assert.Equal(10.0, r.AxialHeightMm, 6);
        Assert.Equal(5.0, r.HorizontalDistanceMm, 6);
    }

    [Fact]
    public void Compute_ostium_in_plane_has_zero_angle()
    {
        AnnulusPlane plane = XyPlane();
        CoronaryOstiumResult r = CoronaryOstiumService.Compute("LCA", new Vector3D(5, 0, 0), plane);

        Assert.Equal(0.0, r.AxialHeightMm, 6);
        Assert.Equal(5.0, r.HorizontalDistanceMm, 6);
        Assert.Equal(0.0, r.AngleToPlaneDegrees, 6);
    }

    [Fact]
    public void Compute_ostium_below_plane_has_negative_axial_height()
    {
        AnnulusPlane plane = XyPlane();
        CoronaryOstiumResult r = CoronaryOstiumService.Compute("LCA", new Vector3D(0, 0, -8), plane);

        Assert.Equal(-8.0, r.AxialHeightMm, 6);
    }
}
