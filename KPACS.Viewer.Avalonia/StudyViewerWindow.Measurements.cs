using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Windows;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly List<StudyMeasurement> _studyMeasurements = [];
    private readonly Dictionary<Guid, SegmentationMask3D> _segmentationMasks = [];
    private readonly Dictionary<Guid, PolygonAutoOutlineState> _polygonAutoOutlineStates = [];
    private MeasurementTool _measurementTool = MeasurementTool.None;
    private NavigationTool _navigationTool = NavigationTool.Navigate;
    private Guid? _selectedMeasurementId;
    private bool _isApplyingPolygonAutoOutlineCorrection;

    private MeasurementTool GetEffectiveMeasurementTool() => _measurementTool;

    private void InitializeMeasurementsUi()
    {
        _measurementTool = MeasurementTool.None;
        UpdateMeasurementToolButtons();
        UpdateCenterlineToolButton();
    }

    private void ConfigureMeasurementPanel(ViewportSlot slot, DicomViewPanel panel)
    {
        panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        panel.NavigationTool = _navigationTool;
        panel.SetMeasurementNudgeMode(false);
        panel.SetMeasurementVisibilityProvider(IsMeasurementVisible);
        panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        panel.SetCenterlineOverlays(GetCenterlineOverlaysForSlot(slot));
        panel.SetSegmentationMaskResolver(ResolveSegmentationMask);
        panel.SetDeveloperAnatomyOverlays(GetDeveloperAnatomyOverlaysForSlot(slot));
        panel.SetMeasurementTextSupplementProvider(GetMeasurementTextSupplement);
        panel.MeasurementCreated += OnPanelMeasurementCreated;
        panel.MeasurementUpdated += OnPanelMeasurementUpdated;
        panel.MeasurementDeleted += OnPanelMeasurementDeleted;
        panel.SelectedMeasurementChanged += OnPanelMeasurementSelectedChanged;
        panel.AutoOutlinedMeasurementCreated += OnPanelAutoOutlinedMeasurementCreated;
        panel.AutoOutlineAttempted += OnPanelAutoOutlineAttempted;
        panel.SegmentationMaskCreated += OnPanelSegmentationMaskCreated;
        panel.SegmentationMaskUpdated += OnPanelSegmentationMaskUpdated;
        panel.VolumeRoiDraftChanged += _ =>
        {
            ScheduleVolumeRoiDraftPanelRefresh();
            RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: _volumeRoiDeveloperPanelVisible || _volumeRoiDeveloperPanelPinned);
        };
    }

    private void ApplyMeasurementContext(ViewportSlot slot)
    {
        slot.Panel.SetMeasurementTool(GetEffectiveMeasurementTool());
        slot.Panel.NavigationTool = _navigationTool;
        slot.Panel.SetMeasurementNudgeMode(false);
        slot.Panel.SetMeasurementVisibilityProvider(IsMeasurementVisible);
        slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
        slot.Panel.SetCenterlineOverlays(GetCenterlineOverlaysForSlot(slot));
        slot.Panel.SetDeveloperAnatomyOverlays(GetDeveloperAnatomyOverlaysForSlot(slot));
        RefreshMeasurementInsightPanel();
        ScheduleVolumeRoiDraftPanelRefresh();
        RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: _volumeRoiDeveloperPanelVisible || _volumeRoiDeveloperPanelPinned);
        RefreshReportPanel();
        if (_anatomyPanelVisible || _anatomyPanelPinned)
        {
            RefreshAnatomyPanel();
        }
    }

    private void RefreshMeasurementPanels()
    {
        MeasurementTool effectiveTool = GetEffectiveMeasurementTool();
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetMeasurementTool(effectiveTool);
            slot.Panel.NavigationTool = _navigationTool;
            slot.Panel.SetMeasurementNudgeMode(false);
            slot.Panel.SetMeasurementVisibilityProvider(IsMeasurementVisible);
            slot.Panel.SetMeasurements(_studyMeasurements, _selectedMeasurementId);
            slot.Panel.SetCenterlineOverlays(GetCenterlineOverlaysForSlot(slot));
            slot.Panel.SetDeveloperAnatomyOverlays(GetDeveloperAnatomyOverlaysForSlot(slot));
        }

        RefreshMeasurementInsightPanel();
        ScheduleVolumeRoiDraftPanelRefresh();
        RefreshReportPanel();
        if (_anatomyPanelVisible || _anatomyPanelPinned)
        {
            RefreshAnatomyPanel();
        }
        UpdateStatus();
    }

    private bool IsMeasurementVisible(StudyMeasurement measurement)
    {
        if (measurement.Kind == MeasurementKind.VolumeRoi &&
            measurement.SegmentationMaskId is Guid segmentationMaskId &&
            _segmentationMasks.ContainsKey(segmentationMaskId))
        {
            return _visibleSegmentationMaskIds.Contains(segmentationMaskId);
        }

        return true;
    }

    private IEnumerable<DicomViewPanel.CenterlineOverlay> GetCenterlineOverlaysForSlot(ViewportSlot slot)
    {
        if (slot.CurrentSpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return [];
        }

        string seriesInstanceUid = slot.Series?.SeriesInstanceUid ?? metadata.SeriesInstanceUid;
        string frameOfReferenceUid = metadata.FrameOfReferenceUid;
        List<DicomViewPanel.CenterlineOverlay> overlays = [];

        foreach (CenterlineSeedSet seedSet in _centerlineSeedSets.Values.OrderBy(seed => seed.CreatedUtc))
        {
            CenterlinePath? path = _centerlinePaths.Values
                .Where(candidate => candidate.SeedSetId == seedSet.Id)
                .OrderByDescending(candidate => candidate.Kind == CenterlinePathKind.Computed)
                .ThenByDescending(candidate => candidate.UpdatedUtc)
                .FirstOrDefault();
            if (path is null || !path.HasRenderablePath)
            {
                continue;
            }

            bool seedMatchesSeries = seedSet.GetOrderedSeeds().Any(seed => string.Equals(seed.SeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal));
            bool maskMatchesFrame = false;
            Guid? maskId = path.SegmentationMaskId ?? seedSet.SegmentationMaskId;
            if (maskId is Guid resolvedMaskId && _segmentationMasks.TryGetValue(resolvedMaskId, out SegmentationMask3D? mask))
            {
                maskMatchesFrame =
                    (!string.IsNullOrWhiteSpace(mask.SourceSeriesInstanceUid) && string.Equals(mask.SourceSeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal)) ||
                    (!string.IsNullOrWhiteSpace(mask.SourceFrameOfReferenceUid) && string.Equals(mask.SourceFrameOfReferenceUid, frameOfReferenceUid, StringComparison.Ordinal));
            }

            if (!seedMatchesSeries && !maskMatchesFrame)
            {
                continue;
            }

            overlays.Add(new DicomViewPanel.CenterlineOverlay(
                seedSet.Id,
                path,
                seedSet.GetOrderedSeeds(),
                seedSet.Id == _selectedCenterlineSeedSetId));
        }

        return overlays;
    }

    private void OnMeasurementToolPopupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control button || button.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, ignoreCase: true, out MeasurementTool tool))
        {
            tool = MeasurementTool.None;
        }

        SetMeasurementTool(tool);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void SetMeasurementTool(MeasurementTool tool)
    {
        _measurementTool = tool;
        if (tool != MeasurementTool.None)
        {
            _navigationTool = NavigationTool.Navigate;
            SetCenterlineEditMode(false, showToast: false);
        }

        if (tool != MeasurementTool.None)
        {
            _is3DCursorToolArmed = false;
            Update3DCursorToolButton();
        }

        UpdateMeasurementToolButtons();
        RefreshMeasurementPanels();
    }

    private void UpdateMeasurementToolButtons()
    {
        if (ActionToolsButton is null)
        {
            return;
        }

        ToolboxNavigateButton.IsChecked = _measurementTool == MeasurementTool.None && _navigationTool == NavigationTool.Navigate;
        ToolboxTiltPlaneButton.IsChecked = _measurementTool == MeasurementTool.None && _navigationTool == NavigationTool.TiltPlane;
        ToolboxPixelLensButton.IsChecked = _measurementTool == MeasurementTool.PixelLens;
        ToolboxLineButton.IsChecked = _measurementTool == MeasurementTool.Line;
        ToolboxAngleButton.IsChecked = _measurementTool == MeasurementTool.Angle;
        ToolboxAnnotationButton.IsChecked = _measurementTool == MeasurementTool.Annotation;
        ToolboxRectangleRoiButton.IsChecked = _measurementTool == MeasurementTool.RectangleRoi;
        ToolboxEllipseRoiButton.IsChecked = _measurementTool == MeasurementTool.EllipseRoi;
        ToolboxPolygonRoiButton.IsChecked = _measurementTool == MeasurementTool.PolygonRoi;
        ToolboxVolumeRoiButton.IsChecked = _measurementTool == MeasurementTool.VolumeRoi;
        ToolboxBallRoiButton.IsChecked = _measurementTool == MeasurementTool.BallRoiCorrection;
        ToolboxModifyButton.IsChecked = _measurementTool == MeasurementTool.Modify;
        ToolboxEraseButton.IsChecked = _measurementTool == MeasurementTool.Erase;
        UpdateCenterlineToolButton();
    }

    private bool TryNudgeSelectedMeasurement(Avalonia.Vector delta)
    {
        IEnumerable<ViewportSlot> candidateSlots = _slots
            .Where(slot => slot.Panel.IsImageLoaded)
            .OrderByDescending(slot => ReferenceEquals(slot, _activeSlot));

        foreach (ViewportSlot slot in candidateSlots)
        {
            if (slot.Panel.TryNudgeSelectedMeasurement(delta))
            {
                return true;
            }
        }

        return false;
    }

    private async void OnPanelMeasurementCreated(StudyMeasurement measurement)
    {
        StudyMeasurement? existingMeasurement = _studyMeasurements.FirstOrDefault(existing => existing.Id == measurement.Id);
        if (existingMeasurement?.SegmentationMaskId is Guid existingMaskId)
        {
            _segmentationMasks.Remove(existingMaskId);
        }

        _studyMeasurements.RemoveAll(existing => existing.Id == measurement.Id);
        _studyMeasurements.Add(measurement);
        _selectedMeasurementId = measurement.Id;
        QueueMeasurementInsightRefresh(measurement.Id);
        RefreshMeasurementPanels();
        ScheduleMeasurementSessionSave();

        if (measurement.Kind != MeasurementKind.Annotation)
        {
            return;
        }

        var dialog = new AnnotationTextWindow(measurement.AnnotationText);
        string? annotationText = await dialog.ShowDialog<string?>(this);
        if (annotationText is null)
        {
            return;
        }

        StudyMeasurement updatedMeasurement = measurement.WithAnnotationText(annotationText);
        int index = _studyMeasurements.FindIndex(existing => existing.Id == measurement.Id);
        if (index >= 0)
        {
            _studyMeasurements[index] = updatedMeasurement;
            _selectedMeasurementId = updatedMeasurement.Id;
            RefreshMeasurementPanels();
            ScheduleMeasurementSessionSave();
        }
    }

    private void OnPanelMeasurementUpdated(StudyMeasurement measurement)
    {
        if (!_isApplyingPolygonAutoOutlineCorrection && measurement.Kind == MeasurementKind.PolygonRoi)
        {
            _polygonAutoOutlineStates.Remove(measurement.Id);
        }

        int index = _studyMeasurements.FindIndex(existing => existing.Id == measurement.Id);
        if (index >= 0)
        {
            if (_studyMeasurements[index].SegmentationMaskId is Guid previousMaskId &&
                previousMaskId != measurement.SegmentationMaskId)
            {
                _segmentationMasks.Remove(previousMaskId);
            }

            _studyMeasurements[index] = measurement;
        }
        else
        {
            _studyMeasurements.Add(measurement);
        }

        _selectedMeasurementId = measurement.Id;
        QueueMeasurementInsightRefresh(measurement.Id);
        RefreshMeasurementPanels();
        ScheduleMeasurementSessionSave();

        if (measurement.SegmentationMaskId is Guid updatedMaskId)
        {
            OnSegmentationMaskChangedForCenterline(updatedMaskId);
        }
    }

    private void OnPanelMeasurementSelectedChanged(Guid? measurementId)
    {
        if (_selectedMeasurementId == measurementId)
        {
            return;
        }

        _selectedMeasurementId = measurementId;
        QueueMeasurementInsightRefresh(measurementId);
        RefreshMeasurementPanels();
    }

    private void OnPanelMeasurementDeleted(Guid measurementId)
    {
        StudyMeasurement? removedMeasurement = _studyMeasurements.FirstOrDefault(existing => existing.Id == measurementId);
        _studyMeasurements.RemoveAll(existing => existing.Id == measurementId);
        if (removedMeasurement?.SegmentationMaskId is Guid segmentationMaskId)
        {
            _segmentationMasks.Remove(segmentationMaskId);
            OnSegmentationMaskChangedForCenterline(segmentationMaskId);
        }
        _polygonAutoOutlineStates.Remove(measurementId);
        _reportRegionOverrides.Remove(measurementId);
        _reportAnatomyOverrides.Remove(measurementId);
        _reportReviewStates.Remove(measurementId);
        RemoveMeasurementInsight(measurementId);
        if (_selectedMeasurementId == measurementId)
        {
            _selectedMeasurementId = null;
        }

        RefreshMeasurementPanels();
        ScheduleMeasurementSessionSave();
    }

    private void OnPanelSegmentationMaskCreated(SegmentationMask3D mask)
    {
        var stopwatch = StartVascularStopwatch();
        _segmentationMasks[mask.Id] = mask;
        OnSegmentationMaskChangedForCenterline(mask.Id);
        ScheduleMeasurementSessionSave();
        RecordVascularPerformanceMetric("mask-edit-commit", stopwatch.Elapsed.TotalMilliseconds);
    }

    private void OnPanelSegmentationMaskUpdated(SegmentationMask3D mask)
    {
        var stopwatch = StartVascularStopwatch();
        _segmentationMasks[mask.Id] = mask;
        OnSegmentationMaskChangedForCenterline(mask.Id);
        ScheduleMeasurementSessionSave();
        RecordVascularPerformanceMetric("mask-edit-commit", stopwatch.Elapsed.TotalMilliseconds);
    }

    private SegmentationMask3D? ResolveSegmentationMask(Guid segmentationMaskId)
    {
        return _segmentationMasks.TryGetValue(segmentationMaskId, out SegmentationMask3D? mask)
            ? mask
            : null;
    }

    private void OnPanelAutoOutlinedMeasurementCreated(DicomViewPanel.AutoOutlinedMeasurementInfo info)
    {
        if (info.Kind != MeasurementKind.PolygonRoi)
        {
            return;
        }

        _polygonAutoOutlineStates[info.MeasurementId] = new PolygonAutoOutlineState(info.SeedPoint, info.SensitivityLevel);
        RefreshMeasurementInsightPanel();
    }

    private void OnPanelAutoOutlineAttempted(DicomViewPanel.AutoOutlineAttemptInfo info)
    {
        if (info.Kind == MeasurementKind.VolumeRoi)
        {
            ShowToast(
                info.Succeeded ? info.Message : $"Auto 3D ROI failed: {info.Message}",
                info.Succeeded ? ToastSeverity.Info : ToastSeverity.Warning,
                TimeSpan.FromSeconds(info.Succeeded ? 4 : 7));
            return;
        }

        if (!info.Succeeded)
        {
            ShowToast(info.Message, ToastSeverity.Warning, TimeSpan.FromSeconds(5));
        }
    }

    private void OnToolboxClearVolumeRoiAndCenterlinesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Collect all VolumeRoi measurements and their associated segmentation masks.
        List<StudyMeasurement> volumeRoiMeasurements = _studyMeasurements
            .Where(m => m.Kind == MeasurementKind.VolumeRoi)
            .ToList();

        int removedMeasurements = volumeRoiMeasurements.Count;
        int removedCenterlines = _centerlineSeedSets.Count;

        if (removedMeasurements == 0 && removedCenterlines == 0)
        {
            ShowToast("No 3D ROIs or centerlines to clear.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
            return;
        }

        foreach (StudyMeasurement measurement in volumeRoiMeasurements)
        {
            if (measurement.SegmentationMaskId is Guid maskId)
            {
                _segmentationMasks.Remove(maskId);
            }

            _polygonAutoOutlineStates.Remove(measurement.Id);
            _reportRegionOverrides.Remove(measurement.Id);
            _reportAnatomyOverrides.Remove(measurement.Id);
            _reportReviewStates.Remove(measurement.Id);
            RemoveMeasurementInsight(measurement.Id);
        }

        _studyMeasurements.RemoveAll(m => m.Kind == MeasurementKind.VolumeRoi);

        // Clear all centerline seed sets, paths, and vascular planning bundles.
        _centerlineSeedSets.Clear();
        _centerlinePaths.Clear();
        _vascularPlanningBundles.Clear();
        _selectedCenterlineSeedSetId = null;
        SetCenterlineEditMode(false, showToast: false);

        // Cancel any in-progress volume ROI draft on the active panel.
        if (_activeSlot?.Panel is { HasVolumeRoiDraft: true } activePanel)
        {
            activePanel.CancelVolumeRoiDraft();
        }

        if (_selectedMeasurementId is Guid selectedId &&
            !_studyMeasurements.Any(m => m.Id == selectedId))
        {
            _selectedMeasurementId = null;
        }

        RefreshMeasurementPanels();
        ScheduleMeasurementSessionSave();

        string summary = (removedMeasurements, removedCenterlines) switch
        {
            (> 0, > 0) => $"Cleared {removedMeasurements} 3D ROI(s) and {removedCenterlines} centerline(s).",
            (> 0, _) => $"Cleared {removedMeasurements} 3D ROI(s).",
            _ => $"Cleared {removedCenterlines} centerline(s).",
        };
        ShowToast(summary, ToastSeverity.Success, TimeSpan.FromSeconds(4));

        CloseViewportToolbox();
        CloseAllActionPopups();
    }

    private sealed record PolygonAutoOutlineState(Point SeedPoint, int SensitivityLevel);

    private string GetMeasurementToolLabel() => _measurementTool switch
    {
        _ when _isCenterlineEditMode => "Centerline seeds",
        MeasurementTool.None => "Navigate",
        MeasurementTool.PixelLens => "Pixel lens",
        MeasurementTool.Line => "Line",
        MeasurementTool.Angle => "Angle",
        MeasurementTool.Annotation => "Annotation",
        MeasurementTool.RectangleRoi => "Rectangle ROI",
        MeasurementTool.EllipseRoi => "Ellipse ROI",
        MeasurementTool.PolygonRoi => "Polygon ROI",
        MeasurementTool.VolumeRoi => "3D ROI",
        MeasurementTool.BallRoiCorrection => "ROI ball",
        MeasurementTool.Modify => "Modify",
        MeasurementTool.Erase => "Erase",
        _ => "Navigate",
    };
}