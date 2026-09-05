using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double CenterlineCurvedMprFieldOfViewMm = 90.0;
    private const double CenterlineCurvedMprSlabThicknessMm = 8.0;
    private const int CenterlineCurvedMprImageHeight = 241;
    private const double CenterlineCurvedMprRotationStepDegrees = 5.0;
    private readonly byte[] _centerlineCurvedMprLutR = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCurvedMprLutG = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCurvedMprLutB = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private Point _centerlineCurvedMprOffset;
    private bool _centerlineCurvedMprPinned;
    private double _centerlineCurvedMprRotationDegrees;
    private WriteableBitmap? _centerlineCurvedMprBitmap;
    private byte[]? _centerlineCurvedMprRenderBuffer;
    private IPointer? _centerlineCurvedMprDragPointer;
    private Point _centerlineCurvedMprDragStart;
    private Point _centerlineCurvedMprDragStartOffset;
    private CancellationTokenSource? _centerlineCurvedMprRenderCancellation;
    private int _centerlineCurvedMprRenderVersion;
    private bool _isUpdatingCurvedMprRotationSlider;
    private Guid? _centerlineCurvedMprRenderedPathId;
    private double _centerlineCurvedMprRenderedRotationDegrees = double.NaN;
    private CurvedMprDisplayOrientation _centerlineCurvedMprDisplayOrientation = CurvedMprDisplayOrientation.Horizontal;
    private bool _centerlineCurvedMprStationReversed;
    private const double CenterlineCurvedMprHorizontalDisplayWidth = 620;
    private const double CenterlineCurvedMprHorizontalDisplayHeight = 240;
    private const double CenterlineCurvedMprVerticalDisplayWidth = 240;
    private const double CenterlineCurvedMprVerticalDisplayHeight = 620;

    private void RefreshCenterlineCurvedMprPanel()
    {
        if (CenterlineCurvedMprPanel is null ||
            CenterlineCurvedMprPinButton is null ||
            CenterlineCurvedMprTitleText is null ||
            CenterlineCurvedMprSummaryText is null ||
            CenterlineCurvedMprStatusText is null ||
            CenterlineCurvedMprRotationValueText is null ||
            CenterlineCurvedMprRotationSlider is null ||
            CenterlineCurvedMprHintText is null ||
            CenterlineCurvedMprImage is null ||
            CenterlineCurvedMprStationIndicator is null ||
            CenterlineCurvedMprGuideCanvas is null ||
            CenterlineCurvedMprGuideLine is null)
        {
            return;
        }

        if ((!_isCenterlineEditMode && !_centerlineCurvedMprPinned) ||
            !TryResolveCenterlineCrossSectionContext(out CenterlineSeedSet seedSet, out CenterlinePath path, out _, out SeriesVolume volume) ||
            path.Points.Count == 0)
        {
            HideCenterlineCurvedMprPanel();
            return;
        }

        CenterlineCurvedMprPanel.IsVisible = true;
        CenterlineCurvedMprPinButton.IsChecked = _centerlineCurvedMprPinned;
        CenterlineCurvedMprTitleText.Text = "Curved MPR";
        CenterlineCurvedMprSummaryText.Text = $"{seedSet.Label} · {path.Summary}";
        _isUpdatingCurvedMprRotationSlider = true;
        try
        {
            CenterlineCurvedMprRotationSlider.Value = _centerlineCurvedMprRotationDegrees;
        }
        finally
        {
            _isUpdatingCurvedMprRotationSlider = false;
        }

        CenterlineCurvedMprRotationValueText.Text = BuildCenterlineCurvedMprRotationLabel();

        int stationIndex = GetSelectedCenterlineStationIndex(path);
        UpdateCenterlineCurvedMprStationIndicator(path, stationIndex);
        CenterlineCurvedMprStatusText.Text = BuildCenterlineCurvedMprStatusText(path, stationIndex);
        CenterlineCurvedMprHintText.Text = $"Rotate around the vessel axis to align the stretched CPR with the anatomy you want to inspect. Click or drag in the image to move the shared centerline station and keep the orthogonal cross-section synchronized. {BuildSelectedVascularPlanningSummary()} For guided EVAR planning (Segmentation, Centerline, Measurements, Sizing, Report) the dedicated Vascular Workspace is available via the toolbar 'Vascular' button.";
        ApplyCenterlineCurvedMprPanelOffset();
        ScheduleCenterlineCurvedMprRender(path, volume);
    }

    private void HideCenterlineCurvedMprPanel()
    {
        _centerlineCurvedMprRenderCancellation?.Cancel();
        if (CenterlineCurvedMprPanel is null)
        {
            return;
        }

        CenterlineCurvedMprPanel.IsVisible = false;
        CenterlineCurvedMprTitleText.Text = "Curved MPR";
        CenterlineCurvedMprSummaryText.Text = string.Empty;
        CenterlineCurvedMprStatusText.Text = string.Empty;
        CenterlineCurvedMprHintText.Text = "Computed curved MPR appears here.";
        _centerlineCurvedMprDisplayOrientation = CurvedMprDisplayOrientation.Horizontal;
        _centerlineCurvedMprRenderedRotationDegrees = double.NaN;
        ApplyCenterlineCurvedMprDisplayOrientation(_centerlineCurvedMprDisplayOrientation);
        CenterlineCurvedMprImage.Source = null;
        CenterlineCurvedMprStationIndicator.Margin = new Thickness(0, 0, 0, 0);
        ApplyCenterlinePanelLayoutMode();
    }

    private void ScheduleCenterlineCurvedMprRender(CenterlinePath path, SeriesVolume volume)
    {
        double rotationDegrees = NormalizeCenterlineCurvedMprRotation(_centerlineCurvedMprRotationDegrees);
        if (_centerlineCurvedMprRenderedPathId == path.Id &&
            CenterlineCurvedMprImage.Source is not null &&
            Math.Abs(_centerlineCurvedMprRenderedRotationDegrees - rotationDegrees) <= 1e-6)
        {
            return;
        }

        _centerlineCurvedMprRenderCancellation?.Cancel();
        _centerlineCurvedMprRenderCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _centerlineCurvedMprRenderCancellation = cancellation;
        int version = ++_centerlineCurvedMprRenderVersion;
        _ = RenderCenterlineCurvedMprAsync(path, volume, version, rotationDegrees, cancellation.Token);
    }

    private async Task RenderCenterlineCurvedMprAsync(CenterlinePath path, SeriesVolume volume, int version, double rotationDegrees, CancellationToken cancellationToken)
    {
        var stopwatch = StartVascularStopwatch();
        CurvedMprRenderResult renderResult;
        try
        {
            renderResult = await Task.Run(
                () => CenterlineCurvedMprRenderer.Render(
                    volume,
                    path,
                    CenterlineCurvedMprFieldOfViewMm,
                    CenterlineCurvedMprImageHeight,
                    CenterlineCurvedMprSlabThicknessMm,
                    rotationDegrees,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _centerlineCurvedMprRenderVersion && CenterlineCurvedMprStatusText is not null)
                {
                    CenterlineCurvedMprStatusText.Text = "Curved MPR rendering failed unexpectedly.";
                }
            });
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _centerlineCurvedMprRenderVersion || CenterlineCurvedMprImage is null)
            {
                return;
            }

            RenderCenterlineCurvedMprBitmap(renderResult);
            _centerlineCurvedMprDisplayOrientation = renderResult.Orientation;
            _centerlineCurvedMprStationReversed = renderResult.IsStationReversed;
            ApplyCenterlineCurvedMprDisplayOrientation(_centerlineCurvedMprDisplayOrientation);
            _centerlineCurvedMprRenderedPathId = path.Id;
            _centerlineCurvedMprRenderedRotationDegrees = rotationDegrees;

            string gpuError = VolumeComputeBackend.LastRenderError ?? string.Empty;
            string backendInfo = string.IsNullOrEmpty(gpuError)
                ? $"[{renderResult.RenderBackendLabel}]"
                : $"[{renderResult.RenderBackendLabel}] ⚠ {gpuError}";
            if (CenterlineCurvedMprStatusText is not null)
            {
                int stationIndex = GetSelectedCenterlineStationIndex(path);
                CenterlineCurvedMprStatusText.Text = $"{BuildCenterlineCurvedMprStatusText(path, stationIndex)} · {backendInfo}";
            }

            RecordVascularPerformanceMetric("curved-mpr-update", stopwatch.Elapsed.TotalMilliseconds);
        });
    }

    private void RenderCenterlineCurvedMprBitmap(CurvedMprRenderResult renderResult)
    {
        int width = Math.Max(1, renderResult.Width);
        int height = Math.Max(1, renderResult.Height);
        int requiredBytes = width * height * 4;
        _centerlineCurvedMprRenderBuffer ??= new byte[requiredBytes];
        if (_centerlineCurvedMprRenderBuffer.Length < requiredBytes)
        {
            _centerlineCurvedMprRenderBuffer = new byte[requiredBytes];
        }

        (double center, double widthWindow) = ComputeAutoWindow(renderResult.Pixels);
        DicomPixelRenderer.RenderRescaled16BitScaled(
            renderResult.Pixels,
            width,
            height,
            center,
            widthWindow,
            _centerlineCurvedMprLutR,
            _centerlineCurvedMprLutG,
            _centerlineCurvedMprLutB,
            isMonochrome1: false,
            width,
            height,
            _centerlineCurvedMprRenderBuffer);

        EnsureCenterlineCurvedMprBitmap(width, height);
        if (_centerlineCurvedMprBitmap is null)
        {
            return;
        }

        using ILockedFramebuffer framebuffer = _centerlineCurvedMprBitmap.Lock();
        int rowBytes = width * 4;
        if (framebuffer.RowBytes == rowBytes)
        {
            Marshal.Copy(_centerlineCurvedMprRenderBuffer, 0, framebuffer.Address, requiredBytes);
        }
        else
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(_centerlineCurvedMprRenderBuffer, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
        }

        CenterlineCurvedMprImage.Source = _centerlineCurvedMprBitmap;
        CenterlineCurvedMprImage.InvalidateVisual();
    }

    private void EnsureCenterlineCurvedMprBitmap(int width, int height)
    {
        if (_centerlineCurvedMprBitmap is not null &&
            _centerlineCurvedMprBitmap.PixelSize.Width == width &&
            _centerlineCurvedMprBitmap.PixelSize.Height == height)
        {
            return;
        }

        _centerlineCurvedMprBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
    }

    private void UpdateCenterlineCurvedMprStationIndicator(CenterlinePath path, int stationIndex)
    {
        if (CenterlineCurvedMprImageHost is null || CenterlineCurvedMprStationIndicator is null)
        {
            return;
        }

        double hostWidth = CenterlineCurvedMprImageHost.Bounds.Width;
        double hostHeight = CenterlineCurvedMprImageHost.Bounds.Height;
        if ((hostWidth <= 1 && hostHeight <= 1) || path.Points.Count <= 1)
        {
            CenterlineCurvedMprStationIndicator.Margin = new Thickness(0, 0, 0, 0);
            return;
        }

        if (_centerlineCurvedMprDisplayOrientation == CurvedMprDisplayOrientation.Vertical)
        {
            double t = stationIndex / (double)(path.Points.Count - 1);
            if (_centerlineCurvedMprStationReversed) t = 1.0 - t;
            double y = t * Math.Max(0, hostHeight - 2);
            CenterlineCurvedMprStationIndicator.Margin = new Thickness(0, Math.Clamp(y, 0, Math.Max(0, hostHeight - 2)), 0, 0);
            return;
        }

        double tH = stationIndex / (double)(path.Points.Count - 1);
        if (_centerlineCurvedMprStationReversed) tH = 1.0 - tH;
        double x = tH * Math.Max(0, hostWidth - 2);
        CenterlineCurvedMprStationIndicator.Margin = new Thickness(Math.Clamp(x, 0, Math.Max(0, hostWidth - 2)), 0, 0, 0);
    }

    private void OnCenterlineCurvedMprPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _centerlineCurvedMprPinned = CenterlineCurvedMprPinButton.IsChecked == true;
        RefreshCenterlineCurvedMprPanel();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprRotationSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingCurvedMprRotationSlider)
        {
            return;
        }

        ApplyCenterlineCurvedMprRotation(e.NewValue, updateSlider: false);
    }

    private void OnCenterlineCurvedMprRotationResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyCenterlineCurvedMprRotation(0, updateSlider: true);

        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsCenterlinePanelsTiled)
        {
            return;
        }

        if (!CenterlineCurvedMprPanel.IsVisible || !e.GetCurrentPoint(CenterlineCurvedMprDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _centerlineCurvedMprDragPointer = e.Pointer;
        _centerlineCurvedMprDragPointer.Capture(CenterlineCurvedMprDragHandle);
        _centerlineCurvedMprDragStart = e.GetPosition(ViewerContentHost);
        _centerlineCurvedMprDragStartOffset = _centerlineCurvedMprOffset;
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCurvedMprDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _centerlineCurvedMprDragStart;
        _centerlineCurvedMprOffset = new Point(
            _centerlineCurvedMprDragStartOffset.X + delta.X,
            _centerlineCurvedMprDragStartOffset.Y + delta.Y);
        ApplyCenterlineCurvedMprPanelOffset();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCurvedMprDragPointer, e.Pointer))
        {
            return;
        }

        _centerlineCurvedMprDragPointer.Capture(null);
        _centerlineCurvedMprDragPointer = null;
        ApplyCenterlineCurvedMprPanelOffset();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateCenterlineStationFromCurvedMprPointer(e);
    }

    private void OnCenterlineCurvedMprImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(CenterlineCurvedMprImageHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateCenterlineStationFromCurvedMprPointer(e);
    }

    private void OnCenterlineCurvedMprImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        double delta = e.Delta.Y;
        if (Math.Abs(delta) <= double.Epsilon)
        {
            return;
        }

        double steps = Math.Sign(delta) * Math.Max(1, Math.Abs(delta));
        ApplyCenterlineCurvedMprRotation(
            _centerlineCurvedMprRotationDegrees + (steps * CenterlineCurvedMprRotationStepDegrees),
            updateSlider: true);
        e.Handled = true;
    }

    private void UpdateCenterlineStationFromCurvedMprPointer(PointerEventArgs e)
    {
        if (CenterlineCurvedMprImageHost is null ||
            !TryResolveCenterlineCrossSectionContext(out _, out CenterlinePath path, out _, out _) ||
            path.Points.Count <= 1)
        {
            return;
        }

        Point position = e.GetPosition(CenterlineCurvedMprImageHost);
        if (_centerlineCurvedMprDisplayOrientation == CurvedMprDisplayOrientation.Vertical)
        {
            double height = Math.Max(1, CenterlineCurvedMprImageHost.Bounds.Height);
            double t = Math.Clamp(position.Y / height, 0, 1);
            _centerlineCrossSectionStationNormalized = _centerlineCurvedMprStationReversed ? 1.0 - t : t;
        }
        else
        {
            double width = Math.Max(1, CenterlineCurvedMprImageHost.Bounds.Width);
            double t = Math.Clamp(position.X / width, 0, 1);
            _centerlineCrossSectionStationNormalized = _centerlineCurvedMprStationReversed ? 1.0 - t : t;
        }

        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void ApplyCenterlineCurvedMprDisplayOrientation(CurvedMprDisplayOrientation orientation)
    {
        if (CenterlineCurvedMprImage is null ||
            CenterlineCurvedMprStationIndicator is null ||
            CenterlineCurvedMprGuideCanvas is null ||
            CenterlineCurvedMprGuideLine is null)
        {
            return;
        }

        bool isVertical = orientation == CurvedMprDisplayOrientation.Vertical;
        double displayWidth;
        double displayHeight;
        if (IsCenterlinePanelsTiled)
        {
            displayWidth = isVertical ? 220 : 520;
            displayHeight = isVertical ? 420 : 180;
        }
        else
        {
            displayWidth = isVertical ? CenterlineCurvedMprVerticalDisplayWidth : CenterlineCurvedMprHorizontalDisplayWidth;
            displayHeight = isVertical ? CenterlineCurvedMprVerticalDisplayHeight : CenterlineCurvedMprHorizontalDisplayHeight;
        }

        CenterlineCurvedMprImage.Width = displayWidth;
        CenterlineCurvedMprImage.Height = displayHeight;
        CenterlineCurvedMprGuideCanvas.Width = displayWidth;
        CenterlineCurvedMprGuideCanvas.Height = displayHeight;

        if (isVertical)
        {
            CenterlineCurvedMprStationIndicator.Width = double.NaN;
            CenterlineCurvedMprStationIndicator.Height = 2;
            CenterlineCurvedMprStationIndicator.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            CenterlineCurvedMprStationIndicator.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            CenterlineCurvedMprGuideLine.StartPoint = new Point(displayWidth * 0.5, 0);
            CenterlineCurvedMprGuideLine.EndPoint = new Point(displayWidth * 0.5, displayHeight);
        }
        else
        {
            CenterlineCurvedMprStationIndicator.Width = 2;
            CenterlineCurvedMprStationIndicator.Height = double.NaN;
            CenterlineCurvedMprStationIndicator.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            CenterlineCurvedMprStationIndicator.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            CenterlineCurvedMprGuideLine.StartPoint = new Point(0, displayHeight * 0.5);
            CenterlineCurvedMprGuideLine.EndPoint = new Point(displayWidth, displayHeight * 0.5);
        }
    }

    private string BuildCenterlineCurvedMprStatusText(CenterlinePath path, int stationIndex)
    {
        return $"Station {stationIndex + 1}/{path.Points.Count} · {path.Points[stationIndex].ArcLengthMm:0.0} / {path.TotalLengthMm:0.0} mm · strip {CenterlineCurvedMprFieldOfViewMm:0} mm · slab {CenterlineCurvedMprSlabThicknessMm:0} mm MIP · rot {NormalizeCenterlineCurvedMprRotation(_centerlineCurvedMprRotationDegrees):0.#}°";
    }

    private void ApplyCenterlineCurvedMprRotation(double degrees, bool updateSlider)
    {
        double clamped = NormalizeCenterlineCurvedMprRotation(degrees);
        bool changed = Math.Abs(_centerlineCurvedMprRotationDegrees - clamped) > 1e-6;
        _centerlineCurvedMprRotationDegrees = clamped;

        if (CenterlineCurvedMprRotationValueText is not null)
        {
            CenterlineCurvedMprRotationValueText.Text = BuildCenterlineCurvedMprRotationLabel();
        }

        if (updateSlider && CenterlineCurvedMprRotationSlider is not null && Math.Abs(CenterlineCurvedMprRotationSlider.Value - clamped) > 1e-6)
        {
            CenterlineCurvedMprRotationSlider.Value = clamped;
        }

        if (!changed)
        {
            return;
        }

        _centerlineCurvedMprRenderedPathId = null;
        _centerlineCurvedMprRenderedRotationDegrees = double.NaN;
        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
    }

    private string BuildCenterlineCurvedMprRotationLabel() => $"{NormalizeCenterlineCurvedMprRotation(_centerlineCurvedMprRotationDegrees):0.#}°";

    private static double NormalizeCenterlineCurvedMprRotation(double degrees)
    {
        double normalized = degrees % 360.0;
        if (normalized <= -180)
        {
            normalized += 360;
        }
        else if (normalized > 180)
        {
            normalized -= 360;
        }

        return Math.Round(normalized / CenterlineCurvedMprRotationStepDegrees) * CenterlineCurvedMprRotationStepDegrees;
    }

    private double GetCenterlineCurvedMprRotationRadians() => NormalizeCenterlineCurvedMprRotation(_centerlineCurvedMprRotationDegrees) * (Math.PI / 180.0);

    private void ApplyCenterlineCurvedMprPanelOffset()
    {
        if (CenterlineCurvedMprPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureCenterlineCurvedMprPanelTransform();
        if (IsCenterlinePanelsTiled)
        {
            transform.X = 0;
            transform.Y = 0;
            ApplyCenterlinePanelLayoutMode();
            return;
        }

        double panelWidth = CenterlineCurvedMprPanel.Bounds.Width;
        double panelHeight = CenterlineCurvedMprPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = CenterlineCurvedMprPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _centerlineCurvedMprOffset.X;
            transform.Y = _centerlineCurvedMprOffset.Y;
            return;
        }

        double defaultLeft = margin.Left;
        double availableRight = Math.Max(0, hostWidth - panelWidth - defaultLeft);
        double availableBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_centerlineCurvedMprOffset.X, -defaultLeft - overflowX, availableRight + overflowX);
        double clampedY = Math.Clamp(_centerlineCurvedMprOffset.Y, -margin.Top - overflowY, availableBottom + overflowY);
        _centerlineCurvedMprOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureCenterlineCurvedMprPanelTransform()
    {
        if (CenterlineCurvedMprPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        CenterlineCurvedMprPanel.RenderTransform = transform;
        return transform;
    }
}