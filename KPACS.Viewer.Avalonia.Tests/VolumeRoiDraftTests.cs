using Avalonia;
using KPACS.Viewer.Models;
using KPACS.Viewer.RoiDraft;
using Vector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Tests for the ROI-draft model's pure operations (deep clone). No panel, no Dispatcher.
/// </summary>
public class VolumeRoiDraftTests
{
    private static VolumeRoiDraft NewDraft() =>
        new("series-1", "for-1", "acq-1", new Vector3D(0, 0, 1), 12.5, "file.dcm", "sop-1");

    private static VolumeRoiDraftContour NewContour(string key, int componentId, int anchorCount)
    {
        List<MeasurementAnchor> anchors = [];
        for (int i = 0; i < anchorCount; i++)
        {
            anchors.Add(new MeasurementAnchor(new Point(i, i), new Vector3D(i, i, 0)));
        }

        return new VolumeRoiDraftContour(
            sliceKey: "slice-0",
            contourKey: key,
            componentId: componentId,
            sourceFilePath: "file.dcm",
            referencedSopInstanceUid: "sop-1",
            planeOrigin: new Vector3D(0, 0, 0),
            rowDirection: new Vector3D(1, 0, 0),
            columnDirection: new Vector3D(0, 1, 0),
            normal: new Vector3D(0, 0, 1),
            planePosition: 12.5,
            rowSpacing: 1,
            columnSpacing: 1,
            anchors: anchors,
            isClosed: true);
    }

    [Fact]
    public void Clone_CopiesScalarState()
    {
        VolumeRoiDraft draft = NewDraft();
        draft.AdditiveModeEnabled = true;
        draft.NextComponentId = 7;
        draft.ActiveAddComponentId = 3;
        draft.AutoOutlineState = new VolumeRoiAutoOutlineState(new Point(4, 5), 2);

        VolumeRoiDraft clone = draft.Clone();

        Assert.Equal("series-1", clone.SeriesInstanceUid);
        Assert.Equal("for-1", clone.FrameOfReferenceUid);
        Assert.True(clone.AdditiveModeEnabled);
        Assert.Equal(7, clone.NextComponentId);
        Assert.Equal(3, clone.ActiveAddComponentId);
        Assert.Equal(new VolumeRoiAutoOutlineState(new Point(4, 5), 2), clone.AutoOutlineState);
    }

    [Fact]
    public void Clone_DeepCopiesContoursSoMutationIsIsolated()
    {
        VolumeRoiDraft draft = NewDraft();
        draft.Contours["a"] = NewContour("a", componentId: 1, anchorCount: 3);

        VolumeRoiDraft clone = draft.Clone();
        clone.Contours["a"].IsClosed = false;
        clone.Contours["a"].Anchors.Add(new MeasurementAnchor(new Point(9, 9), new Vector3D(9, 9, 0)));
        clone.Contours["b"] = NewContour("b", componentId: 2, anchorCount: 3);

        // Original is untouched: same contour count, still closed, original anchor count.
        Assert.Single(draft.Contours);
        Assert.True(draft.Contours["a"].IsClosed);
        Assert.Equal(3, draft.Contours["a"].Anchors.Count);
    }

    [Fact]
    public void CloneContour_DuplicatesAnchorList()
    {
        VolumeRoiDraftContour contour = NewContour("a", componentId: 1, anchorCount: 4);

        VolumeRoiDraftContour clone = contour.Clone();
        clone.Anchors.Clear();

        Assert.Equal(4, contour.Anchors.Count);
        Assert.Empty(clone.Anchors);
    }
}
