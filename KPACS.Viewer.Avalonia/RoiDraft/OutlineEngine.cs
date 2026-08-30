// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - RoiDraft/OutlineEngine.cs
//
// The stateful ROI-draft orchestrator (see CONTEXT.md). Avalonia-free: no Dispatcher, no controls.
// The panel marshals results back to the UI thread. Grown incrementally; the finalize contour
// mapping is the first behaviour behind the seam.
// ------------------------------------------------------------------------------------------------

using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;

namespace KPACS.Viewer.RoiDraft;

/// <summary>
/// Pure operations over a <see cref="VolumeRoiDraft"/> that the finalize path needs. Internal to
/// the viewer; exposed to the unit-test assembly via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class OutlineEngine
{
    /// <summary>
    /// Maps the draft's closed, well-formed contours (≥ 3 anchors) into output
    /// <see cref="VolumeRoiContour"/>s ordered by plane position. Returns <c>false</c> when the
    /// draft has no such contours. Pure: no panel state, no side effects.
    /// </summary>
    internal static bool TryBuildClosedContours(VolumeRoiDraft draft, out VolumeRoiContour[] contours)
    {
        VolumeRoiDraftContour[] closedContours = draft.Contours.Values
            .Where(contour => contour.IsClosed && contour.Anchors.Count >= 3)
            .OrderBy(contour => contour.PlanePosition)
            .ToArray();

        if (closedContours.Length == 0)
        {
            contours = [];
            return false;
        }

        contours = closedContours
            .Select(contour => new VolumeRoiContour(
                contour.Anchors.ToArray(),
                contour.SourceFilePath,
                contour.ReferencedSopInstanceUid,
                contour.PlaneOrigin,
                contour.RowDirection,
                contour.ColumnDirection,
                contour.Normal,
                contour.PlanePosition,
                contour.IsClosed,
                contour.RowSpacing,
                contour.ColumnSpacing,
                contour.ComponentId))
            .ToArray();

        return true;
    }

    /// <summary>
    /// Builds a <see cref="SegmentationMask3D"/> from a flat voxel-index region over
    /// <paramref name="volume"/>. Pure given the volume: no panel state, no side effects.
    /// </summary>
    internal static SegmentationMask3D CreateSegmentationMaskFromRegion(SeriesVolume volume, HashSet<int> region)
    {
        VolumeGridGeometry geometry = new(
            volume.SizeX,
            volume.SizeY,
            volume.SizeZ,
            volume.SpacingX > 0 ? volume.SpacingX : 1.0,
            volume.SpacingY > 0 ? volume.SpacingY : 1.0,
            volume.SpacingZ > 0 ? volume.SpacingZ : 1.0,
            volume.Origin,
            volume.RowDirection.Normalize(),
            volume.ColumnDirection.Normalize(),
            volume.Normal.Normalize(),
            volume.FrameOfReferenceUid);

        SegmentationMaskBuffer buffer = new(geometry);
        foreach (int key in region)
        {
            AutoOutlineMath.DecodeVoxelKey(key, volume.SizeX, volume.SizeY, out int x, out int y, out int z);
            buffer.Set(x, y, z, true);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SegmentationMask3D(
            Guid.NewGuid(),
            "Auto 3D ROI",
            volume.SeriesInstanceUid,
            volume.FrameOfReferenceUid,
            string.Empty,
            geometry,
            buffer.ToStorage(),
            new SegmentationMaskMetadata(
                SegmentationMaskSourceKind.AutoRoi,
                now,
                now,
                sourceMeasurementId: null,
                notes: "Created from auto 3D ROI volume segmentation.",
                revision: 0,
                buffer.ComputeStatistics()));
    }
}
