// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - RoiDraft/VolumeRoiDraft.cs
//
// The ROI-draft model: the in-progress 3D ROI the user is drawing. Pure data + pure operations
// (deep clone, plane projection). No Avalonia control, no Dispatcher, no panel state. Owned by
// the OutlineEngine (see CONTEXT.md); the panel reaches it only through the engine.
// ------------------------------------------------------------------------------------------------

using Avalonia;
using KPACS.Viewer.Models;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.RoiDraft;

/// <summary>
/// The in-progress 3D ROI: per-slice contours, additive-mode state, sensitivity, and the
/// carried <see cref="SegmentationMask3D"/>. Internal to the viewer; exposed to the unit-test
/// assembly via <c>InternalsVisibleTo</c>.
/// </summary>
internal sealed class VolumeRoiDraft(
    string seriesInstanceUid,
    string frameOfReferenceUid,
    string acquisitionNumber,
    SpatialVector3D referenceNormal,
    double firstPlanePosition,
    string firstSourceFilePath,
    string firstSopInstanceUid)
{
    public string SeriesInstanceUid { get; } = seriesInstanceUid;
    public string FrameOfReferenceUid { get; } = frameOfReferenceUid;
    public string AcquisitionNumber { get; } = acquisitionNumber;
    public SpatialVector3D ReferenceNormal { get; } = referenceNormal;
    public double FirstPlanePosition { get; } = firstPlanePosition;
    public string FirstSourceFilePath { get; } = firstSourceFilePath;
    public string FirstSopInstanceUid { get; } = firstSopInstanceUid;
    public Dictionary<string, VolumeRoiDraftContour> Contours { get; } = new(StringComparer.Ordinal);
    public Point? CurrentHoverPoint { get; set; }
    public VolumeRoiAutoOutlineState? AutoOutlineState { get; set; }
    public bool AdditiveModeEnabled { get; set; }
    public VolumeRoiDraftContour? PendingAddContour { get; set; }
    public SegmentationMask3D? SegmentationMask { get; set; }
    public int NextComponentId { get; set; } = 1;
    public int? ActiveAddComponentId { get; set; }

    /// <summary>Deep-copies the draft so it can be pushed onto the undo/redo history.</summary>
    public VolumeRoiDraft Clone()
    {
        VolumeRoiDraft clone = new(
            SeriesInstanceUid,
            FrameOfReferenceUid,
            AcquisitionNumber,
            ReferenceNormal,
            FirstPlanePosition,
            FirstSourceFilePath,
            FirstSopInstanceUid)
        {
            CurrentHoverPoint = CurrentHoverPoint,
            AutoOutlineState = AutoOutlineState is null ? null : new VolumeRoiAutoOutlineState(AutoOutlineState.ImagePoint, AutoOutlineState.SensitivityLevel),
            AdditiveModeEnabled = AdditiveModeEnabled,
            PendingAddContour = PendingAddContour?.Clone(),
            SegmentationMask = SegmentationMask,
            NextComponentId = NextComponentId,
            ActiveAddComponentId = ActiveAddComponentId,
        };

        foreach ((string key, VolumeRoiDraftContour contour) in Contours)
        {
            clone.Contours[key] = contour.Clone();
        }

        return clone;
    }
}

/// <summary>The seed point + sensitivity captured when an auto-outline pass produced this draft.</summary>
internal sealed record VolumeRoiAutoOutlineState(Point ImagePoint, int SensitivityLevel);

/// <summary>One closed contour on one slice, with its plane geometry and anchors.</summary>
internal sealed class VolumeRoiDraftContour(
    string sliceKey,
    string contourKey,
    int componentId,
    string sourceFilePath,
    string referencedSopInstanceUid,
    SpatialVector3D planeOrigin,
    SpatialVector3D rowDirection,
    SpatialVector3D columnDirection,
    SpatialVector3D normal,
    double planePosition,
    double rowSpacing,
    double columnSpacing,
    List<MeasurementAnchor> anchors,
    bool isClosed)
{
    public string SliceKey { get; } = sliceKey;
    public string ContourKey { get; } = contourKey;
    public int ComponentId { get; } = componentId;
    public string SourceFilePath { get; } = sourceFilePath;
    public string ReferencedSopInstanceUid { get; } = referencedSopInstanceUid;
    public SpatialVector3D PlaneOrigin { get; } = planeOrigin;
    public SpatialVector3D RowDirection { get; } = rowDirection;
    public SpatialVector3D ColumnDirection { get; } = columnDirection;
    public SpatialVector3D Normal { get; } = normal;
    public double PlanePosition { get; } = planePosition;
    public double RowSpacing { get; } = rowSpacing;
    public double ColumnSpacing { get; } = columnSpacing;
    public List<MeasurementAnchor> Anchors { get; } = anchors;
    public bool IsClosed { get; set; } = isClosed;

    /// <summary>Deep-copies the contour (its anchor list is duplicated, not shared).</summary>
    public VolumeRoiDraftContour Clone() =>
        new(
            SliceKey,
            ContourKey,
            ComponentId,
            SourceFilePath,
            ReferencedSopInstanceUid,
            PlaneOrigin,
            RowDirection,
            ColumnDirection,
            Normal,
            PlanePosition,
            RowSpacing,
            ColumnSpacing,
            Anchors.ToList(),
            IsClosed);

    public bool TryProjectTo(DicomSpatialMetadata metadata, out Point[] imagePoints)
    {
        imagePoints = [];
        if (Anchors.Count == 0)
        {
            return false;
        }

        // Quick reject on first anchor — all anchors share the same geometric plane.
        double planeTolerance = Math.Max(0.75, Math.Min(metadata.RowSpacing, metadata.ColumnSpacing));
        if (Anchors[0].PatientPoint is not { } firstPatientPoint ||
            metadata.DistanceToPlane(firstPatientPoint) > planeTolerance)
        {
            return false;
        }

        Point[] points = new Point[Anchors.Count];
        for (int i = 0; i < Anchors.Count; i++)
        {
            if (Anchors[i].PatientPoint is null)
            {
                return false;
            }

            points[i] = metadata.PixelPointFromPatient(Anchors[i].PatientPoint!.Value);
        }

        imagePoints = points;
        return true;
    }
}
