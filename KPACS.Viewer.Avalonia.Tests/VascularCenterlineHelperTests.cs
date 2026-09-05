using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure EVAR centerline helpers (Phase B3): preset table,
/// modifier → seed-kind resolution, radius→colour ramp, mean radius, and the
/// status-card summary. Pure model surface — no Avalonia, no I/O.
/// </summary>
public class VascularCenterlineHelperTests
{
    [Fact]
    public void Presets_root_aorta_and_parent_branches()
    {
        VascularCenterlineHelper.VesselPreset aorta =
            Assert.Single(VascularCenterlineHelper.Presets, p => p.Label == "aorta");
        Assert.Null(aorta.ParentLabel);

        Assert.Contains(VascularCenterlineHelper.Presets,
            p => p.Label == "iliac_common_left" && p.ParentLabel == "aorta");
        Assert.Contains(VascularCenterlineHelper.Presets,
            p => p.Label == "renal_right" && p.ParentLabel == "aorta");
    }

    [Fact]
    public void ResolveSeedKind_modifier_schema()
    {
        CenterlineSeedSet empty = new();
        Assert.Equal(CenterlineSeedKind.Guide,
            VascularCenterlineHelper.ResolveSeedKind(empty, shift: false, alt: false, ctrl: true));
        Assert.Equal(CenterlineSeedKind.End,
            VascularCenterlineHelper.ResolveSeedKind(empty, shift: false, alt: true, ctrl: false));
        Assert.Equal(CenterlineSeedKind.Start,
            VascularCenterlineHelper.ResolveSeedKind(empty, shift: true, alt: false, ctrl: false));
    }

    [Fact]
    public void ResolveSeedKind_auto_assigns_first_missing_endpoint()
    {
        CenterlineSeedSet empty = new();
        Assert.Equal(CenterlineSeedKind.Start,
            VascularCenterlineHelper.ResolveSeedKind(empty, shift: false, alt: false, ctrl: false));

        CenterlineSeedSet withStart = empty.UpsertSeed(new CenterlineSeed
        {
            Kind = CenterlineSeedKind.Start,
            PatientPoint = new Vector3D(0, 0, 0),
        });
        Assert.Equal(CenterlineSeedKind.End,
            VascularCenterlineHelper.ResolveSeedKind(withStart, false, false, false));

        CenterlineSeedSet withBoth = withStart.UpsertSeed(new CenterlineSeed
        {
            Kind = CenterlineSeedKind.End,
            PatientPoint = new Vector3D(0, 0, 10),
        });
        Assert.Equal(CenterlineSeedKind.Guide,
            VascularCenterlineHelper.ResolveSeedKind(withBoth, false, false, false));
    }

    [Fact]
    public void RadiusToColor_small_is_red_large_is_blue()
    {
        (byte rSmall, byte gSmall, byte bSmall) = VascularCenterlineHelper.RadiusToColor(1.0);
        Assert.True(rSmall > 200, $"small radius should be red, got ({rSmall},{gSmall},{bSmall})");
        Assert.True(bSmall < 40);

        (byte rLarge, byte gLarge, byte bLarge) = VascularCenterlineHelper.RadiusToColor(20.0);
        Assert.True(bLarge > 200, $"large radius should be blue, got ({rLarge},{gLarge},{bLarge})");
        Assert.True(rLarge < 40);
    }

    [Fact]
    public void RadiusToColor_is_monotone_in_blue_over_range()
    {
        byte previousBlue = 0;
        for (double r = 1.5; r <= 15.0; r += 0.5)
        {
            (_, _, byte b) = VascularCenterlineHelper.RadiusToColor(r);
            Assert.True(b >= previousBlue, $"blue channel must not decrease with radius at {r}");
            previousBlue = b;
        }
    }

    [Fact]
    public void MeanRadiusMm_averages_positive_radii()
    {
        CenterlinePath path = new()
        {
            Kind = CenterlinePathKind.Computed,
            Points =
            [
                new CenterlinePathPoint { PatientPoint = new Vector3D(0, 0, 0) },
                new CenterlinePathPoint { PatientPoint = new Vector3D(0, 0, 1) },
                new CenterlinePathPoint { PatientPoint = new Vector3D(0, 0, 2) },
            ],
            RadiiMm = [5.0, 5.0, 5.0],
        };
        Assert.Equal(5.0, VascularCenterlineHelper.MeanRadiusMm(path)!.Value, precision: 6);
    }

    [Fact]
    public void MeanRadiusMm_null_when_no_radius_data()
    {
        CenterlinePath path = new() { Points = [new CenterlinePathPoint()] };
        Assert.Null(VascularCenterlineHelper.MeanRadiusMm(path));
    }

    [Fact]
    public void Summarize_reports_length_and_diameter_when_radius_present()
    {
        CenterlinePath path = new()
        {
            Kind = CenterlinePathKind.Computed,
            TotalLengthMm = 120,
            QualityScore = 0.9,
            Points =
            [
                new CenterlinePathPoint { PatientPoint = new Vector3D(0, 0, 0) },
                new CenterlinePathPoint { PatientPoint = new Vector3D(0, 0, 1) },
            ],
            RadiiMm = [6.0, 6.0],
        };

        string summary = VascularCenterlineHelper.Summarize(path, "Aorta");
        Assert.Contains("Aorta", summary);
        Assert.Contains("120 mm", summary);
        Assert.Contains("Ø 12.0 mm", summary);
    }

    [Fact]
    public void Summarize_no_path_message()
    {
        CenterlinePath empty = new();
        string summary = VascularCenterlineHelper.Summarize(empty, "Aorta");
        Assert.Contains("noch keine Centerline", summary);
    }
}
