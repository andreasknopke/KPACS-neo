using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Tests for vessel-tree assembly and ostium clock-position geometry. A Y-shaped pair of
/// centerlines exercises bifurcation detection, and a straight reference path with known frames
/// exercises the clock projection. Pure geometry — no Avalonia, no volume needed.
/// </summary>
public class VesselTreeTests
{
    private static CenterlinePath MakePath(params (double X, double Y, double Z)[] points)
    {
        List<CenterlinePathPoint> pts = [];
        double arc = 0;
        Vector3D? prev = null;
        foreach ((double x, double y, double z) in points)
        {
            Vector3D p = new(x, y, z);
            if (prev is Vector3D pv)
            {
                arc += (p - pv).Length;
            }

            pts.Add(new CenterlinePathPoint { PatientPoint = p, ArcLengthMm = arc });
            prev = p;
        }

        return new CenterlinePath
        {
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = pts,
            TotalLengthMm = arc,
        };
    }

    [Fact]
    public void AttachBranch_FindsBifurcationWithin1_5mm()
    {
        // Aorta descends along Z at x=y=0 from z=0 to z=20.
        CenterlinePath aorta = MakePath(
            (0, 0, 0), (0, 0, 5), (0, 0, 10), (0, 0, 15), (0, 0, 20));

        // Left common iliac branches off near the aortic bifurcation (0,0,20), slightly offset.
        CenterlinePath iliac = MakePath(
            (0.5, 0, 20.5), (4, 0, 24), (8, 0, 28), (12, 0, 32));

        VesselTree tree = new()
        {
            Segments = [new VesselSegment { Label = "aorta", DisplayName = "Aorta", Path = aorta }],
        };

        VesselSegment child = new() { Label = "iliac_common_left", DisplayName = "ILIAC L", Path = iliac };
        VesselTree result = VesselTreeBuilder.AttachBranch(tree, child, "aorta");

        VesselSegment attached = result.FindByLabel("iliac_common_left")!;
        Assert.Equal("aorta", attached.ParentLabel);
        Assert.NotNull(attached.BifurcationPatientPoint);

        Vector3D bifurcation = attached.BifurcationPatientPoint!.Value;
        Vector3D truth = new(0, 0, 20);
        double error = (bifurcation - truth).Length;
        Assert.True(error < 1.5, $"Bifurcation error {error:0.000} mm exceeds 1.5 mm.");
    }

    [Fact]
    public void AttachBranch_UnknownParent_LeavesRoot()
    {
        CenterlinePath aorta = MakePath((0, 0, 0), (0, 0, 10));
        CenterlinePath orphan = MakePath((0, 0, 10), (5, 0, 15));

        VesselTree tree = new()
        {
            Segments = [new VesselSegment { Label = "aorta", Path = aorta }],
        };

        VesselTree result = VesselTreeBuilder.AttachBranch(
            tree,
            new VesselSegment { Label = "unknown", Path = orphan },
            "does_not_exist");

        Assert.Null(result.FindByLabel("unknown")!.ParentLabel);
    }

    [Fact]
    public void ComputeLandmark_ClockPositionMatchesGeometry()
    {
        // Straight reference path along Z; frames with normal = +Y (12 o'clock) and a fixed
        // binormal. The clock angle is measured from the normal toward the binormal, clockwise.
        CenterlinePath reference = MakePath((0, 0, 0), (0, 0, 10), (0, 0, 20));
        Vector3D tangent = new(0, 0, 1);
        Vector3D normal = new(0, 1, 0);
        Vector3D binormal = tangent.Cross(normal).Normalize(); // = (-1, 0, 0)

        List<CenterlineSampleFrame> frames =
        [
            new(new Vector3D(0, 0, 0), tangent, normal, binormal),
            new(new Vector3D(0, 0, 10), tangent, normal, binormal),
            new(new Vector3D(0, 0, 20), tangent, normal, binormal),
        ];

        // Ostium directly along the normal (12 o'clock) at the middle station.
        OstiumLandmark twelve = OstiaLandmarkService.ComputeLandmark(
            reference, frames, "renal_left", new Vector3D(0, 5, 10), referenceStationMm: 0);
        AssertClockClose(twelve.ClockHours, 0); // 0 ≡ 12 h

        // Ostium along +binormal → 3 o'clock.
        OstiumLandmark three = OstiaLandmarkService.ComputeLandmark(
            reference, frames, "x", framePoint(normal, binormal, 0, 1), 0);
        AssertClockClose(three.ClockHours, 3);

        // Ostium opposite the normal → 6 o'clock.
        OstiumLandmark six = OstiaLandmarkService.ComputeLandmark(
            reference, frames, "x", framePoint(normal, binormal, -1, 0), 0);
        AssertClockClose(six.ClockHours, 6);

        // Ostium along -binormal → 9 o'clock.
        OstiumLandmark nine = OstiaLandmarkService.ComputeLandmark(
            reference, frames, "x", framePoint(normal, binormal, 0, -1), 0);
        AssertClockClose(nine.ClockHours, 9);

        // Distance from reference station (0) equals the nearest station arc length (10 mm).
        Assert.Equal(10.0, twelve.DistanceFromReferenceMm, 3);
    }

    private static Vector3D framePoint(Vector3D normal, Vector3D binormal, double n, double b) =>
        new Vector3D(0, 0, 10) + (normal * n * 5) + (binormal * b * 5);

    private static void AssertClockClose(double actual, double expected)
    {
        // Compare on the clock circle (0 ≡ 12).
        double diff = Math.Abs(actual - expected);
        diff = Math.Min(diff, 12 - diff);
        Assert.True(diff < 0.01, $"Clock {actual:0.000} h deviates from {expected} h by {diff:0.000}.");
    }
}
