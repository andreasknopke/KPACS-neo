using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure vascular workspace snapshot helper (Phase E): building a
/// snapshot from workspace parts and validating compatibility against a volume.
/// Pure model surface — no Avalonia, no volume I/O.
/// </summary>
public class VascularWorkspaceSnapshotHelperTests
{
    private static VesselTree BuildTree(string label = "aorta", int pointCount = 5)
    {
        List<CenterlinePathPoint> points = [];
        for (int i = 0; i < pointCount; i++)
        {
            points.Add(new CenterlinePathPoint
            {
                PatientPoint = new Vector3D(i, 0, 0),
                ArcLengthMm = i,
            });
        }

        return new VesselTree
        {
            Segments =
            [
                new VesselSegment
                {
                    Label = label,
                    DisplayName = label,
                    Path = new CenterlinePath { Points = points, TotalLengthMm = pointCount - 1 },
                },
            ],
        };
    }

    [Fact]
    public void Build_populates_all_fields()
    {
        VesselTree tree = BuildTree();
        EndograftPlan plan = new() { NeckDiameterMm = 20.0 };
        VascularPlanningBundle bundle = new() { Metrics = new VascularPlanningMetrics { Summary = "ok" } };

        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", tree, plan, bundle,
            lumenMaskId: Guid.NewGuid(), calciumMaskId: Guid.NewGuid(), thrombusMaskId: Guid.NewGuid(),
            chartStationIndex: 3, activeSegmentLabel: "aorta");

        Assert.Equal("1.2.3", snapshot.SeriesInstanceUid);
        Assert.Equal("1.2.3.4", snapshot.FrameOfReferenceUid);
        Assert.Same(tree, snapshot.VesselTree);
        Assert.Same(plan, snapshot.EndograftPlan);
        Assert.Same(bundle, snapshot.PlanningBundle);
        Assert.NotNull(snapshot.LumenMaskId);
        Assert.NotNull(snapshot.CalciumMaskId);
        Assert.NotNull(snapshot.ThrombusMaskId);
        Assert.Equal(3, snapshot.ChartStationIndex);
        Assert.Equal("aorta", snapshot.ActiveSegmentLabel);
    }

    [Fact]
    public void Build_clamps_negative_chart_station_to_zero()
    {
        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", null, null, null, null, null, null,
            chartStationIndex: -5, activeSegmentLabel: string.Empty);

        Assert.Equal(0, snapshot.ChartStationIndex);
    }

    [Fact]
    public void IsCompatibleWithVolume_matching_uids_and_station_returns_true()
    {
        VesselTree tree = BuildTree(pointCount: 10);
        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", tree, null, null, null, null, null,
            chartStationIndex: 4, activeSegmentLabel: "aorta");

        Assert.True(VascularWorkspaceSnapshotHelper.IsCompatibleWithVolume(snapshot, "1.2.3", "1.2.3.4", referencePathPointCount: 10));
    }

    [Fact]
    public void IsCompatibleWithVolume_series_mismatch_returns_false()
    {
        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", null, null, null, null, null, null, 0, string.Empty);

        Assert.False(VascularWorkspaceSnapshotHelper.IsCompatibleWithVolume(snapshot, "9.9.9", "1.2.3.4", referencePathPointCount: 0));
    }

    [Fact]
    public void IsCompatibleWithVolume_frame_of_reference_mismatch_returns_false()
    {
        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", null, null, null, null, null, null, 0, string.Empty);

        Assert.False(VascularWorkspaceSnapshotHelper.IsCompatibleWithVolume(snapshot, "1.2.3", "9.9.9", referencePathPointCount: 0));
    }

    [Fact]
    public void IsCompatibleWithVolume_station_out_of_range_returns_false()
    {
        VesselTree tree = BuildTree(pointCount: 5);
        VascularWorkspaceSnapshot snapshot = VascularWorkspaceSnapshotHelper.Build(
            "1.2.3", "1.2.3.4", tree, null, null, null, null, null,
            chartStationIndex: 7, activeSegmentLabel: "aorta");

        Assert.False(VascularWorkspaceSnapshotHelper.IsCompatibleWithVolume(snapshot, "1.2.3", "1.2.3.4", referencePathPointCount: 5));
    }

    [Fact]
    public void ResolveReferencePathPointCount_returns_segment_point_count()
    {
        VesselTree tree = BuildTree(label: "aorta", pointCount: 12);
        Assert.Equal(12, VascularWorkspaceSnapshotHelper.ResolveReferencePathPointCount(tree, "aorta"));
    }

    [Fact]
    public void ResolveReferencePathPointCount_unknown_label_returns_zero()
    {
        VesselTree tree = BuildTree(label: "aorta", pointCount: 12);
        Assert.Equal(0, VascularWorkspaceSnapshotHelper.ResolveReferencePathPointCount(tree, "iliac_common_left"));
    }

    [Fact]
    public void ResolveReferencePathPointCount_null_tree_returns_zero()
    {
        Assert.Equal(0, VascularWorkspaceSnapshotHelper.ResolveReferencePathPointCount(null, "aorta"));
    }
}
