using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure double-oblique reformat geometry (Phase G2): en-face plane
/// construction, orthogonal long-axis plane, click-to-patient mapping, and offset range
/// computation. Pure model surface — no Avalonia, no volume I/O.
/// </summary>
public class DoubleObliqueHelperTests
{
    private static AnnulusPlane XyPlane() => new()
    {
        Center = new Vector3D(0, 0, 0),
        Normal = new Vector3D(0, 0, 1),
    };

    [Fact]
    public void BuildEnFacePlane_uses_annulus_normal_and_offset()
    {
        AnnulusPlane plane = XyPlane();
        VolumeSlicePlane p = DoubleObliqueHelper.BuildEnFacePlane(
            plane, 1.0, 1.0, -10.0, 10.0, 5.0, 100, 100);

        Assert.Equal(new Vector3D(0, 0, 5), p.VolumeCenter);
        Assert.Equal(0, p.Normal.X, 6);
        Assert.Equal(0, p.Normal.Y, 6);
        Assert.Equal(1, p.Normal.Z, 6);
        Assert.Equal(100, p.Width);
        Assert.Equal(100, p.Height);
    }

    [Fact]
    public void BuildEnFacePlane_clamps_offset_to_range()
    {
        AnnulusPlane plane = XyPlane();
        VolumeSlicePlane p = DoubleObliqueHelper.BuildEnFacePlane(
            plane, 1.0, 1.0, -10.0, 10.0, 50.0, 100, 100);

        Assert.Equal(10.0, p.CurrentOffsetMm, 6);
        Assert.Equal(new Vector3D(0, 0, 10), p.VolumeCenter);
    }

    [Fact]
    public void BuildLongAxisPlane_contains_annulus_axis()
    {
        AnnulusPlane plane = XyPlane();
        VolumeSlicePlane p = DoubleObliqueHelper.BuildLongAxisPlane(plane, 1.0, 1.0, 100, 100);

        // Row direction should be the annulus axis (Z).
        Assert.Equal(0, p.RowDirection.X, 6);
        Assert.Equal(0, p.RowDirection.Y, 6);
        Assert.Equal(1, p.RowDirection.Z, 6);
        // Normal should be perpendicular to the axis.
        Assert.Equal(0, p.Normal.Dot(plane.Normal), 6);
    }

    [Fact]
    public void ClickToPatientPoint_maps_pixel_to_plane()
    {
        AnnulusPlane plane = XyPlane();
        Vector3D pt = DoubleObliqueHelper.ClickToPatientPoint(plane, 0.0, 10.0, 20.0, 1.0, 1.0);

        Assert.Equal(10.0, pt.X, 6);
        Assert.Equal(20.0, pt.Y, 6);
        Assert.Equal(0.0, pt.Z, 6);
    }

    [Fact]
    public void ClickToPatientPoint_applies_offset_along_axis()
    {
        AnnulusPlane plane = XyPlane();
        Vector3D pt = DoubleObliqueHelper.ClickToPatientPoint(plane, 7.0, 0.0, 0.0, 1.0, 1.0);

        Assert.Equal(0.0, pt.X, 6);
        Assert.Equal(0.0, pt.Y, 6);
        Assert.Equal(7.0, pt.Z, 6);
    }

    [Fact]
    public void ComputeOffsetRange_centers_on_annulus()
    {
        (double min, double max, int count) = DoubleObliqueHelper.ComputeOffsetRange(20.0, 1.0);

        Assert.Equal(-10.0, min, 6);
        Assert.Equal(10.0, max, 6);
        Assert.Equal(21, count);
    }

    [Fact]
    public void ComputeOffsetRange_zero_depth_gives_single_slice()
    {
        (double min, double max, int count) = DoubleObliqueHelper.ComputeOffsetRange(0.0, 1.0);

        Assert.Equal(0.0, min, 6);
        Assert.Equal(0.0, max, 6);
        Assert.Equal(1, count);
    }
}
