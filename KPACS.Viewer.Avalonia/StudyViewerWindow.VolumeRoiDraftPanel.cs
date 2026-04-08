using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double DefaultVolumeRoiPreviewYaw = -0.55;
    private const double DefaultVolumeRoiPreviewPitch = 0.38;
    private const double VolumeRoiPreviewStep = 0.12;
    private const double VolumeRoiPreviewAcceleratedStep = 0.18;
    private const double VolumeRoiPreviewAutoRotateYawStep = 0.055;
    private const double VolumeRoiPreviewAutoRotatePitchAmplitude = 0.22;
    private const double VolumeRoiPreviewAutoRotatePitchBase = 0.16;
    private const int SavedVolumeRoiPreviewHighSampleCount = 32;
    private const int SavedVolumeRoiPreviewMediumSampleCount = 24;
    private const int SavedVolumeRoiPreviewLowSampleCount = 18;
    private const int SavedVolumeRoiPreviewVeryLowSampleCount = 14;
    private readonly DispatcherTimer _volumeRoiPreviewAutoRotateTimer = new();
    private Point _volumeRoiPreviewOffset;
    private bool _volumeRoiPreviewPinned;
    private bool _volumeRoiPreviewAutoRotateEnabled;
    private double _volumeRoiPreviewYaw = DefaultVolumeRoiPreviewYaw;
    private double _volumeRoiPreviewPitch = DefaultVolumeRoiPreviewPitch;
    private double _volumeRoiPreviewAutoRotatePhase;
    private DicomViewPanel.VolumeRoiDraftPreview? _currentVolumeRoiDraftPreview;
    private DicomViewPanel.VolumeRoiDraftPreview? _lastVolumeRoiPreviewSnapshot;
    private bool _lastVolumeRoiPreviewWasDraft;
    private IPointer? _volumeRoiPreviewDragPointer;
    private Point _volumeRoiPreviewDragStart;
    private Point _volumeRoiPreviewDragStartOffset;

    private void ScheduleVolumeRoiDraftPanelRefresh()
    {
        _volumeRoiDraftPanelRefreshTimer.Stop();
        _volumeRoiDraftPanelRefreshTimer.Start();
    }

    private void InitializeVolumeRoiDraftPreviewControls()
    {
        _volumeRoiPreviewAutoRotateTimer.Interval = TimeSpan.FromMilliseconds(90);
        _volumeRoiPreviewAutoRotateTimer.Tick += OnVolumeRoiPreviewAutoRotateTimerTick;
        VolumeRoiAutoRotateCheckBox.IsChecked = _volumeRoiPreviewAutoRotateEnabled;
        UpdateVolumeRoiAutoRotateState();
    }

    private void OnVolumeRoiDraftPanelRefreshTimerTick(object? sender, EventArgs e)
    {
        _volumeRoiDraftPanelRefreshTimer.Stop();
        RefreshVolumeRoiDraftPanel();
    }

    private void RefreshVolumeRoiDraftPanel()
    {
        _volumeRoiDraftPanelRefreshTimer.Stop();

        ViewportSlot? slot = _activeSlot;
        if (TryApplyVolumeRoiDraftPreview(slot))
        {
            return;
        }

        foreach (ViewportSlot candidate in _slots)
        {
            if (ReferenceEquals(candidate, slot))
            {
                continue;
            }

            if (TryApplyVolumeRoiDraftPreview(candidate))
            {
                return;
            }
        }

        if (_selectedMeasurementId is Guid measurementId &&
            _studyMeasurements.FirstOrDefault(candidate => candidate.Id == measurementId && candidate.Kind == MeasurementKind.VolumeRoi) is { } selectedVolumeRoi &&
            TryCreateVolumeRoiMeasurementPreview(selectedVolumeRoi, out DicomViewPanel.VolumeRoiDraftPreview measurementPreview))
        {
            ApplyVolumeRoiPreview(measurementPreview, isDraft: false);
            return;
        }

        if (_volumeRoiPreviewPinned && _lastVolumeRoiPreviewSnapshot is not null)
        {
            ApplyVolumeRoiPreview(_lastVolumeRoiPreviewSnapshot, _lastVolumeRoiPreviewWasDraft);
            return;
        }

        HideVolumeRoiDraftPanel();
    }

    private bool TryApplyVolumeRoiDraftPreview(ViewportSlot? slot)
    {
        if (slot?.Panel is null || !slot.Panel.TryGetVolumeRoiDraftPreview(out DicomViewPanel.VolumeRoiDraftPreview preview))
        {
            return false;
        }

        ApplyVolumeRoiDraftPreview(preview);
        return true;
    }

    private void ApplyVolumeRoiDraftPreview(DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        ApplyVolumeRoiPreview(preview, isDraft: true);
    }

    private void ApplyVolumeRoiPreview(DicomViewPanel.VolumeRoiDraftPreview preview, bool isDraft)
    {
        _currentVolumeRoiDraftPreview = preview;
        _lastVolumeRoiPreviewSnapshot = preview;
        _lastVolumeRoiPreviewWasDraft = isDraft;
        StudyMeasurement? selectedVolumeRoi = !isDraft
            ? GetSelectedVolumeRoiMeasurement()
            : null;
        ApplyVolumeRoiPreviewChrome(selectedVolumeRoi, isDraft);
        VolumeRoiAddButton.IsVisible = isDraft && preview.SupportsAdditiveMode;
        VolumeRoiAddButton.IsChecked = isDraft && preview.IsAdditiveModeEnabled;
        VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        VolumeRoiDraftTitleText.Text = isDraft
            ? "3D ROI draft"
            : BuildSavedVolumeRoiPreviewTitle(selectedVolumeRoi);
        string statusText = isDraft
            ? $"{preview.OrientationLabel} · {preview.ContourCount} drawn · {preview.SliceCount} mesh slices · {(preview.VolumeCubicMillimeters / 1000.0):F1} ml"
            : $"{preview.OrientationLabel} · {preview.ContourCount} source slices · {preview.SliceCount} mesh slices · {(preview.VolumeCubicMillimeters / 1000.0):F1} ml";
        string secondaryLabel = selectedVolumeRoi is null ? string.Empty : GetMeasurementSecondaryLabel(selectedVolumeRoi) ?? string.Empty;
        VolumeRoiDraftStatusText.Text = string.IsNullOrWhiteSpace(secondaryLabel)
            ? statusText
            : $"{secondaryLabel} · {statusText}";
        UpdateVolumeRoiCorrectionControls(preview, isDraft);
        VolumeRoiDraftHintText.Text = isDraft
            ? preview.IsAdditiveModeEnabled
                ? "Add mode is on: click to draft another region on the current slice or double-click to auto-outline and merge it into the 3D ROI. Shrink/Grow refine the latest auto-outline. For local cleanup such as removing wall or stray bridges, switch to ROI ball and drag along the edge. Rotate with ↔/↕, arrow keys, or auto mode. Enter finishes, Esc cancels."
                : "Click to place points, double-click without a drawn line to auto-outline, or double-click with a line to close a slice contour. Turn on Add to merge another region into the model, use Shrink/Grow to refine the auto-outline, and use ROI ball for local cleanup of wrong wall/bridge segments. Scroll to another slice and rotate with ↔/↕, arrow keys, or auto mode before pressing Enter or Esc."
            : "Selected 3D ROI model preview. Scroll through the series to highlight the current slice contour and rotate the model with ↔/↕, arrow keys, or auto mode.";
        RenderVolumeRoiDraftPreview(preview);
        VolumeRoiDraftPanel.IsVisible = true;
        ApplyVolumeRoiDraftPanelOffset();
        VolumeRoiAutoRotateCheckBox.IsChecked = _volumeRoiPreviewAutoRotateEnabled;
        UpdateVolumeRoiAutoRotateState();
    }

    private void HideVolumeRoiDraftPanel()
    {
        _currentVolumeRoiDraftPreview = null;
        ApplyVolumeRoiPreviewChrome(null, isDraft: true);
        VolumeRoiDraftTitleText.Text = "3D ROI draft";
        VolumeRoiAddButton.IsVisible = false;
        VolumeRoiAddButton.IsChecked = false;
        VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        VolumeRoiDraftCanvas.Children.Clear();
        VolumeRoiDraftStatusText.Text = string.Empty;
        VolumeRoiDraftCorrectionRow.IsVisible = false;
        VolumeRoiShrinkButton.IsEnabled = false;
        VolumeRoiGrowButton.IsEnabled = false;
        VolumeRoiCorrectionText.Text = "Sensitivity: default";
        VolumeRoiDraftHintText.Text = "Click to place points, double-click without a line to auto-outline, or double-click with a line to close a slice contour. Scroll to another slice, use ↔/↕ or arrow keys to rotate the mesh preview, enable auto if desired, then press Enter to finish or Esc to cancel.";
        VolumeRoiDraftPanel.IsVisible = false;
        UpdateVolumeRoiAutoRotateState();
    }

    private string BuildSavedVolumeRoiPreviewTitle(StudyMeasurement? measurement)
    {
        if (measurement is null)
        {
            return "3D ROI model";
        }

        string title = GetMeasurementDisplayTitle(measurement) ?? "3D ROI";
        return $"{title} model";
    }

    private void ApplyVolumeRoiPreviewChrome(StudyMeasurement? measurement, bool isDraft)
    {
        Color accent = !isDraft && measurement is not null
            ? GetMeasurementAccentColor(measurement) ?? Color.Parse("#FF56D3C2")
            : Color.Parse("#FF6D9ED6");

        VolumeRoiDraftPanel.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, accent.R, accent.G, accent.B));
        VolumeRoiDraftPanel.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x18, 0x20));
        VolumeRoiDraftTitleText.Foreground = new SolidColorBrush(BlendVolumeRoiPreviewChrome(accent, Colors.White, 0.32));
        VolumeRoiDraftStatusText.Foreground = new SolidColorBrush(BlendVolumeRoiPreviewChrome(accent, Color.Parse("#FFD8E5F0"), 0.48));
        VolumeRoiDraftHintText.Foreground = new SolidColorBrush(Color.Parse("#FF9DB3C7"));
    }

    private static Color BlendVolumeRoiPreviewChrome(Color start, Color end, double amount)
    {
        double clamped = Math.Clamp(amount, 0, 1);
        byte a = (byte)Math.Round(start.A + ((end.A - start.A) * clamped));
        byte r = (byte)Math.Round(start.R + ((end.R - start.R) * clamped));
        byte g = (byte)Math.Round(start.G + ((end.G - start.G) * clamped));
        byte b = (byte)Math.Round(start.B + ((end.B - start.B) * clamped));
        return Color.FromArgb(a, r, g, b);
    }

    private void OnVolumeRoiAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewportSlot? slotWithDraft = _activeSlot?.Panel is { HasVolumeRoiDraft: true }
            ? _activeSlot
            : _slots.FirstOrDefault(candidate => candidate.Panel.HasVolumeRoiDraft);

        bool enabled = VolumeRoiAddButton.IsChecked == true;
        if (slotWithDraft?.Panel.TrySetVolumeRoiAdditiveMode(enabled) == true)
        {
            RefreshVolumeRoiDraftPanel();
            UpdateStatus();
        }
        else
        {
            VolumeRoiAddButton.IsChecked = _currentVolumeRoiDraftPreview?.IsAdditiveModeEnabled == true;
        }

        e.Handled = true;
    }

    private void UpdateVolumeRoiCorrectionControls(DicomViewPanel.VolumeRoiDraftPreview preview, bool isDraft)
    {
        bool showCorrection = isDraft && preview.SupportsAutoOutlineCorrection;
        VolumeRoiDraftCorrectionRow.IsVisible = showCorrection;
        VolumeRoiShrinkButton.IsEnabled = showCorrection;
        VolumeRoiGrowButton.IsEnabled = showCorrection;

        string levelText = preview.AutoOutlineSensitivityLevel switch
        {
            > 0 => $"grow +{preview.AutoOutlineSensitivityLevel}",
            < 0 => $"shrink {preview.AutoOutlineSensitivityLevel}",
            _ => "default"
        };

        VolumeRoiCorrectionText.Text = showCorrection
            ? $"Sensitivity: {levelText}"
            : "Sensitivity: default";
    }

    private void OnVolumeRoiShrinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustVolumeRoiAutoOutlineSensitivity(-1);
        e.Handled = true;
    }

    private void OnVolumeRoiGrowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AdjustVolumeRoiAutoOutlineSensitivity(1);
        e.Handled = true;
    }

    private void AdjustVolumeRoiAutoOutlineSensitivity(int delta)
    {
        ViewportSlot? slotWithDraft = _activeSlot?.Panel is { HasVolumeRoiDraft: true }
            ? _activeSlot
            : _slots.FirstOrDefault(candidate => candidate.Panel.HasVolumeRoiDraft);

        if (slotWithDraft?.Panel.TryAdjustVolumeRoiAutoOutlineSensitivity(delta, out _) == true)
        {
            RefreshVolumeRoiDraftPanel();
            UpdateStatus();
        }
    }

    private void OnVolumeRoiPreviewPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _volumeRoiPreviewPinned = VolumeRoiDraftPinButton.IsChecked == true;
        if (!_volumeRoiPreviewPinned && _currentVolumeRoiDraftPreview is null)
        {
            HideVolumeRoiDraftPanel();
        }
        else
        {
            VolumeRoiDraftPinButton.IsChecked = _volumeRoiPreviewPinned;
        }

        SaveViewerSettings();
        e.Handled = true;
    }

    private void OnVolumeRoiRotateHorizontalClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RotateVolumeRoiDraftPreview(VolumeRoiPreviewStep, 0);
        e.Handled = true;
    }

    private void OnVolumeRoiRotateVerticalClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RotateVolumeRoiDraftPreview(0, -VolumeRoiPreviewStep);
        e.Handled = true;
    }

    private void OnVolumeRoiAutoRotateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _volumeRoiPreviewAutoRotateEnabled = VolumeRoiAutoRotateCheckBox.IsChecked == true;
        UpdateVolumeRoiAutoRotateState(resetPhase: _volumeRoiPreviewAutoRotateEnabled);
        SaveViewerSettings();
        e.Handled = true;
    }

    private void OnVolumeRoiPreviewAutoRotateTimerTick(object? sender, EventArgs e)
    {
        if (!_volumeRoiPreviewAutoRotateEnabled || _currentVolumeRoiDraftPreview is null || !VolumeRoiDraftPanel.IsVisible)
        {
            _volumeRoiPreviewAutoRotateTimer.Stop();
            return;
        }

        _volumeRoiPreviewAutoRotatePhase += 0.18;
        _volumeRoiPreviewYaw += VolumeRoiPreviewAutoRotateYawStep;
        _volumeRoiPreviewPitch = Math.Clamp(
            VolumeRoiPreviewAutoRotatePitchBase + (Math.Sin(_volumeRoiPreviewAutoRotatePhase) * VolumeRoiPreviewAutoRotatePitchAmplitude),
            -1.25,
            1.25);
        RenderVolumeRoiDraftPreview(_currentVolumeRoiDraftPreview);
    }

    private void OnVolumeRoiDraftHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!VolumeRoiDraftPanel.IsVisible || !e.GetCurrentPoint(VolumeRoiDraftDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _volumeRoiPreviewDragPointer = e.Pointer;
        _volumeRoiPreviewDragPointer.Capture(VolumeRoiDraftDragHandle);
        _volumeRoiPreviewDragStart = e.GetPosition(ViewerContentHost);
        _volumeRoiPreviewDragStartOffset = _volumeRoiPreviewOffset;
        e.Handled = true;
    }

    private void OnVolumeRoiDraftHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_volumeRoiPreviewDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _volumeRoiPreviewDragStart;
        _volumeRoiPreviewOffset = new Point(
            _volumeRoiPreviewDragStartOffset.X + delta.X,
            _volumeRoiPreviewDragStartOffset.Y + delta.Y);
        ApplyVolumeRoiDraftPanelOffset();
        e.Handled = true;
    }

    private void OnVolumeRoiDraftHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_volumeRoiPreviewDragPointer, e.Pointer))
        {
            return;
        }

        _volumeRoiPreviewDragPointer.Capture(null);
        _volumeRoiPreviewDragPointer = null;
        ApplyVolumeRoiDraftPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyVolumeRoiDraftPanelOffset()
    {
        if (VolumeRoiDraftPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureVolumeRoiDraftPanelTransform();

        double panelWidth = VolumeRoiDraftPanel.Bounds.Width;
        double panelHeight = VolumeRoiDraftPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = VolumeRoiDraftPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _volumeRoiPreviewOffset.X;
            transform.Y = _volumeRoiPreviewOffset.Y;
            return;
        }

        double defaultRight = Math.Max(0, hostWidth - panelWidth - margin.Left);
        double defaultTop = Math.Max(0, hostHeight - panelHeight - margin.Bottom);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_volumeRoiPreviewOffset.X, -overflowX, defaultRight + overflowX);
        double clampedY = Math.Clamp(_volumeRoiPreviewOffset.Y, -defaultTop - overflowY, overflowY);
        _volumeRoiPreviewOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureVolumeRoiDraftPanelTransform()
    {
        if (VolumeRoiDraftPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        VolumeRoiDraftPanel.RenderTransform = transform;
        return transform;
    }

    private bool TryCreateVolumeRoiMeasurementPreview(StudyMeasurement measurement, out DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        preview = default!;
        if (measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return false;
        }

        ViewportSlot? slot = _activeSlot is not null && SlotContainsMeasurementSource(_activeSlot, measurement)
            ? _activeSlot
            : FindSlotForMeasurement(measurement);
        if (slot?.Panel is null)
        {
            return false;
        }

        DicomSpatialMetadata? metadata = slot.CurrentSpatialMetadata;
        double currentPlanePosition = metadata is not null &&
            !string.IsNullOrWhiteSpace(measurement.FrameOfReferenceUid) &&
            string.Equals(measurement.FrameOfReferenceUid, metadata.FrameOfReferenceUid, StringComparison.Ordinal)
                ? metadata.Origin.Dot(metadata.Normal)
                : measurement.VolumeContours[0].PlanePosition;

        List<DicomViewPanel.VolumeRoiDraftPreviewContour> contours = BuildMeasurementVolumeRoiPreviewContours(measurement.VolumeContours, currentPlanePosition);
        if (contours.Count == 0)
        {
            return false;
        }

        preview = new DicomViewPanel.VolumeRoiDraftPreview(
            slot.Panel.OrientationLabel,
            measurement.VolumeContours.Count(contour => contour.IsClosed && contour.Anchors.Length >= 3),
            contours.Count(contour => contour.IsClosed),
            EstimateMeasurementVolumeCubicMillimeters(measurement.VolumeContours),
            measurement.VolumeContours.Min(contour => contour.PlanePosition),
            currentPlanePosition,
            contours);
        return true;
    }

    private static List<DicomViewPanel.VolumeRoiDraftPreviewContour> BuildMeasurementVolumeRoiPreviewContours(
        IEnumerable<VolumeRoiContour> sourceContours,
        double currentPlanePosition)
    {
        VolumeRoiContour[] contours = sourceContours
            .Where(contour => contour.Anchors.Any(anchor => anchor.PatientPoint is not null))
            .ToArray();
        if (contours.Length == 0)
        {
            return [];
        }

        int previewSampleCount = GetAdaptiveSavedVolumeRoiPreviewSampleCount(contours.Length);
        List<DicomViewPanel.VolumeRoiDraftPreviewContour> previewContours = [];
        foreach (IGrouping<int, VolumeRoiContour> componentGroup in contours.GroupBy(contour => contour.ComponentId).OrderBy(group => group.Key))
        {
            List<(VolumeRoiContour Source, SpatialVector3D[] Points)> closedContours = [];
            foreach (VolumeRoiContour contour in componentGroup
                .Where(contour => contour.IsClosed && contour.Anchors.Length >= 3)
                .OrderBy(contour => contour.PlanePosition))
            {
                SpatialVector3D[] resampled = ResampleMeasurementContour(contour, previewSampleCount);
                if (resampled.Length < 3)
                {
                    continue;
                }

                if (closedContours.Count > 0)
                {
                    resampled = AlignMeasurementContourPoints(closedContours[^1].Points, resampled);
                }

                closedContours.Add((contour, resampled));
            }

            for (int index = 0; index < closedContours.Count; index++)
            {
                (VolumeRoiContour contour, SpatialVector3D[] points) = closedContours[index];
                previewContours.Add(new DicomViewPanel.VolumeRoiDraftPreviewContour(
                    points,
                    contour.PlanePosition,
                    IsCurrentMeasurementPlane(contour.PlanePosition, currentPlanePosition),
                    true,
                    false,
                    contour.ComponentId));

                if (index >= closedContours.Count - 1)
                {
                    continue;
                }

                (VolumeRoiContour nextContour, SpatialVector3D[] nextPoints) = closedContours[index + 1];
                int sectionCount = GetMeasurementInterpolationSectionCount(Math.Abs(nextContour.PlanePosition - contour.PlanePosition));
                for (int section = 1; section < sectionCount; section++)
                {
                    double t = section / (double)sectionCount;
                    double planePosition = Lerp(contour.PlanePosition, nextContour.PlanePosition, t);
                    SpatialVector3D[] interpolatedPoints = VolumeRoiInterpolationHelper.TryInterpolateContour(
                        CreateMeasurementInterpolationInput(contour),
                        CreateMeasurementInterpolationInput(nextContour),
                        t,
                        previewSampleCount,
                        out SpatialVector3D[] maskInterpolatedPoints)
                        ? maskInterpolatedPoints
                        : InterpolateMeasurementContourPoints(points, nextPoints, t);
                    previewContours.Add(new DicomViewPanel.VolumeRoiDraftPreviewContour(
                        interpolatedPoints,
                        planePosition,
                        IsCurrentMeasurementPlane(planePosition, currentPlanePosition),
                        true,
                        true,
                        contour.ComponentId));
                }
            }
        }

        return previewContours
            .OrderBy(contour => contour.PlanePosition)
            .ThenBy(contour => contour.IsInterpolated)
            .ToList();
    }

    private static int GetAdaptiveSavedVolumeRoiPreviewSampleCount(int contourCount)
    {
        return contourCount switch
        {
            >= 72 => SavedVolumeRoiPreviewVeryLowSampleCount,
            >= 40 => SavedVolumeRoiPreviewLowSampleCount,
            >= 18 => SavedVolumeRoiPreviewMediumSampleCount,
            _ => SavedVolumeRoiPreviewHighSampleCount,
        };
    }

    private static bool IsCurrentMeasurementPlane(double planePosition, double currentPlanePosition) => Math.Abs(planePosition - currentPlanePosition) <= 0.25;

    private static SpatialVector3D[] ResampleMeasurementContour(VolumeRoiContour contour, int sampleCount)
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
            cumulative[index + 1] = cumulative[index] + GetMeasurementDistance(points[index], points[(index + 1) % points.Length]);
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

        if (GetMeasurementSignedContourArea(result, contour.PlaneOrigin, contour.RowDirection, contour.ColumnDirection) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static SpatialVector3D[] AlignMeasurementContourPoints(SpatialVector3D[] reference, SpatialVector3D[] candidate)
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

    private static SpatialVector3D[] InterpolateMeasurementContourPoints(SpatialVector3D[] first, SpatialVector3D[] second, double t)
    {
        int count = Math.Min(first.Length, second.Length);
        SpatialVector3D[] points = new SpatialVector3D[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = Lerp(first[index], second[index], t);
        }

        return points;
    }

    private static VolumeContourInterpolationInput CreateMeasurementInterpolationInput(VolumeRoiContour contour)
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

    private static int GetMeasurementInterpolationSectionCount(double gapMillimeters)
    {
        if (gapMillimeters <= 2)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Round(gapMillimeters / 3.0, MidpointRounding.AwayFromZero), 1, 8);
    }

    private static double EstimateMeasurementVolumeCubicMillimeters(IEnumerable<VolumeRoiContour> sourceContours)
    {
        double volume = 0;
        foreach (VolumeRoiContour[] contours in sourceContours
            .Where(contour => contour.IsClosed && contour.Anchors.Length >= 3)
            .GroupBy(contour => contour.ComponentId)
            .Select(group => group.OrderBy(contour => contour.PlanePosition).ToArray()))
        {
            if (contours.Length < 2)
            {
                continue;
            }

            for (int index = 0; index < contours.Length - 1; index++)
            {
                double areaA = Math.Abs(GetMeasurementSignedContourArea(
                    contours[index].Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
                    contours[index].PlaneOrigin,
                    contours[index].RowDirection,
                    contours[index].ColumnDirection));
                double areaB = Math.Abs(GetMeasurementSignedContourArea(
                    contours[index + 1].Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
                    contours[index + 1].PlaneOrigin,
                    contours[index + 1].RowDirection,
                    contours[index + 1].ColumnDirection));
                double thickness = Math.Abs(contours[index + 1].PlanePosition - contours[index].PlanePosition);
                volume += ((areaA + areaB) * 0.5) * thickness;
            }
        }

        return volume;
    }

    private static double GetMeasurementDistance(SpatialVector3D first, SpatialVector3D second) => (first - second).Length;

    private static double GetMeasurementSignedContourArea(
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

    private void UpdateVolumeRoiAutoRotateState(bool resetPhase = false)
    {
        if (resetPhase)
        {
            _volumeRoiPreviewAutoRotatePhase = 0;
        }

        if (_volumeRoiPreviewAutoRotateEnabled && _currentVolumeRoiDraftPreview is not null && VolumeRoiDraftPanel.IsVisible)
        {
            _volumeRoiPreviewAutoRotateTimer.Start();
        }
        else
        {
            _volumeRoiPreviewAutoRotateTimer.Stop();
        }
    }

    private void RotateVolumeRoiDraftPreview(double yawDelta, double pitchDelta)
    {
        if (_currentVolumeRoiDraftPreview is null || !VolumeRoiDraftPanel.IsVisible)
        {
            return;
        }

        _volumeRoiPreviewYaw += yawDelta;
        _volumeRoiPreviewPitch = Math.Clamp(_volumeRoiPreviewPitch + pitchDelta, -1.25, 1.25);
        RenderVolumeRoiDraftPreview(_currentVolumeRoiDraftPreview);
    }

    private bool TryRotateVolumeRoiDraftPreview(Key key, bool accelerate)
    {
        if (_currentVolumeRoiDraftPreview is null || !VolumeRoiDraftPanel.IsVisible)
        {
            return false;
        }

        double delta = accelerate ? VolumeRoiPreviewAcceleratedStep : VolumeRoiPreviewStep;
        switch (key)
        {
            case Key.Left:
                RotateVolumeRoiDraftPreview(-delta, 0);
                break;
            case Key.Right:
                RotateVolumeRoiDraftPreview(delta, 0);
                break;
            case Key.Up:
                RotateVolumeRoiDraftPreview(0, -delta);
                break;
            case Key.Down:
                RotateVolumeRoiDraftPreview(0, delta);
                break;
            default:
                return false;
        }

        return true;
    }

    private void RenderVolumeRoiDraftPreview(DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        VolumeRoiDraftCanvas.Children.Clear();
        if (preview.Contours.Count == 0)
        {
            return;
        }

        List<SpatialVector3D> allPoints = preview.Contours.SelectMany(contour => contour.PatientPoints).ToList();
        if (allPoints.Count == 0)
        {
            return;
        }

        SpatialVector3D center = new(
            allPoints.Average(point => point.X),
            allPoints.Average(point => point.Y),
            allPoints.Average(point => point.Z));

        List<PreviewContourGeometry> contourGeometry = preview.Contours
            .Select(contour => new PreviewContourGeometry(
                contour,
                contour.PatientPoints.Select(point => RotateVolumeRoiPoint(point - center)).ToArray()))
            .Where(geometry => geometry.Points.Length > 0)
            .OrderBy(geometry => geometry.Contour.PlanePosition)
            .ToList();
        if (contourGeometry.Count == 0)
        {
            return;
        }

        List<PreviewTriangle> triangles = BuildPreviewTriangles(contourGeometry);
        IEnumerable<Point> projectedPoints = contourGeometry.SelectMany(contour => contour.Points.Select(ProjectVolumeRoiPoint));
        double minX = projectedPoints.Min(point => point.X);
        double maxX = projectedPoints.Max(point => point.X);
        double minY = projectedPoints.Min(point => point.Y);
        double maxY = projectedPoints.Max(point => point.Y);
        double width = Math.Max(1, maxX - minX);
        double height = Math.Max(1, maxY - minY);
        double canvasWidth = VolumeRoiDraftCanvas.Width;
        double canvasHeight = VolumeRoiDraftCanvas.Height;
        double scale = Math.Min((canvasWidth - 24) / width, (canvasHeight - 24) / height);
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        double offsetX = 12 + (((canvasWidth - 24) - (width * scale)) * 0.5);
        double offsetY = 12 + (((canvasHeight - 24) - (height * scale)) * 0.5);

        foreach (PreviewTriangle triangle in triangles.OrderBy(triangle => triangle.Depth))
        {
            Point[] transformed =
            [
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.A), minX, minY, scale, offsetX, offsetY),
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.B), minX, minY, scale, offsetX, offsetY),
                TransformPreviewPoint(ProjectVolumeRoiPoint(triangle.C), minX, minY, scale, offsetX, offsetY),
            ];

            Color fillColor = GetVolumeRoiPreviewColor(preview.FirstPlanePosition, triangle.PlanePosition, triangle.IsCurrentSlice, triangle.IsInterpolated);
            fillColor = ApplyPreviewLighting(fillColor, triangle.Shading);

            var polygon = new Polygon
            {
                Points = new Points(transformed),
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(Color.FromArgb(110, fillColor.R, fillColor.G, fillColor.B)),
                StrokeThickness = triangle.IsCurrentSlice ? 1.1 : 0.7,
            };
            VolumeRoiDraftCanvas.Children.Add(polygon);
        }

        foreach (PreviewContourGeometry contour in contourGeometry)
        {
            Color strokeColor = GetVolumeRoiPreviewColor(preview.FirstPlanePosition, contour.Contour.PlanePosition, contour.Contour.IsCurrentSlice, contour.Contour.IsInterpolated);
            var polyline = new Polyline
            {
                Points = new Points(contour.Points.Select(point =>
                    TransformPreviewPoint(ProjectVolumeRoiPoint(point), minX, minY, scale, offsetX, offsetY))),
                Stroke = new SolidColorBrush(Color.FromArgb(
                    contour.Contour.IsInterpolated ? (byte)80 : (byte)180,
                    strokeColor.R,
                    strokeColor.G,
                    strokeColor.B)),
                StrokeThickness = contour.Contour.IsCurrentSlice ? 2.2 : contour.Contour.IsInterpolated ? 1.0 : 1.4,
            };
            VolumeRoiDraftCanvas.Children.Add(polyline);

            if (contour.Contour.IsClosed && polyline.Points.Count >= 3)
            {
                VolumeRoiDraftCanvas.Children.Add(new Line
                {
                    StartPoint = polyline.Points[^1],
                    EndPoint = polyline.Points[0],
                    Stroke = polyline.Stroke,
                    StrokeThickness = polyline.StrokeThickness,
                });
            }
        }
    }

    private SpatialVector3D RotateVolumeRoiPoint(SpatialVector3D point)
    {
        double cosYaw = Math.Cos(_volumeRoiPreviewYaw);
        double sinYaw = Math.Sin(_volumeRoiPreviewYaw);
        double cosPitch = Math.Cos(_volumeRoiPreviewPitch);
        double sinPitch = Math.Sin(_volumeRoiPreviewPitch);

        double x1 = (point.X * cosYaw) - (point.Z * sinYaw);
        double z1 = (point.X * sinYaw) + (point.Z * cosYaw);
        double y1 = point.Y;
        double y2 = (y1 * cosPitch) - (z1 * sinPitch);
        double z2 = (y1 * sinPitch) + (z1 * cosPitch);

        return new SpatialVector3D(x1, y2, z2);
    }

    private static Point ProjectVolumeRoiPoint(SpatialVector3D point) => new(point.X, point.Y);

    private static Point TransformPreviewPoint(Point point, double minX, double minY, double scale, double offsetX, double offsetY) =>
        new(
            offsetX + ((point.X - minX) * scale),
            offsetY + ((point.Y - minY) * scale));

    private static List<PreviewTriangle> BuildPreviewTriangles(IReadOnlyList<PreviewContourGeometry> contours)
    {
        List<PreviewTriangle> triangles = [];

        foreach (List<PreviewContourGeometry> closedContours in contours
            .Where(contour => contour.Contour.IsClosed && contour.Points.Length >= 3)
            .GroupBy(contour => contour.Contour.ComponentId)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(contour => contour.Contour.PlanePosition).ToList()))
        {
            for (int contourIndex = 0; contourIndex < closedContours.Count - 1; contourIndex++)
            {
                PreviewContourGeometry first = closedContours[contourIndex];
                PreviewContourGeometry second = closedContours[contourIndex + 1];
                int pointCount = Math.Min(first.Points.Length, second.Points.Length);
                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    SpatialVector3D a0 = first.Points[pointIndex];
                    SpatialVector3D a1 = first.Points[(pointIndex + 1) % pointCount];
                    SpatialVector3D b0 = second.Points[pointIndex];
                    SpatialVector3D b1 = second.Points[(pointIndex + 1) % pointCount];
                    double planePosition = (first.Contour.PlanePosition + second.Contour.PlanePosition) * 0.5;
                    bool isCurrentSlice = first.Contour.IsCurrentSlice || second.Contour.IsCurrentSlice;
                    bool isInterpolated = first.Contour.IsInterpolated || second.Contour.IsInterpolated;

                    triangles.Add(CreatePreviewTriangle(a0, a1, b1, planePosition, isCurrentSlice, isInterpolated));
                    triangles.Add(CreatePreviewTriangle(a0, b1, b0, planePosition, isCurrentSlice, isInterpolated));
                }
            }

            if (closedContours.Count > 0)
            {
                AddContourCapTriangles(triangles, closedContours[0]);
                if (closedContours.Count > 1)
                {
                    AddContourCapTriangles(triangles, closedContours[^1]);
                }
            }
        }

        return triangles;
    }

    private static void AddContourCapTriangles(List<PreviewTriangle> triangles, PreviewContourGeometry contour)
    {
        if (contour.Points.Length < 3)
        {
            return;
        }

        SpatialVector3D center = new(
            contour.Points.Average(point => point.X),
            contour.Points.Average(point => point.Y),
            contour.Points.Average(point => point.Z));

        for (int index = 0; index < contour.Points.Length; index++)
        {
            SpatialVector3D first = contour.Points[index];
            SpatialVector3D second = contour.Points[(index + 1) % contour.Points.Length];
            triangles.Add(CreatePreviewTriangle(center, first, second, contour.Contour.PlanePosition, contour.Contour.IsCurrentSlice, contour.Contour.IsInterpolated));
        }
    }

    private static PreviewTriangle CreatePreviewTriangle(SpatialVector3D a, SpatialVector3D b, SpatialVector3D c, double planePosition, bool isCurrentSlice, bool isInterpolated)
    {
        SpatialVector3D normal = (b - a).Cross(c - a).Normalize();
        double shading = Math.Clamp(0.45 + (0.4 * Math.Abs(normal.Z)) + (0.15 * Math.Max(0, normal.Y)), 0.2, 1.0);
        return new PreviewTriangle(a, b, c, planePosition, isCurrentSlice, isInterpolated, (a.Z + b.Z + c.Z) / 3.0, shading);
    }

    private static Color GetVolumeRoiPreviewColor(double firstPlanePosition, double planePosition, bool isCurrentSlice, bool isInterpolated)
    {
        Color color = Math.Abs(planePosition - firstPlanePosition) <= 0.25
            ? Color.Parse("#FFFFD54F")
            : planePosition > firstPlanePosition
                ? Color.Parse("#FFFF8A8A")
                : Color.Parse("#FF7FB7FF");

        byte alpha = isCurrentSlice
            ? (byte)220
            : isInterpolated
                ? (byte)88
                : (byte)145;

        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color ApplyPreviewLighting(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0, 1.25);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
    }

    private sealed record PreviewContourGeometry(
        DicomViewPanel.VolumeRoiDraftPreviewContour Contour,
        SpatialVector3D[] Points);

    private sealed record PreviewTriangle(
        SpatialVector3D A,
        SpatialVector3D B,
        SpatialVector3D C,
        double PlanePosition,
        bool IsCurrentSlice,
        bool IsInterpolated,
        double Depth,
        double Shading);
}
