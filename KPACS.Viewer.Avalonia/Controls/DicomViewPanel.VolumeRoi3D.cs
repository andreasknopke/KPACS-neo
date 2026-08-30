using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using KPACS.Viewer.Models;
using KPACS.Viewer.RoiDraft;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    private const int VolumeRoiPreviewHighSampleCount = 32;
    private const int VolumeRoiPreviewMediumSampleCount = 24;
    private const int VolumeRoiPreviewLowSampleCount = 18;
    private const int VolumeRoiPreviewVeryLowSampleCount = 14;

    public sealed record VolumeRoiDraftPreview(
        string OrientationLabel,
        int ContourCount,
        int SliceCount,
        double VolumeCubicMillimeters,
        double FirstPlanePosition,
        double CurrentPlanePosition,
        IReadOnlyList<VolumeRoiDraftPreviewContour> Contours,
        bool SupportsAutoOutlineCorrection = false,
        int AutoOutlineSensitivityLevel = 0,
        bool SupportsAdditiveMode = false,
        bool IsAdditiveModeEnabled = false);

    public sealed record VolumeRoiDraftPreviewContour(
        IReadOnlyList<SpatialVector3D> PatientPoints,
        double PlanePosition,
        bool IsCurrentSlice,
        bool IsClosed,
        bool IsInterpolated,
        int ComponentId = 0);

    private VolumeRoiDraft? _volumeRoiDraft;
    private int _volumeRoiDraftPreviewVersion;
    private VolumeRoiDraftPreviewCache? _volumeRoiDraftPreviewCache;

    public event Action<VolumeRoiDraftPreview?>? VolumeRoiDraftChanged;
    public event Action<SegmentationMask3D>? SegmentationMaskCreated;

    public bool HasVolumeRoiDraft => _volumeRoiDraft is not null;

    public bool TrySetVolumeRoiAdditiveMode(bool enabled)
    {
        if (_volumeRoiDraft is null || _volumeRoiDraft.AdditiveModeEnabled == enabled)
        {
            return false;
        }

        VolumeRoiDraft? previousDraft = CloneVolumeRoiDraftState();

        _volumeRoiDraft.AdditiveModeEnabled = enabled;
        _volumeRoiDraft.SegmentationMask = null;
        if (!enabled)
        {
            _volumeRoiDraft.ActiveAddComponentId = null;
            _volumeRoiDraft.PendingAddContour = null;
        }
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        RecordVolumeRoiDraftTransition("Toggle volume ROI additive mode", previousDraft, _volumeRoiDraft);
        return true;
    }

    public bool TryAdjustVolumeRoiAutoOutlineSensitivity(int deltaSteps, out int resultingLevel)
    {
        resultingLevel = 0;
        if (deltaSteps == 0 || _volumeRoiDraft?.AutoOutlineState is not { } autoOutlineState)
        {
            return false;
        }

        int nextLevel = Math.Clamp(autoOutlineState.SensitivityLevel + deltaSteps, AutoOutlineMinSensitivityLevel, AutoOutlineMaxSensitivityLevel);
        resultingLevel = nextLevel;
        if (nextLevel == autoOutlineState.SensitivityLevel)
        {
            return false;
        }

        return TryCreateAutoOutlinedVolumeRoiDraft(autoOutlineState.ImagePoint, nextLevel);
    }

    public bool TryCompleteVolumeRoiDraft()
    {
        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        VolumeRoiDraft finalizedDraft = CloneVolumeRoiDraft(_volumeRoiDraft);

        VolumeRoiDraftContour[] closedContours = _volumeRoiDraft.Contours.Values
            .Where(contour => contour.IsClosed && contour.Anchors.Count >= 3)
            .OrderBy(contour => contour.PlanePosition)
            .ToArray();
        if (closedContours.Length == 0)
        {
            return false;
        }

        VolumeRoiContour[] contours = closedContours
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

        SegmentationMask3D? segmentationMask = _volumeRoiDraft.SegmentationMask;
        StudyMeasurement measurement = StudyMeasurement.CreateVolumeRoi(FilePath, SpatialMetadata, contours, segmentationMask?.Id);
        if (segmentationMask is not null)
        {
            segmentationMask = segmentationMask with
            {
                Metadata = segmentationMask.Metadata with
                {
                    SourceMeasurementId = measurement.Id.ToString("D"),
                }
            };
        }

        ClearVolumeRoiDraft();
        SetSelectedMeasurement(measurement.Id);
        MeasurementCreated?.Invoke(measurement);
        if (segmentationMask is not null)
        {
            SegmentationMaskCreated?.Invoke(segmentationMask);
        }
        UpdateMeasurementPresentation();

        PushRoiHistory(
            "Finalize volume ROI draft",
            () =>
            {
                ApplyMeasurementDeletedFromHistory(measurement.Id);
                ApplyVolumeRoiDraftState(finalizedDraft);
            },
            () =>
            {
                ApplyVolumeRoiDraftState(null);
                ApplyMeasurementCreatedFromHistory(measurement, segmentationMask);
            });
        return true;
    }

    public bool CancelVolumeRoiDraft()
    {
        if (_volumeRoiDraft is null)
        {
            return false;
        }

        ClearVolumeRoiDraft();
        UpdateMeasurementPresentation();
        return true;
    }

    public bool TryGetVolumeRoiDraftPreview(out VolumeRoiDraftPreview preview)
    {
        preview = default!;
        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        string currentSliceKey = GetCurrentVolumeRoiSliceKey(SpatialMetadata);
        double currentPlanePosition = GetPlanePosition(SpatialMetadata);
        string orientationLabel = OrientationLabel;
        if (_volumeRoiDraftPreviewCache is { } cache &&
            ReferenceEquals(cache.Draft, _volumeRoiDraft) &&
            cache.Version == _volumeRoiDraftPreviewVersion &&
            string.Equals(cache.CurrentSliceKey, currentSliceKey, StringComparison.Ordinal) &&
            string.Equals(cache.OrientationLabel, orientationLabel, StringComparison.Ordinal) &&
            Math.Abs(cache.CurrentPlanePosition - currentPlanePosition) <= 1e-6)
        {
            preview = cache.Preview;
            return true;
        }

        List<VolumeRoiDraftPreviewContour> contours = BuildVolumeRoiDraftPreviewContours(_volumeRoiDraft.Contours.Values, currentSliceKey);

        if (contours.Count == 0)
        {
            return false;
        }

        preview = new VolumeRoiDraftPreview(
            orientationLabel,
            _volumeRoiDraft.Contours.Values.Count(contour => contour.Anchors.Any(anchor => anchor.PatientPoint is not null)),
            contours.Count(contour => contour.IsClosed),
            EstimateVolumeCubicMillimeters(_volumeRoiDraft.Contours.Values),
            _volumeRoiDraft.FirstPlanePosition,
            currentPlanePosition,
            contours,
            _volumeRoiDraft.AutoOutlineState is not null,
            _volumeRoiDraft.AutoOutlineState?.SensitivityLevel ?? 0,
            true,
            _volumeRoiDraft.AdditiveModeEnabled);

        _volumeRoiDraftPreviewCache = new VolumeRoiDraftPreviewCache(
            _volumeRoiDraft,
            _volumeRoiDraftPreviewVersion,
            currentSliceKey,
            orientationLabel,
            currentPlanePosition,
            preview);
        return true;
    }

    private bool CanRetainVolumeRoiDraftForCurrentSlice()
    {
        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        if (!string.Equals(_volumeRoiDraft.FrameOfReferenceUid, SpatialMetadata.FrameOfReferenceUid, StringComparison.Ordinal) ||
            !string.Equals(_volumeRoiDraft.SeriesInstanceUid, SpatialMetadata.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        double alignment = Math.Abs(_volumeRoiDraft.ReferenceNormal.Dot(SpatialMetadata.Normal.Normalize()));
        return alignment >= 0.995;
    }

    private void HandleVolumeRoiPressed(Point imagePoint, int clickCount, KeyModifiers modifiers)
    {
        if (SpatialMetadata is null)
        {
            return;
        }

        VolumeRoiDraft? previousDraft = CloneVolumeRoiDraftState();

        Point clamped = ClampImagePoint(imagePoint);

        VolumeRoiDraft draft = EnsureVolumeRoiDraft();
        VolumeRoiDraftContour? existingContour = GetCurrentVolumeRoiContour(draft, SpatialMetadata);
        bool additiveOnClosedContour = draft.AdditiveModeEnabled && existingContour?.IsClosed == true;
        VolumeRoiDraftContour contour = GetActiveVolumeRoiContour(draft, SpatialMetadata);

        if (clickCount >= 2 && (contour.Anchors.Count < 2 || additiveOnClosedContour))
        {
            TryCreateAutoOutlinedVolumeRoiDraft(clamped);
            return;
        }

        if (clickCount >= 2)
        {
            if (contour.IsClosed)
            {
                ReplaceCurrentVolumeRoiContour(draft, SpatialMetadata, clamped);
            }
            else
            {
                if (contour.Anchors.Count > 0 && Distance(contour.Anchors[^1].ImagePoint, clamped) > 0.5)
                {
                    contour.Anchors.Add(new MeasurementAnchor(clamped, SpatialMetadata.PatientPointFromPixel(clamped)));
                    draft.SegmentationMask = null;
                }

                contour.IsClosed = contour.Anchors.Count >= 3;
                TryCommitPendingAddContour(draft, contour);
            }

            draft.CurrentHoverPoint = null;
            NotifyVolumeRoiDraftChanged();
            UpdateMeasurementPresentation();
            RecordVolumeRoiDraftTransition("Update volume ROI draft", previousDraft, _volumeRoiDraft);
            return;
        }

        if (contour.IsClosed)
        {
            contour = draft.AdditiveModeEnabled && existingContour?.IsClosed == true
                ? StartPendingAddContour(draft, SpatialMetadata, clamped)
                : ReplaceCurrentVolumeRoiContour(draft, SpatialMetadata, clamped);
        }
        else
        {
            contour.Anchors.Add(new MeasurementAnchor(clamped, SpatialMetadata.PatientPointFromPixel(clamped)));
            draft.SegmentationMask = null;
        }

        draft.CurrentHoverPoint = clamped;
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        RecordVolumeRoiDraftTransition("Update volume ROI draft", previousDraft, _volumeRoiDraft);
    }

    private bool HandleVolumeRoiPointerMoved(Point controlPoint)
    {
        if (_measurementTool != MeasurementTool.VolumeRoi || _volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        if (!TryGetImagePoint(controlPoint, out Point imagePoint))
        {
            return false;
        }

        VolumeRoiDraftContour? contour = GetCurrentVolumeRoiContour(_volumeRoiDraft, SpatialMetadata);
        if (contour is null || contour.IsClosed || contour.Anchors.Count == 0)
        {
            return false;
        }

        _volumeRoiDraft.CurrentHoverPoint = ClampImagePoint(imagePoint);
        UpdateMeasurementPresentation();
        return true;
    }

    private void DrawVolumeRoiDraftOverlay()
    {
        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return;
        }

        IEnumerable<VolumeRoiDraftContour> contours = _volumeRoiDraft.PendingAddContour is null
            ? _volumeRoiDraft.Contours.Values
            : _volumeRoiDraft.Contours.Values.Concat([_volumeRoiDraft.PendingAddContour]);

        foreach (VolumeRoiDraftContour contour in contours)
        {
            if (!contour.TryProjectTo(SpatialMetadata, out Point[] imagePoints) || imagePoints.Length == 0)
            {
                continue;
            }

            Point[] controlPoints = imagePoints.Select(ImageToControlPoint).ToArray();
            IBrush stroke = GetVolumeRoiDraftBrush(contour.PlanePosition);

            if (contour.IsClosed && controlPoints.Length >= 3)
            {
                Color strokeColor = ((SolidColorBrush)stroke).Color;
                var polygon = new Polygon
                {
                    Points = new Points(controlPoints),
                    Stroke = stroke,
                    Fill = new SolidColorBrush(Color.FromArgb(0x12, strokeColor.R, strokeColor.G, strokeColor.B)),
                    StrokeThickness = 2,
                };
                MeasurementOverlay.Children.Add(polygon);
            }
            else if (controlPoints.Length >= 2)
            {
                var polyline = new Polyline
                {
                    Points = new Points(controlPoints),
                    Stroke = stroke,
                    StrokeThickness = 2,
                };
                MeasurementOverlay.Children.Add(polyline);
            }

            foreach (Point controlPoint in controlPoints)
            {
                AddHandle(controlPoint, stroke, 4.5);
            }

            if (!contour.IsClosed &&
                _volumeRoiDraft.CurrentHoverPoint is Point hoverPoint &&
                string.Equals(contour.SliceKey, GetCurrentVolumeRoiSliceKey(SpatialMetadata), StringComparison.Ordinal))
            {
                Point hoverControlPoint = ImageToControlPoint(hoverPoint);
                AddLine(controlPoints[^1], hoverControlPoint, stroke, 1.5);
            }
        }
    }

    private string GetVolumeRoiMeasurementText(StudyMeasurement measurement)
    {
        if (measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return string.Empty;
        }

        double volume = EstimateVolumeCubicMillimeters(measurement.VolumeContours.Select(contour => new VolumeRoiDraftContour(
            string.Empty,
            string.Empty,
            contour.ComponentId,
            contour.SourceFilePath,
            contour.ReferencedSopInstanceUid,
            contour.PlaneOrigin,
            contour.RowDirection,
            contour.ColumnDirection,
            contour.Normal,
            contour.PlanePosition,
            contour.RowSpacing,
            contour.ColumnSpacing,
            contour.Anchors.ToList(),
            contour.IsClosed)));

        string title = _measurementTitleProvider?.Invoke(measurement)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "3D ROI";
        }

        string summary = $"Slices {measurement.VolumeContours.Length}  Volume {volume / 1000.0:F1} ml";
        string supplement = _measurementTextSupplementProvider?.Invoke(measurement, [])?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(supplement)
            ? $"{title}\n{summary}"
            : $"{title}\n{summary}\n{supplement}";
    }

    private Point GetVolumeRoiLabelAnchor(StudyMeasurement measurement, DicomSpatialMetadata metadata)
    {
        if (measurement.TryProjectVolumeContoursTo(metadata, out Point[][] contours) && contours.Length > 0)
        {
            return GetPolygonCenter(contours[0]);
        }

        return default;
    }

    private IBrush GetVolumeRoiDraftBrush(double planePosition)
    {
        if (_volumeRoiDraft is null)
        {
            return new SolidColorBrush(Color.Parse("#FF7FDFA2"));
        }

        double delta = planePosition - _volumeRoiDraft.FirstPlanePosition;
        if (Math.Abs(delta) <= 0.25)
        {
            return new SolidColorBrush(Color.Parse("#FFFFD54F"));
        }

        return delta > 0
            ? new SolidColorBrush(Color.Parse("#FFFF8A8A"))
            : new SolidColorBrush(Color.Parse("#FF7FB7FF"));
    }

    private VolumeRoiDraft EnsureVolumeRoiDraft()
    {
        if (_volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice())
        {
            return _volumeRoiDraft;
        }

        if (SpatialMetadata is null)
        {
            throw new InvalidOperationException("Spatial metadata is required for volume ROI drawing.");
        }

        _volumeRoiDraft = new VolumeRoiDraft(
            SpatialMetadata.SeriesInstanceUid,
            SpatialMetadata.FrameOfReferenceUid,
            SpatialMetadata.AcquisitionNumber,
            SpatialMetadata.Normal.Normalize(),
            GetPlanePosition(SpatialMetadata),
            FilePath,
            SpatialMetadata.SopInstanceUid);
        NotifyVolumeRoiDraftChanged();
        return _volumeRoiDraft;
    }

    private VolumeRoiDraftContour GetActiveVolumeRoiContour(VolumeRoiDraft draft, DicomSpatialMetadata metadata)
    {
        string sliceKey = GetCurrentVolumeRoiSliceKey(metadata);
        if (draft.PendingAddContour is { } pending && string.Equals(pending.SliceKey, sliceKey, StringComparison.Ordinal))
        {
            return pending;
        }

        int componentId = draft.AdditiveModeEnabled && draft.ActiveAddComponentId is int addComponentId
            ? addComponentId
            : 0;
        return GetOrCreateVolumeRoiContour(draft, metadata, componentId);
    }

    private VolumeRoiDraftContour GetOrCreateVolumeRoiContour(VolumeRoiDraft draft, DicomSpatialMetadata metadata, int componentId)
    {
        string sliceKey = GetCurrentVolumeRoiSliceKey(metadata);
        string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
        if (draft.Contours.TryGetValue(contourKey, out VolumeRoiDraftContour? contour))
        {
            return contour;
        }

        contour = new VolumeRoiDraftContour(
            sliceKey,
            contourKey,
            componentId,
            metadata.FilePath,
            metadata.SopInstanceUid,
            metadata.Origin,
            metadata.RowDirection,
            metadata.ColumnDirection,
            metadata.Normal,
            GetPlanePosition(metadata),
            metadata.RowSpacing,
            metadata.ColumnSpacing,
            [],
            false);
        draft.Contours[contourKey] = contour;
        draft.SegmentationMask = null;
        return contour;
    }

    private VolumeRoiDraftContour ReplaceCurrentVolumeRoiContour(VolumeRoiDraft draft, DicomSpatialMetadata metadata, Point firstPoint)
    {
        string sliceKey = GetCurrentVolumeRoiSliceKey(metadata);
        foreach (string key in draft.Contours.Where(entry => string.Equals(entry.Value.SliceKey, sliceKey, StringComparison.Ordinal)).Select(entry => entry.Key).ToArray())
        {
            draft.Contours.Remove(key);
        }

        string contourKey = BuildVolumeRoiContourKey(sliceKey, 0);
        var contour = new VolumeRoiDraftContour(
            sliceKey,
            contourKey,
            0,
            metadata.FilePath,
            metadata.SopInstanceUid,
            metadata.Origin,
            metadata.RowDirection,
            metadata.ColumnDirection,
            metadata.Normal,
            GetPlanePosition(metadata),
            metadata.RowSpacing,
            metadata.ColumnSpacing,
            [new MeasurementAnchor(firstPoint, metadata.PatientPointFromPixel(firstPoint))],
            false);
        draft.Contours[contourKey] = contour;
        draft.ActiveAddComponentId = null;
        draft.SegmentationMask = null;
        return contour;
    }

    private VolumeRoiDraftContour StartPendingAddContour(VolumeRoiDraft draft, DicomSpatialMetadata metadata, Point firstPoint)
    {
        string sliceKey = GetCurrentVolumeRoiSliceKey(metadata);
        int componentId = draft.ActiveAddComponentId ?? draft.NextComponentId++;
        draft.ActiveAddComponentId = componentId;
        draft.SegmentationMask = null;
        string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
        draft.PendingAddContour = new VolumeRoiDraftContour(
            sliceKey,
            contourKey,
            componentId,
            metadata.FilePath,
            metadata.SopInstanceUid,
            metadata.Origin,
            metadata.RowDirection,
            metadata.ColumnDirection,
            metadata.Normal,
            GetPlanePosition(metadata),
            metadata.RowSpacing,
            metadata.ColumnSpacing,
            [new MeasurementAnchor(firstPoint, metadata.PatientPointFromPixel(firstPoint))],
            false);
        return draft.PendingAddContour;
    }

    private void TryCommitPendingAddContour(VolumeRoiDraft draft, VolumeRoiDraftContour contour)
    {
        if (!ReferenceEquals(draft.PendingAddContour, contour) || !contour.IsClosed)
        {
            return;
        }

        draft.Contours[contour.ContourKey] = contour;
        draft.PendingAddContour = null;
        draft.SegmentationMask = null;
    }

    private static VolumeRoiDraftContour? GetCurrentVolumeRoiContour(VolumeRoiDraft draft, DicomSpatialMetadata metadata)
    {
        string sliceKey = GetCurrentVolumeRoiSliceKey(metadata);
        int preferredComponentId = draft.AdditiveModeEnabled && draft.ActiveAddComponentId is int addComponentId ? addComponentId : 0;
        return draft.Contours.Values
            .Where(contour => string.Equals(contour.SliceKey, sliceKey, StringComparison.Ordinal))
            .OrderBy(contour => contour.ComponentId == preferredComponentId ? 0 : 1)
            .ThenBy(contour => contour.ComponentId)
            .FirstOrDefault();
    }

    private void ClearVolumeRoiDraft()
    {
        _volumeRoiDraft = null;
        NotifyVolumeRoiDraftChanged();
    }

    private VolumeRoiDraft? CloneVolumeRoiDraftState() =>
        _volumeRoiDraft is null ? null : CloneVolumeRoiDraft(_volumeRoiDraft);

    // Deep-clone now lives on the model (RoiDraft/VolumeRoiDraft.cs). Thin forwarders keep call sites.
    private static VolumeRoiDraft CloneVolumeRoiDraft(VolumeRoiDraft draft) => draft.Clone();

    private static VolumeRoiDraftContour CloneVolumeRoiDraftContour(VolumeRoiDraftContour contour) => contour.Clone();

    private void ApplyVolumeRoiDraftState(VolumeRoiDraft? draft)
    {
        _volumeRoiDraft = draft is null ? null : CloneVolumeRoiDraft(draft);
        InvalidateRoiBallProjectionCaches();
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
    }

    private void RecordVolumeRoiDraftTransition(string description, VolumeRoiDraft? beforeDraft, VolumeRoiDraft? afterDraft)
    {
        VolumeRoiDraft? undoDraft = beforeDraft is null ? null : CloneVolumeRoiDraft(beforeDraft);
        VolumeRoiDraft? redoDraft = afterDraft is null ? null : CloneVolumeRoiDraft(afterDraft);
        PushRoiHistory(
            description,
            () => ApplyVolumeRoiDraftState(undoDraft),
            () => ApplyVolumeRoiDraftState(redoDraft));
    }

    private void NotifyVolumeRoiDraftChanged()
    {
        _volumeRoiDraftPreviewVersion++;
        _volumeRoiDraftPreviewCache = null;
        VolumeRoiDraftChanged?.Invoke(TryGetVolumeRoiDraftPreview(out VolumeRoiDraftPreview preview) ? preview : null);
    }

    private static string GetCurrentVolumeRoiSliceKey(DicomSpatialMetadata metadata) =>
        BuildVolumeRoiSliceKey(metadata.SeriesInstanceUid, metadata.FrameOfReferenceUid, GetPlanePosition(metadata));

    private static string BuildVolumeRoiSliceKey(string seriesInstanceUid, string frameOfReferenceUid, double planePosition) =>
        $"{seriesInstanceUid}|{frameOfReferenceUid}|{Math.Round(planePosition, 3):F3}";

    private static string BuildVolumeRoiContourKey(string sliceKey, int componentId) => $"{sliceKey}|component:{componentId}";

    private static double GetPlanePosition(DicomSpatialMetadata metadata) => metadata.Origin.Dot(metadata.Normal);

    private static List<VolumeRoiDraftPreviewContour> BuildVolumeRoiDraftPreviewContours(IEnumerable<VolumeRoiDraftContour> sourceContours, string currentSliceKey)
    {
        VolumeRoiDraftContour[] contours = sourceContours
            .Where(contour => contour.Anchors.Any(anchor => anchor.PatientPoint is not null))
            .ToArray();
        if (contours.Length == 0)
        {
            return [];
        }

        int previewSampleCount = GetAdaptiveVolumeRoiPreviewSampleCount(contours.Length);
        List<VolumeRoiDraftPreviewContour> previewContours = [];
        foreach (IGrouping<int, VolumeRoiDraftContour> componentGroup in contours.GroupBy(contour => contour.ComponentId).OrderBy(group => group.Key))
        {
            List<(VolumeRoiDraftContour Source, SpatialVector3D[] Points)> closedContours = [];
            foreach (VolumeRoiDraftContour contour in componentGroup
                .Where(contour => contour.IsClosed && contour.Anchors.Count >= 3)
                .OrderBy(contour => contour.PlanePosition))
            {
                SpatialVector3D[] resampled = ResampleClosedContour(contour, previewSampleCount);
                if (resampled.Length < 3)
                {
                    continue;
                }

                if (closedContours.Count > 0)
                {
                    resampled = AlignContourPoints(closedContours[^1].Points, resampled);
                }

                closedContours.Add((contour, resampled));
            }

            for (int index = 0; index < closedContours.Count; index++)
            {
                (VolumeRoiDraftContour contour, SpatialVector3D[] points) = closedContours[index];
                previewContours.Add(new VolumeRoiDraftPreviewContour(
                    points,
                    contour.PlanePosition,
                    string.Equals(contour.SliceKey, currentSliceKey, StringComparison.Ordinal),
                    true,
                    false,
                    contour.ComponentId));

                if (index >= closedContours.Count - 1)
                {
                    continue;
                }

                (VolumeRoiDraftContour nextContour, SpatialVector3D[] nextPoints) = closedContours[index + 1];
                int sectionCount = GetInterpolationSectionCount(Math.Abs(nextContour.PlanePosition - contour.PlanePosition));
                for (int section = 1; section < sectionCount; section++)
                {
                    double t = section / (double)sectionCount;
                    SpatialVector3D[] interpolatedPoints = VolumeRoiInterpolationHelper.TryInterpolateContour(
                        CreateInterpolationInput(contour),
                        CreateInterpolationInput(nextContour),
                        t,
                        previewSampleCount,
                        out SpatialVector3D[] maskInterpolatedPoints)
                        ? maskInterpolatedPoints
                        : InterpolateContourPoints(points, nextPoints, t);
                    previewContours.Add(new VolumeRoiDraftPreviewContour(
                        interpolatedPoints,
                        Lerp(contour.PlanePosition, nextContour.PlanePosition, t),
                        false,
                        true,
                        true,
                        contour.ComponentId));
                }
            }
        }

        foreach (VolumeRoiDraftContour contour in contours.Where(contour => !contour.IsClosed || contour.Anchors.Count < 3))
        {
            SpatialVector3D[] points = contour.Anchors
                .Where(anchor => anchor.PatientPoint is not null)
                .Select(anchor => anchor.PatientPoint!.Value)
                .ToArray();
            if (points.Length == 0)
            {
                continue;
            }

            previewContours.Add(new VolumeRoiDraftPreviewContour(
                points,
                contour.PlanePosition,
                string.Equals(contour.SliceKey, currentSliceKey, StringComparison.Ordinal),
                false,
                false,
                contour.ComponentId));
        }

        return previewContours
            .OrderBy(contour => contour.PlanePosition)
            .ThenBy(contour => contour.IsInterpolated)
            .ToList();
    }

    private static int GetAdaptiveVolumeRoiPreviewSampleCount(int contourCount)
    {
        return contourCount switch
        {
            >= 72 => VolumeRoiPreviewVeryLowSampleCount,
            >= 40 => VolumeRoiPreviewLowSampleCount,
            >= 18 => VolumeRoiPreviewMediumSampleCount,
            _ => VolumeRoiPreviewHighSampleCount,
        };
    }

    private static SpatialVector3D[] ResampleClosedContour(VolumeRoiDraftContour contour, int sampleCount)
    {
        SpatialVector3D[] points = contour.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (points.Length < 3 || sampleCount < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        SpatialVector3D[] result = new SpatialVector3D[sampleCount];
        double step = totalLength / sampleCount;
        int segmentIndex = 0;

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double t = (target - segmentStart) / segmentLength;
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Length], t);
        }

        if (GetSignedContourArea(result, contour.PlaneOrigin, contour.RowDirection, contour.ColumnDirection) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static SpatialVector3D[] AlignContourPoints(SpatialVector3D[] reference, SpatialVector3D[] candidate)
    {
        if (reference.Length == 0 || candidate.Length == 0 || reference.Length != candidate.Length)
        {
            return candidate;
        }

        int bestShift = 0;
        double bestCost = double.MaxValue;
        for (int shift = 0; shift < candidate.Length; shift++)
        {
            double cost = 0;
            for (int index = 0; index < reference.Length; index++)
            {
                SpatialVector3D delta = reference[index] - candidate[(index + shift) % candidate.Length];
                cost += delta.Dot(delta);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestShift = shift;
            }
        }

        if (bestShift == 0)
        {
            return candidate;
        }

        SpatialVector3D[] aligned = new SpatialVector3D[candidate.Length];
        for (int index = 0; index < candidate.Length; index++)
        {
            aligned[index] = candidate[(index + bestShift) % candidate.Length];
        }

        return aligned;
    }

    private static SpatialVector3D[] InterpolateContourPoints(SpatialVector3D[] first, SpatialVector3D[] second, double t)
    {
        int count = Math.Min(first.Length, second.Length);
        SpatialVector3D[] points = new SpatialVector3D[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = Lerp(first[index], second[index], t);
        }

        return points;
    }

    private static VolumeContourInterpolationInput CreateInterpolationInput(VolumeRoiDraftContour contour)
    {
        return new VolumeContourInterpolationInput(
            contour.Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
            contour.PlaneOrigin,
            contour.RowDirection,
            contour.ColumnDirection,
            contour.Normal,
            contour.PlanePosition,
            contour.RowSpacing,
            contour.ColumnSpacing);
    }

    private static int GetInterpolationSectionCount(double gapMillimeters)
    {
        if (gapMillimeters <= 2)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Round(gapMillimeters / 3.0, MidpointRounding.AwayFromZero), 1, 8);
    }

    private static double GetDistance(SpatialVector3D first, SpatialVector3D second) => (first - second).Length;

    private static double GetSignedContourArea(
        IReadOnlyList<SpatialVector3D> contour,
        SpatialVector3D planeOrigin,
        SpatialVector3D rowDirection,
        SpatialVector3D columnDirection)
    {
        if (contour.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < contour.Count; index++)
        {
            SpatialVector3D currentRelative = contour[index] - planeOrigin;
            SpatialVector3D nextRelative = contour[(index + 1) % contour.Count] - planeOrigin;
            double currentX = currentRelative.Dot(rowDirection);
            double currentY = currentRelative.Dot(columnDirection);
            double nextX = nextRelative.Dot(rowDirection);
            double nextY = nextRelative.Dot(columnDirection);
            area += (currentX * nextY) - (nextX * currentY);
        }

        return area * 0.5;
    }

    private static double Lerp(double first, double second, double t) => first + ((second - first) * t);

    private static SpatialVector3D Lerp(SpatialVector3D first, SpatialVector3D second, double t) => first + ((second - first) * t);

    private static double EstimateVolumeCubicMillimeters(IEnumerable<VolumeRoiDraftContour> contours)
    {
        double volume = 0;
        foreach (VolumeRoiDraftContour[] orderedContours in contours
            .Where(contour => contour.IsClosed && contour.Anchors.Count >= 3)
            .GroupBy(contour => contour.ComponentId)
            .Select(group => group.OrderBy(contour => contour.PlanePosition).ToArray()))
        {
            if (orderedContours.Length < 2)
            {
                continue;
            }

            for (int index = 0; index < orderedContours.Length - 1; index++)
            {
                VolumeRoiDraftContour first = orderedContours[index];
                VolumeRoiDraftContour second = orderedContours[index + 1];
                double areaA = CalculateContourArea(first);
                double areaB = CalculateContourArea(second);
                double thickness = Math.Abs(second.PlanePosition - first.PlanePosition);
                volume += ((areaA + areaB) * 0.5) * thickness;
            }
        }

        return volume;
    }

    private static double CalculateContourArea(VolumeRoiDraftContour contour)
    {
        if (contour.Anchors.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < contour.Anchors.Count; index++)
        {
            SpatialVector3D current = contour.Anchors[index].PatientPoint ?? default;
            SpatialVector3D next = contour.Anchors[(index + 1) % contour.Anchors.Count].PatientPoint ?? default;
            SpatialVector3D currentRelative = current - contour.PlaneOrigin;
            SpatialVector3D nextRelative = next - contour.PlaneOrigin;
            double currentX = currentRelative.Dot(contour.RowDirection);
            double currentY = currentRelative.Dot(contour.ColumnDirection);
            double nextX = nextRelative.Dot(contour.RowDirection);
            double nextY = nextRelative.Dot(contour.ColumnDirection);
            area += (currentX * nextY) - (nextX * currentY);
        }

        return Math.Abs(area) * 0.5;
    }

    private sealed record VolumeRoiDraftPreviewCache(
        VolumeRoiDraft Draft,
        int Version,
        string CurrentSliceKey,
        string OrientationLabel,
        double CurrentPlanePosition,
        VolumeRoiDraftPreview Preview);
}
