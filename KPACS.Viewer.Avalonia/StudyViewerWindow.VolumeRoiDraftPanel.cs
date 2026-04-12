using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private const double VolumeRoiPreviewAutoRotateYawStep = 0.020;
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
    private WriteableBitmap? _volumeRoiPreviewBitmap;
    private VolumeRoiMeshCache? _volumeRoiMeshCache;

    private void ScheduleVolumeRoiDraftPanelRefresh()
    {
        _volumeRoiDraftPanelRefreshTimer.Stop();
        _volumeRoiDraftPanelRefreshTimer.Start();
    }

    private void InitializeVolumeRoiDraftPreviewControls()
    {
        _volumeRoiPreviewAutoRotateTimer.Interval = TimeSpan.FromMilliseconds(33);
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
        VolumeRoiDraftImage.Source = null;
        _volumeRoiMeshCache = null;
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

        _volumeRoiPreviewAutoRotatePhase += 0.066;
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

        VolumeRoiContour[] sourceContours = measurement.VolumeContours;
        (double? clipMin, double? clipMax) = GetCenterlineSeedPlanePositionRange(sourceContours);
        if (clipMin is not null && clipMax is not null)
        {
            double lo = Math.Min(clipMin.Value, clipMax.Value);
            double hi = Math.Max(clipMin.Value, clipMax.Value);
            sourceContours = sourceContours
                .Where(c => c.PlanePosition >= lo && c.PlanePosition <= hi)
                .ToArray();
            if (sourceContours.Length == 0)
            {
                sourceContours = measurement.VolumeContours;
            }
        }

        List<DicomViewPanel.VolumeRoiDraftPreviewContour> contours = BuildMeasurementVolumeRoiPreviewContours(sourceContours, currentPlanePosition);
        if (contours.Count == 0)
        {
            return false;
        }

        preview = new DicomViewPanel.VolumeRoiDraftPreview(
            slot.Panel.OrientationLabel,
            sourceContours.Count(contour => contour.IsClosed && contour.Anchors.Length >= 3),
            contours.Count(contour => contour.IsClosed),
            EstimateMeasurementVolumeCubicMillimeters(sourceContours),
            sourceContours.Min(contour => contour.PlanePosition),
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

    /// <summary>
    /// Returns the plane-position range spanned by the active centerline
    /// seed set (start → end), projected onto the contour normal.  This is
    /// used to clip the 3D ROI preview and the segmentation mask so only
    /// the portion between the two seeds is rendered / rasterized.
    /// </summary>
    private (double? Min, double? Max) GetCenterlineSeedPlanePositionRange(
        IReadOnlyList<VolumeRoiContour> contours)
    {
        if (_selectedCenterlineSeedSetId is not Guid seedSetId ||
            !_centerlineSeedSets.TryGetValue(seedSetId, out CenterlineSeedSet? seedSet) ||
            seedSet.StartSeed is null ||
            seedSet.EndSeed is null ||
            contours.Count == 0)
        {
            return (null, null);
        }

        // Use the contour normal to project seed patient-space points to
        // plane-position values comparable with VolumeRoiContour.PlanePosition.
        SpatialVector3D normal = contours[0].Normal.Normalize();
        if (normal.Length < 0.5)
        {
            return (null, null);
        }

        SpatialVector3D startPt = seedSet.StartSeed.PatientPoint;
        SpatialVector3D endPt = seedSet.EndSeed.PatientPoint;
        double startPos = startPt.Dot(normal);
        double endPos = endPt.Dot(normal);

        // Add a small margin (half a typical slice thickness) so the end
        // contours aren't accidentally trimmed by float rounding.
        double margin = contours.Count >= 2
            ? Math.Abs(contours[^1].PlanePosition - contours[0].PlanePosition) / Math.Max(1, contours.Count - 1) * 0.6
            : 2.0;

        double lo = Math.Min(startPos, endPos) - margin;
        double hi = Math.Max(startPos, endPos) + margin;
        return (lo, hi);
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

        return Math.Clamp((int)Math.Round(gapMillimeters / 3.0, MidpointRounding.AwayFromZero), 1, 24);
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
        const int bitmapWidth = 320;
        const int bitmapHeight = 160;
        const double margin = 12;

        if (preview.Contours.Count == 0)
        {
            VolumeRoiDraftImage.Source = null;
            return;
        }

        if (!ReferenceEquals(_volumeRoiMeshCache?.Preview, preview))
        {
            _volumeRoiMeshCache = BuildVolumeRoiMeshCache(preview);
        }

        VolumeRoiMeshCache cache = _volumeRoiMeshCache;
        if (cache.Vertices.Length == 0)
        {
            VolumeRoiDraftImage.Source = null;
            return;
        }

        double cosYaw = Math.Cos(_volumeRoiPreviewYaw);
        double sinYaw = Math.Sin(_volumeRoiPreviewYaw);
        double cosPitch = Math.Cos(_volumeRoiPreviewPitch);
        double sinPitch = Math.Sin(_volumeRoiPreviewPitch);

        int vertexCount = cache.Vertices.Length;
        float[] sx = new float[vertexCount];
        float[] sy = new float[vertexCount];
        float[] sz = new float[vertexCount];

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < vertexCount; i++)
        {
            SpatialVector3D p = cache.Vertices[i];
            double x1 = (p.X * cosYaw) - (p.Z * sinYaw);
            double z1 = (p.X * sinYaw) + (p.Z * cosYaw);
            double y2 = (p.Y * cosPitch) - (z1 * sinPitch);
            double z2 = (p.Y * sinPitch) + (z1 * cosPitch);
            sx[i] = (float)x1;
            sy[i] = (float)y2;
            sz[i] = (float)z2;
            if (x1 < minX) minX = x1;
            if (x1 > maxX) maxX = x1;
            if (y2 < minY) minY = y2;
            if (y2 > maxY) maxY = y2;
        }

        double extentW = Math.Max(1, maxX - minX);
        double extentH = Math.Max(1, maxY - minY);
        double scale = Math.Min((bitmapWidth - 2 * margin) / extentW, (bitmapHeight - 2 * margin) / extentH);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        double offsetX = margin + (((bitmapWidth - 2 * margin) - (extentW * scale)) * 0.5);
        double offsetY = margin + (((bitmapHeight - 2 * margin) - (extentH * scale)) * 0.5);
        float fMinX = (float)minX;
        float fMinY = (float)minY;
        float fScale = (float)scale;
        float fOffX = (float)offsetX;
        float fOffY = (float)offsetY;

        float[] px = new float[vertexCount];
        float[] py = new float[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            px[i] = fOffX + ((sx[i] - fMinX) * fScale);
            py[i] = fOffY + ((sy[i] - fMinY) * fScale);
        }

        int pixelCount = bitmapWidth * bitmapHeight;
        uint[] pixels = new uint[pixelCount];
        float[] depthBuf = new float[pixelCount];
        Array.Fill(depthBuf, float.NegativeInfinity);

        for (int ti = 0; ti < cache.Triangles.Length; ti++)
        {
            ref readonly CachedTriangleData tri = ref cache.Triangles[ti];
            float ax = sx[tri.A], ay = sy[tri.A], az = sz[tri.A];
            float bx = sx[tri.B], by = sy[tri.B], bz = sz[tri.B];
            float cx = sx[tri.C], cy = sy[tri.C], cz = sz[tri.C];

            float nx = ((by - ay) * (cz - az)) - ((bz - az) * (cy - ay));
            float ny = ((bz - az) * (cx - ax)) - ((bx - ax) * (cz - az));
            float nz = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            float nLen = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (nLen > 1e-8f)
            {
                nz /= nLen;
                ny /= nLen;
            }
            else
            {
                nz = 0;
                ny = 0;
            }

            double shading = Math.Clamp(0.45 + (0.4 * Math.Abs(nz)) + (0.15 * Math.Max(0, ny)), 0.2, 1.0);
            Color baseColor = GetVolumeRoiPreviewColor(cache.FirstPlanePosition, tri.PlanePosition, tri.IsCurrentSlice, tri.IsInterpolated);
            Color litColor = ApplyPreviewLighting(baseColor, shading);
            uint pixel = ToPremultipliedBgra(litColor);

            RasterizeTriangle(
                pixels, depthBuf, bitmapWidth, bitmapHeight,
                px[tri.A], py[tri.A], sz[tri.A],
                px[tri.B], py[tri.B], sz[tri.B],
                px[tri.C], py[tri.C], sz[tri.C],
                pixel);
        }

        for (int ci = 0; ci < cache.Contours.Length; ci++)
        {
            ref readonly CachedContourData contour = ref cache.Contours[ci];
            Color strokeBase = GetVolumeRoiPreviewColor(cache.FirstPlanePosition, contour.PlanePosition, contour.IsCurrentSlice, contour.IsInterpolated);
            byte alpha = contour.IsInterpolated ? (byte)80 : (byte)180;
            uint lineColor = ToPremultipliedBgra(Color.FromArgb(alpha, strokeBase.R, strokeBase.G, strokeBase.B));

            int end = contour.IsClosed ? contour.VertexCount : contour.VertexCount - 1;
            for (int pi = 0; pi < end; pi++)
            {
                int idxA = contour.VertexOffset + pi;
                int idxB = contour.VertexOffset + ((pi + 1) % contour.VertexCount);
                DrawLine(pixels, bitmapWidth, bitmapHeight,
                    (int)px[idxA], (int)py[idxA],
                    (int)px[idxB], (int)py[idxB],
                    lineColor);
            }
        }

        EnsureVolumeRoiPreviewBitmap(bitmapWidth, bitmapHeight);
        using (ILockedFramebuffer framebuffer = _volumeRoiPreviewBitmap!.Lock())
        {
            int rowBytes = bitmapWidth * 4;
            if (framebuffer.RowBytes == rowBytes)
            {
                Marshal.Copy(MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(), 0, framebuffer.Address, pixelCount * 4);
            }
            else
            {
                byte[] raw = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
                for (int row = 0; row < bitmapHeight; row++)
                {
                    Marshal.Copy(raw, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
                }
            }
        }

        VolumeRoiDraftImage.Source = _volumeRoiPreviewBitmap;
        VolumeRoiDraftImage.InvalidateVisual();
    }

    private void EnsureVolumeRoiPreviewBitmap(int width, int height)
    {
        if (_volumeRoiPreviewBitmap is not null &&
            _volumeRoiPreviewBitmap.PixelSize.Width == width &&
            _volumeRoiPreviewBitmap.PixelSize.Height == height)
        {
            return;
        }

        _volumeRoiPreviewBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
    }

    private static VolumeRoiMeshCache BuildVolumeRoiMeshCache(DicomViewPanel.VolumeRoiDraftPreview preview)
    {
        List<SpatialVector3D> allPoints = preview.Contours.SelectMany(contour => contour.PatientPoints).ToList();
        if (allPoints.Count == 0)
        {
            return new VolumeRoiMeshCache(preview, 0, [], [], []);
        }

        SpatialVector3D center = new(
            allPoints.Average(p => p.X),
            allPoints.Average(p => p.Y),
            allPoints.Average(p => p.Z));

        List<SpatialVector3D> vertices = [];
        List<(int Offset, int Count, DicomViewPanel.VolumeRoiDraftPreviewContour Contour)> contourMeta = [];

        foreach (DicomViewPanel.VolumeRoiDraftPreviewContour contour in preview.Contours
            .Where(c => c.PatientPoints.Count > 0)
            .OrderBy(c => c.PlanePosition))
        {
            int offset = vertices.Count;
            foreach (SpatialVector3D pt in contour.PatientPoints)
            {
                vertices.Add(pt - center);
            }

            contourMeta.Add((offset, contour.PatientPoints.Count, contour));
        }

        List<CachedTriangleData> triangles = [];

        foreach (List<(int Offset, int Count, DicomViewPanel.VolumeRoiDraftPreviewContour Contour)> closedGroup in contourMeta
            .Where(ci => ci.Contour.IsClosed && ci.Count >= 3)
            .GroupBy(ci => ci.Contour.ComponentId)
            .Select(g => g.OrderBy(ci => ci.Contour.PlanePosition).ToList()))
        {
            for (int ci = 0; ci < closedGroup.Count - 1; ci++)
            {
                var first = closedGroup[ci];
                var second = closedGroup[ci + 1];
                int pointCount = Math.Min(first.Count, second.Count);
                double planePos = (first.Contour.PlanePosition + second.Contour.PlanePosition) * 0.5;
                bool isCurrent = first.Contour.IsCurrentSlice || second.Contour.IsCurrentSlice;
                bool isInterp = first.Contour.IsInterpolated || second.Contour.IsInterpolated;

                for (int pi = 0; pi < pointCount; pi++)
                {
                    int a0 = first.Offset + pi;
                    int a1 = first.Offset + ((pi + 1) % pointCount);
                    int b0 = second.Offset + pi;
                    int b1 = second.Offset + ((pi + 1) % pointCount);
                    triangles.Add(new CachedTriangleData(a0, a1, b1, planePos, isCurrent, isInterp));
                    triangles.Add(new CachedTriangleData(a0, b1, b0, planePos, isCurrent, isInterp));
                }
            }

            if (closedGroup.Count > 0)
            {
                AddCachedCapTriangles(triangles, vertices, closedGroup[0]);
                if (closedGroup.Count > 1)
                {
                    AddCachedCapTriangles(triangles, vertices, closedGroup[^1]);
                }
            }
        }

        CachedContourData[] contours = contourMeta.Select(ci => new CachedContourData(
            ci.Offset, ci.Count,
            ci.Contour.PlanePosition, ci.Contour.IsCurrentSlice,
            ci.Contour.IsClosed, ci.Contour.IsInterpolated))
            .ToArray();

        return new VolumeRoiMeshCache(preview, preview.FirstPlanePosition, vertices.ToArray(), triangles.ToArray(), contours);
    }

    private static void AddCachedCapTriangles(
        List<CachedTriangleData> triangles,
        List<SpatialVector3D> vertices,
        (int Offset, int Count, DicomViewPanel.VolumeRoiDraftPreviewContour Contour) cap)
    {
        if (cap.Count < 3)
        {
            return;
        }

        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < cap.Count; i++)
        {
            SpatialVector3D v = vertices[cap.Offset + i];
            cx += v.X;
            cy += v.Y;
            cz += v.Z;
        }

        int centerIdx = vertices.Count;
        vertices.Add(new SpatialVector3D(cx / cap.Count, cy / cap.Count, cz / cap.Count));

        for (int i = 0; i < cap.Count; i++)
        {
            int first = cap.Offset + i;
            int second = cap.Offset + ((i + 1) % cap.Count);
            triangles.Add(new CachedTriangleData(
                centerIdx, first, second,
                cap.Contour.PlanePosition, cap.Contour.IsCurrentSlice, cap.Contour.IsInterpolated));
        }
    }

    private static void RasterizeTriangle(
        uint[] pixels, float[] depth,
        int width, int height,
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        uint color)
    {
        int minPx = Math.Max(0, (int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))));
        int maxPx = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int minPy = Math.Max(0, (int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))));
        int maxPy = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))));

        float denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
        if (MathF.Abs(denom) < 1e-6f)
        {
            return;
        }

        float invDenom = 1.0f / denom;

        for (int py = minPy; py <= maxPy; py++)
        {
            float ey = py + 0.5f;
            for (int px = minPx; px <= maxPx; px++)
            {
                float ex = px + 0.5f;
                float w0 = (((y1 - y2) * (ex - x2)) + ((x2 - x1) * (ey - y2))) * invDenom;
                float w1 = (((y2 - y0) * (ex - x2)) + ((x0 - x2) * (ey - y2))) * invDenom;
                float w2 = 1.0f - w0 - w1;

                if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                {
                    float z = (w0 * z0) + (w1 * z1) + (w2 * z2);
                    int idx = (py * width) + px;
                    if (z > depth[idx])
                    {
                        depth[idx] = z;
                        pixels[idx] = color;
                    }
                }
            }
        }
    }

    private static void DrawLine(uint[] pixels, int width, int height, int x0, int y0, int x1, int y1, uint color)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                pixels[(y0 * width) + x0] = color;
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static uint ToPremultipliedBgra(Color c)
    {
        float a = c.A / 255f;
        return (uint)(
            (byte)(c.B * a) |
            ((uint)(byte)(c.G * a) << 8) |
            ((uint)(byte)(c.R * a) << 16) |
            ((uint)c.A << 24));
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

    private sealed record VolumeRoiMeshCache(
        DicomViewPanel.VolumeRoiDraftPreview Preview,
        double FirstPlanePosition,
        SpatialVector3D[] Vertices,
        CachedTriangleData[] Triangles,
        CachedContourData[] Contours);

    private readonly record struct CachedTriangleData(
        int A,
        int B,
        int C,
        double PlanePosition,
        bool IsCurrentSlice,
        bool IsInterpolated);

    private readonly record struct CachedContourData(
        int VertexOffset,
        int VertexCount,
        double PlanePosition,
        bool IsCurrentSlice,
        bool IsClosed,
        bool IsInterpolated);
}
