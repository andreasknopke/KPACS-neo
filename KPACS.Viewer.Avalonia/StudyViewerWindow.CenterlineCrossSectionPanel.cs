using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double CenterlineCrossSectionFieldOfViewMm = 70.0;
    private const int CenterlineCrossSectionImageSize = 220;
    private readonly byte[] _centerlineCrossSectionLutR = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCrossSectionLutG = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCrossSectionLutB = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private const int CenterlineSyncThrottleMs = 16;
    private Point _centerlineCrossSectionOffset;
    private bool _centerlineCrossSectionPinned;
    private double _centerlineCrossSectionStationNormalized;
    private WriteableBitmap? _centerlineCrossSectionBitmap;
    private byte[]? _centerlineCrossSectionRenderBuffer;
    private IPointer? _centerlineCrossSectionDragPointer;
    private Point _centerlineCrossSectionDragStart;
    private Point _centerlineCrossSectionDragStartOffset;
    private bool _isUpdatingCenterlineCrossSectionSlider;
    private readonly Avalonia.Threading.DispatcherTimer _centerlineSyncDebounceTimer = new();
    private bool _centerlineSyncThrottleActive;
    private ViewportSlot? _pendingCenterlineSyncSlot;
    private SeriesVolume? _pendingCenterlineSyncVolume;
    private SpatialVector3D? _pendingCenterlineSyncPatientPoint;
    private CancellationTokenSource? _centerlineCrossSectionRenderCancellation;
    private int _centerlineCrossSectionRenderVersion;

    private void RefreshCenterlineCrossSectionPanel()
    {
        if (CenterlineCrossSectionPanel is null ||
            CenterlineCrossSectionSlider is null ||
            CenterlineCrossSectionPinButton is null ||
            CenterlineCrossSectionTitleText is null ||
            CenterlineCrossSectionSummaryText is null ||
            CenterlineCrossSectionStatusText is null ||
            CenterlineCrossSectionHintText is null ||
            CenterlineCrossSectionImage is null)
        {
            return;
        }

        if ((!_isCenterlineEditMode && !_centerlineCrossSectionPinned) ||
            !TryResolveCenterlineCrossSectionContext(out CenterlineSeedSet seedSet, out CenterlinePath path, out ViewportSlot slot, out SeriesVolume volume))
        {
            HideCenterlineCrossSectionPanel();
            return;
        }

        if (path.Points.Count == 0)
        {
            HideCenterlineCrossSectionPanel();
            return;
        }

        int stationIndex = GetCenterlineStationIndex(path, _centerlineCrossSectionStationNormalized);
        CenterlinePathPoint pathPoint = path.Points[stationIndex];

        // Update UI elements immediately (non-blocking) so the slider/labels stay responsive.
        _isUpdatingCenterlineCrossSectionSlider = true;
        try
        {
            CenterlineCrossSectionSlider.Minimum = 0;
            CenterlineCrossSectionSlider.Maximum = Math.Max(0, path.Points.Count - 1);
            CenterlineCrossSectionSlider.Value = stationIndex;
        }
        finally
        {
            _isUpdatingCenterlineCrossSectionSlider = false;
        }

        CenterlineCrossSectionPanel.IsVisible = true;
        CenterlineCrossSectionPinButton.IsChecked = _centerlineCrossSectionPinned;
        CenterlineCrossSectionTitleText.Text = "Centerline cross-section";
        CenterlineCrossSectionSummaryText.Text = $"{seedSet.Label} · {path.Summary}";
        CenterlineCrossSectionHintText.Text = "Scrub along the centerline to inspect orthogonal vessel sections. Use the marker buttons to capture neck and distal landing spans; the current station is synchronized back into the native views via the 3D cursor.";
        ApplyCenterlineCrossSectionPanelOffset();

        // Schedule the heavy rendering work off the UI thread.
        ScheduleCenterlineCrossSectionRender(seedSet, path, pathPoint, stationIndex, slot, volume);
    }

    /// <summary>
    /// Cancels any in-flight cross-section render and schedules a new one on a background thread.
    /// </summary>
    private void ScheduleCenterlineCrossSectionRender(
        CenterlineSeedSet seedSet,
        CenterlinePath path,
        CenterlinePathPoint pathPoint,
        int stationIndex,
        ViewportSlot slot,
        SeriesVolume volume)
    {
        _centerlineCrossSectionRenderCancellation?.Cancel();
        _centerlineCrossSectionRenderCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _centerlineCrossSectionRenderCancellation = cancellation;
        int version = ++_centerlineCrossSectionRenderVersion;

        _ = RenderCenterlineCrossSectionAsync(seedSet, path, pathPoint, stationIndex, slot, volume, version, cancellation.Token);
    }

    private async Task RenderCenterlineCrossSectionAsync(
        CenterlineSeedSet seedSet,
        CenterlinePath path,
        CenterlinePathPoint pathPoint,
        int stationIndex,
        ViewportSlot slot,
        SeriesVolume volume,
        int version,
        CancellationToken cancellationToken)
    {
        var stopwatch = StartVascularStopwatch();
        double rotationRadians = GetCenterlineCurvedMprRotationRadians();

        ReslicedImage resliced;
        try
        {
            resliced = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                CenterlineSampleFrame frame = CenterlineFrameBuilder.GetFrame(volume, path, stationIndex, rotationRadians);
                SpatialVector3D csRow = frame.Binormal.Length > 1e-6 ? frame.Binormal.Normalize() : new SpatialVector3D(1, 0, 0);
                SpatialVector3D csCol = frame.Normal.Length > 1e-6 ? frame.Normal.Normalize() : new SpatialVector3D(0, 1, 0);
                double csPixelSpacing = CenterlineCrossSectionFieldOfViewMm / CenterlineCrossSectionImageSize;

                cancellationToken.ThrowIfCancellationRequested();

                if (VolumeComputeBackend.TryRenderCrossSection(
                        volume,
                        pathPoint.PatientPoint,
                        csRow,
                        csCol,
                        CenterlineCrossSectionFieldOfViewMm,
                        CenterlineCrossSectionImageSize,
                        out short[] gpuPixels))
                {
                    return new ReslicedImage
                    {
                        Pixels = gpuPixels,
                        Width = CenterlineCrossSectionImageSize,
                        Height = CenterlineCrossSectionImageSize,
                        PixelSpacingX = csPixelSpacing,
                        PixelSpacingY = csPixelSpacing,
                        RenderBackendLabel = VolumeComputeBackend.CurrentStatus.DisplayName,
                    };
                }

                if (VolumeComputeBackend.CpuFallbackDisabled)
                {
                    Console.Error.WriteLine($"[CPU·BLOCKED] Cross-section fallback suppressed — returning blank image");
                    return new ReslicedImage
                    {
                        Pixels = new short[CenterlineCrossSectionImageSize * CenterlineCrossSectionImageSize],
                        Width = CenterlineCrossSectionImageSize,
                        Height = CenterlineCrossSectionImageSize,
                        PixelSpacingX = csPixelSpacing,
                        PixelSpacingY = csPixelSpacing,
                        RenderBackendLabel = "NONE (CPU disabled)",
                    };
                }

                VolumeSlicePlane plane = CreateCenterlineCrossSectionPlane(pathPoint.PatientPoint, frame);
                return VolumeReslicer.ExtractSlice(volume, plane);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || version != _centerlineCrossSectionRenderVersion)
        {
            return;
        }

        // Post results back to the UI thread.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _centerlineCrossSectionRenderVersion)
            {
                return;
            }

            RenderCenterlineCrossSectionImage(resliced);
            RecordVascularPerformanceMetric("cross-section-scrub", stopwatch.Elapsed.TotalMilliseconds);

            CenterlineCrossSectionStatusText.Text = $"Station {stationIndex + 1}/{path.Points.Count} · {pathPoint.ArcLengthMm:0.0} / {path.TotalLengthMm:0.0} mm · q={path.QualityScore:0.00} · {BuildVascularMarkerStatus(path)} · [{resliced.RenderBackendLabel}]";
            ScheduleCenterlineCrossSectionSync(slot, volume, pathPoint.PatientPoint);
        });
    }

    private void RefreshCenterlinePanels()
    {
        RefreshCenterlineCrossSectionPanel();
        RefreshCenterlineCurvedMprPanel();
        ApplyCenterlinePanelLayoutMode();
    }

    private void HideCenterlineCrossSectionPanel()
    {
        _centerlineCrossSectionRenderCancellation?.Cancel();
        if (CenterlineCrossSectionPanel is null)
        {
            return;
        }

        CenterlineCrossSectionPanel.IsVisible = false;
        CenterlineCrossSectionTitleText.Text = "Centerline cross-section";
        CenterlineCrossSectionSummaryText.Text = string.Empty;
        CenterlineCrossSectionStatusText.Text = string.Empty;
        CenterlineCrossSectionHintText.Text = "Computed centerline sections appear here.";
        CenterlineCrossSectionImage.Source = null;
        ApplyCenterlinePanelLayoutMode();
    }

    private bool TryResolveCenterlineCrossSectionContext(
        out CenterlineSeedSet seedSet,
        out CenterlinePath path,
        out ViewportSlot slot,
        out SeriesVolume volume)
    {
        seedSet = null!;
        path = null!;
        slot = null!;
        volume = null!;

        if (_selectedCenterlineSeedSetId is not Guid selectedSeedSetId ||
            !_centerlineSeedSets.TryGetValue(selectedSeedSetId, out CenterlineSeedSet? selectedSeedSet))
        {
            return false;
        }

        CenterlinePath? selectedPath = _centerlinePaths.Values
            .Where(candidate => candidate.SeedSetId == selectedSeedSet.Id && candidate.Kind == CenterlinePathKind.Computed)
            .OrderByDescending(candidate => candidate.UpdatedUtc)
            .FirstOrDefault();
        if (selectedPath is null || !selectedPath.HasRenderablePath)
        {
            return false;
        }

        Guid? segmentationMaskId = selectedPath.SegmentationMaskId ?? selectedSeedSet.SegmentationMaskId;
        SegmentationMask3D? mask = segmentationMaskId is Guid maskId && _segmentationMasks.TryGetValue(maskId, out SegmentationMask3D? resolvedMask)
            ? resolvedMask
            : null;

        ViewportSlot? resolvedSlot = _slots
            .Where(candidate => candidate.Volume is not null)
            .OrderByDescending(candidate => ReferenceEquals(candidate, _activeSlot))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Volume!.SeriesInstanceUid, mask?.SourceSeriesInstanceUid, StringComparison.Ordinal)
                || string.Equals(candidate.Volume!.FrameOfReferenceUid, mask?.SourceFrameOfReferenceUid, StringComparison.Ordinal)
                || string.Equals(candidate.Series?.SeriesInstanceUid, selectedSeedSet.StartSeed?.SeriesInstanceUid, StringComparison.Ordinal));

        if (resolvedSlot?.Volume is null)
        {
            return false;
        }

        seedSet = selectedSeedSet;
        path = selectedPath;
        slot = resolvedSlot;
        volume = resolvedSlot.Volume;
        return true;
    }

    private static int GetCenterlineStationIndex(CenterlinePath path, double normalizedStation)
    {
        if (path.Points.Count <= 1)
        {
            return 0;
        }

        int maxIndex = path.Points.Count - 1;
        return Math.Clamp((int)Math.Round(Math.Clamp(normalizedStation, 0, 1) * maxIndex), 0, maxIndex);
    }

    private int GetSelectedCenterlineStationIndex(CenterlinePath path) => GetCenterlineStationIndex(path, _centerlineCrossSectionStationNormalized);

    private static VolumeSlicePlane CreateCenterlineCrossSectionPlane(SpatialVector3D patientPoint, CenterlineSampleFrame frame)
    {
        SpatialVector3D row = frame.Binormal.Length > 1e-6 ? frame.Binormal.Normalize() : new SpatialVector3D(1, 0, 0);
        SpatialVector3D column = frame.Normal.Length > 1e-6 ? frame.Normal.Normalize() : new SpatialVector3D(0, 1, 0);
        SpatialVector3D tangent = frame.Tangent.Length > 1e-6 ? frame.Tangent.Normalize() : new SpatialVector3D(0, 0, 1);
        double pixelSpacing = CenterlineCrossSectionFieldOfViewMm / CenterlineCrossSectionImageSize;

        return new VolumeSlicePlane
        {
            VolumeCenter = patientPoint,
            RowDirection = row,
            ColumnDirection = column,
            Normal = tangent,
            PixelSpacingX = pixelSpacing,
            PixelSpacingY = pixelSpacing,
            SliceSpacingMm = 0.5,
            ScrollStepMm = 0.5,
            MinOffsetMm = 0,
            MaxOffsetMm = 0,
            CurrentOffsetMm = 0,
            SliceCount = 1,
            Width = CenterlineCrossSectionImageSize,
            Height = CenterlineCrossSectionImageSize,
        };
    }

    private void RenderCenterlineCrossSectionImage(ReslicedImage resliced)
    {
        int width = Math.Max(1, resliced.Width);
        int height = Math.Max(1, resliced.Height);
        int requiredBytes = width * height * 4;
        _centerlineCrossSectionRenderBuffer ??= new byte[requiredBytes];
        if (_centerlineCrossSectionRenderBuffer.Length < requiredBytes)
        {
            _centerlineCrossSectionRenderBuffer = new byte[requiredBytes];
        }

        (double center, double widthWindow) = ComputeAutoWindow(resliced.Pixels);
        DicomPixelRenderer.RenderRescaled16BitScaled(
            resliced.Pixels,
            width,
            height,
            center,
            widthWindow,
            _centerlineCrossSectionLutR,
            _centerlineCrossSectionLutG,
            _centerlineCrossSectionLutB,
            isMonochrome1: false,
            width,
            height,
            _centerlineCrossSectionRenderBuffer);

        EnsureCenterlineCrossSectionBitmap(width, height);
        if (_centerlineCrossSectionBitmap is null)
        {
            return;
        }

        using ILockedFramebuffer framebuffer = _centerlineCrossSectionBitmap.Lock();
        int rowBytes = width * 4;
        if (framebuffer.RowBytes == rowBytes)
        {
            Marshal.Copy(_centerlineCrossSectionRenderBuffer, 0, framebuffer.Address, requiredBytes);
        }
        else
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(_centerlineCrossSectionRenderBuffer, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
        }

        CenterlineCrossSectionImage.Source = _centerlineCrossSectionBitmap;
    }

    private void EnsureCenterlineCrossSectionBitmap(int width, int height)
    {
        if (_centerlineCrossSectionBitmap is not null &&
            _centerlineCrossSectionBitmap.PixelSize.Width == width &&
            _centerlineCrossSectionBitmap.PixelSize.Height == height)
        {
            return;
        }

        _centerlineCrossSectionBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
    }

    private static (double Center, double Width) ComputeAutoWindow(short[] pixels)
    {
        if (pixels.Length == 0)
        {
            return (0, 1);
        }

        short minimum = short.MaxValue;
        short maximum = short.MinValue;
        foreach (short pixel in pixels)
        {
            if (pixel < minimum)
            {
                minimum = pixel;
            }

            if (pixel > maximum)
            {
                maximum = pixel;
            }
        }

        double center = (minimum + maximum) * 0.5;
        double width = Math.Max(1, maximum - minimum);
        return (center, width);
    }

    private void SyncCenterlineCrossSectionCursor(ViewportSlot slot, SeriesVolume volume, SpatialVector3D patientPoint)
    {
        DicomSpatialMetadata metadata = slot.CurrentSpatialMetadata
            ?? VolumeReslicer.GetSliceSpatialMetadata(volume, SliceOrientation.Axial, GetVolumeSliceIndexForPatientPoint(volume, SliceOrientation.Axial, patientPoint));

        // Apply synchronously on the current (UI) thread so that the main viewports
        // update in the same render frame as the cross-section panel.
        ApplyCursorToSlots(volume, metadata, patientPoint);
        Broadcast3DCursor(volume, metadata, patientPoint);
    }

    private void ScheduleCenterlineCrossSectionSync(ViewportSlot slot, SeriesVolume volume, SpatialVector3D patientPoint)
    {
        _pendingCenterlineSyncSlot = slot;
        _pendingCenterlineSyncVolume = volume;
        _pendingCenterlineSyncPatientPoint = patientPoint;

        if (!_centerlineSyncThrottleActive)
        {
            // First call: fire immediately, then start cooldown.
            _centerlineSyncThrottleActive = true;
            FlushPendingCenterlineSync();
            _centerlineSyncDebounceTimer.Start();
        }
        // Else: cooldown timer running — latest params stored, will fire on next tick.
    }

    private void FlushPendingCenterlineSync()
    {
        ViewportSlot? slot = _pendingCenterlineSyncSlot;
        SeriesVolume? volume = _pendingCenterlineSyncVolume;
        SpatialVector3D? patientPoint = _pendingCenterlineSyncPatientPoint;
        _pendingCenterlineSyncSlot = null;
        _pendingCenterlineSyncVolume = null;
        _pendingCenterlineSyncPatientPoint = null;

        if (slot is not null && volume is not null && patientPoint is not null)
        {
            SyncCenterlineCrossSectionCursor(slot, volume, patientPoint.Value);
        }
    }

    private void OnCenterlineSyncDebounceTimerTick(object? sender, EventArgs e)
    {
        _centerlineSyncDebounceTimer.Stop();

        if (_pendingCenterlineSyncSlot is not null)
        {
            // More events arrived during cooldown — flush and restart.
            FlushPendingCenterlineSync();
            _centerlineSyncDebounceTimer.Start();
        }
        else
        {
            // No pending events — scrubbing stopped, release throttle.
            _centerlineSyncThrottleActive = false;
        }
    }

    private void OnCenterlineCrossSectionSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUpdatingCenterlineCrossSectionSlider || sender is not Slider slider)
        {
            return;
        }

        if (!TryResolveCenterlineCrossSectionContext(out _, out CenterlinePath path, out _, out _))
        {
            return;
        }

        int maxIndex = Math.Max(1, path.Points.Count - 1);
        _centerlineCrossSectionStationNormalized = Math.Clamp(slider.Value / maxIndex, 0, 1);
        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
    }

    private void OnCenterlineCrossSectionPreviousClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StepCenterlineCrossSection(-1);
        e.Handled = true;
    }

    private void OnCenterlineCrossSectionNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StepCenterlineCrossSection(1);
        e.Handled = true;
    }

    private void StepCenterlineCrossSection(int delta)
    {
        if (!TryResolveCenterlineCrossSectionContext(out _, out CenterlinePath path, out _, out _))
        {
            return;
        }

        int currentIndex = GetCenterlineStationIndex(path, _centerlineCrossSectionStationNormalized);
        int nextIndex = Math.Clamp(currentIndex + delta, 0, Math.Max(0, path.Points.Count - 1));
        _centerlineCrossSectionStationNormalized = path.Points.Count <= 1 ? 0 : nextIndex / (double)(path.Points.Count - 1);
        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
    }

    private void OnCenterlineCrossSectionPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _centerlineCrossSectionPinned = CenterlineCrossSectionPinButton.IsChecked == true;
        RefreshCenterlineCrossSectionPanel();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void OnCenterlineCrossSectionHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsCenterlinePanelsTiled)
        {
            return;
        }

        if (!CenterlineCrossSectionPanel.IsVisible || !e.GetCurrentPoint(CenterlineCrossSectionDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _centerlineCrossSectionDragPointer = e.Pointer;
        _centerlineCrossSectionDragPointer.Capture(CenterlineCrossSectionDragHandle);
        _centerlineCrossSectionDragStart = e.GetPosition(ViewerContentHost);
        _centerlineCrossSectionDragStartOffset = _centerlineCrossSectionOffset;
        e.Handled = true;
    }

    private void OnCenterlineCrossSectionHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCrossSectionDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _centerlineCrossSectionDragStart;
        _centerlineCrossSectionOffset = new Point(
            _centerlineCrossSectionDragStartOffset.X + delta.X,
            _centerlineCrossSectionDragStartOffset.Y + delta.Y);
        ApplyCenterlineCrossSectionPanelOffset();
        e.Handled = true;
    }

    private void OnCenterlineCrossSectionHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCrossSectionDragPointer, e.Pointer))
        {
            return;
        }

        _centerlineCrossSectionDragPointer.Capture(null);
        _centerlineCrossSectionDragPointer = null;
        ApplyCenterlineCrossSectionPanelOffset();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void ApplyCenterlineCrossSectionPanelOffset()
    {
        if (CenterlineCrossSectionPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureCenterlineCrossSectionPanelTransform();
        if (IsCenterlinePanelsTiled)
        {
            transform.X = 0;
            transform.Y = 0;
            ApplyCenterlinePanelLayoutMode();
            return;
        }

        double panelWidth = CenterlineCrossSectionPanel.Bounds.Width;
        double panelHeight = CenterlineCrossSectionPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = CenterlineCrossSectionPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _centerlineCrossSectionOffset.X;
            transform.Y = _centerlineCrossSectionOffset.Y;
            return;
        }

        double defaultRight = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_centerlineCrossSectionOffset.X, -defaultRight - overflowX, overflowX);
        double clampedY = Math.Clamp(_centerlineCrossSectionOffset.Y, -margin.Top - overflowY, defaultTop + overflowY);
        _centerlineCrossSectionOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureCenterlineCrossSectionPanelTransform()
    {
        if (CenterlineCrossSectionPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        CenterlineCrossSectionPanel.RenderTransform = transform;
        return transform;
    }
}