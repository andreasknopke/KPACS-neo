using Avalonia;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.RoiDraft;
using KPACS.Viewer.Services;
using Vector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Tests for the OutlineEngine finalize mapping (draft → output contours). Pure, no panel.
/// </summary>
public class OutlineEngineTests
{
    private static VolumeRoiDraft NewDraft() =>
        new("series-1", "for-1", "acq-1", new Vector3D(0, 0, 1), 0, "file.dcm", "sop-1");

    private static VolumeRoiDraftContour Contour(string key, bool isClosed, int anchorCount, double planePosition)
    {
        List<MeasurementAnchor> anchors = [];
        for (int i = 0; i < anchorCount; i++)
        {
            anchors.Add(new MeasurementAnchor(new Point(i, i), new Vector3D(i, i, planePosition)));
        }

        return new VolumeRoiDraftContour(
            "slice-0", key, componentId: 1, "file.dcm", "sop-1",
            new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1),
            planePosition, 1, 1, anchors, isClosed);
    }

    [Fact]
    public void TryBuildClosedContours_OnlyClosedWellFormed_AreMapped()
    {
        VolumeRoiDraft draft = NewDraft();
        draft.Contours["closed"] = Contour("closed", isClosed: true, anchorCount: 3, planePosition: 1);
        draft.Contours["open"] = Contour("open", isClosed: false, anchorCount: 5, planePosition: 2);
        draft.Contours["thin"] = Contour("thin", isClosed: true, anchorCount: 2, planePosition: 3);

        bool ok = OutlineEngine.TryBuildClosedContours(draft, out VolumeRoiContour[] contours);

        Assert.True(ok);
        Assert.Single(contours);
        Assert.Equal(1, contours[0].PlanePosition);
    }

    [Fact]
    public void TryBuildClosedContours_NoneQualify_ReturnsFalseAndEmpty()
    {
        VolumeRoiDraft draft = NewDraft();
        draft.Contours["open"] = Contour("open", isClosed: false, anchorCount: 4, planePosition: 1);

        bool ok = OutlineEngine.TryBuildClosedContours(draft, out VolumeRoiContour[] contours);

        Assert.False(ok);
        Assert.Empty(contours);
    }

    [Fact]
    public void TryBuildClosedContours_OrdersByPlanePosition()
    {
        VolumeRoiDraft draft = NewDraft();
        draft.Contours["b"] = Contour("b", isClosed: true, anchorCount: 3, planePosition: 30);
        draft.Contours["a"] = Contour("a", isClosed: true, anchorCount: 3, planePosition: 10);
        draft.Contours["c"] = Contour("c", isClosed: true, anchorCount: 3, planePosition: 20);

        bool ok = OutlineEngine.TryBuildClosedContours(draft, out VolumeRoiContour[] contours);

        Assert.True(ok);
        Assert.Equal([10, 20, 30], contours.Select(c => c.PlanePosition));
    }

    private static SeriesVolume NewVolume(int sizeX, int sizeY, int sizeZ)
    {
        short[] voxels = new short[sizeX * sizeY * sizeZ];
        return new SeriesVolume(
            voxels, sizeX, sizeY, sizeZ,
            1, 1, 1,
            new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1),
            0, 0, short.MinValue, short.MaxValue,
            isMonochrome1: false,
            seriesInstanceUid: "series-1",
            frameOfReferenceUid: "for-1",
            acquisitionNumber: "acq-1",
            sliceFilePaths: [],
            sliceSopInstanceUids: []);
    }

    [Fact]
    public void CreateSegmentationMaskFromRegion_EncodesRegionVoxels()
    {
        SeriesVolume volume = NewVolume(sizeX: 4, sizeY: 4, sizeZ: 2);
        // Voxel (x=1, y=2, z=1) → key = z·sizeY·sizeX + y·sizeX + x = 1·16 + 2·4 + 1 = 25.
        HashSet<int> region = [25];

        SegmentationMask3D mask = OutlineEngine.CreateSegmentationMaskFromRegion(volume, region);

        Assert.Equal("series-1", mask.SourceSeriesInstanceUid);
        Assert.Equal("for-1", mask.SourceFrameOfReferenceUid);
        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        Assert.True(buffer.Get(1, 2, 1));
        Assert.Equal(1, buffer.CountForeground());
    }
}
