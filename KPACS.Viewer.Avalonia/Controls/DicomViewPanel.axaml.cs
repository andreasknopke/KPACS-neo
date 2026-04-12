// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Controls/DicomViewPanel.axaml.cs
// Ported from dview.pas (TdView) — the core DICOM viewer control.
// Avalonia cross-platform version.
//
// This is a basic port of the K-PACS dView component with:
//   - Zoom (mouse wheel, fit-to-window, 1:1, programmatic)
//   - Window/Level (right-click drag, ported from the original TdView contrast tool)
//   - Pan (left-click drag in center zone)
//   - Integrated edge-zoom (left-click drag in outer 1/6th zone)
//   - Color LUT support
//   - 4-corner DICOM overlay text
// ------------------------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

/// <summary>
/// Basic DICOM viewer control with zoom, window/level, and pan.
/// Ported from TdView in dview.pas — Avalonia cross-platform version.
/// </summary>
public partial class DicomViewPanel : UserControl
{
    private static readonly DicomTag SharedFunctionalGroupsSequenceTag = new(0x5200, 0x9229);
    private static readonly DicomTag PerFrameFunctionalGroupsSequenceTag = new(0x5200, 0x9230);
    private static readonly DicomTag FrameVoILutSequenceTag = new(0x0028, 0x9132);

    private static Cursor? s_windowCursor;
    private static readonly object s_windowCursorLock = new();

    public sealed record DisplayState(
        double WindowCenter,
        double WindowWidth,
        double ZoomFactor,
        bool FitToWindow,
        double PanX,
        double PanY,
        int ColorScheme,
        SliceOrientation Orientation,
        VolumeProjectionMode ProjectionMode,
        double ProjectionThicknessMm,
        double PlaneTiltAroundColumnRadians,
        double PlaneTiltAroundRowRadians,
        int ViewRotationQuarterTurns,
        bool ViewFlipHorizontal,
        bool ViewFlipVertical);

    public sealed record NavigationState(
        double ZoomFactor,
        double RelativeZoomFactor,
        bool FitToWindow,
        Point CenterImagePoint);

    // ==============================================================================================
    // Image data (ported from TdView fields + DICOMdata record)
    // ==============================================================================================

    private byte[]? _rawPixelData;
    private int _imageWidth;
    private int _imageHeight;
    private int _bitsAllocated = 8;
    private int _bitsStored = 8;
    private bool _isSigned;
    private int _samplesPerPixel = 1;
    private int _planarConfiguration;
    private double _rescaleSlope = 1.0;
    private double _rescaleIntercept;
    private bool _isMonochrome1;
    private string _photometricInterpretation = "MONOCHROME2";
    private readonly List<BitmapOverlayPlane> _bitmapOverlays = [];

    // DICOM metadata for overlay
    private string _patientName = "";
    private string _patientId = "";
    private string _studyDate = "";
    private string _studyDescription = "";
    private string _institution = "";
    private string _modality = "";
    private string _fileName = "";
    private int _frameCount = 1;

    // ==============================================================================================
    // Display state (ported from TdView: FWindowCenter, FWindowWidth, FImageZoomPct, etc.)
    // ==============================================================================================

    private double _windowCenter;
    private double _windowWidth;
    private double _defaultWindowCenter;
    private double _defaultWindowWidth;
    private double _zoomFactor = 1.0;
    private bool _fitToWindow = true;
    private int _colorScheme = 1;
    private double _displayScaleX = 1.0;
    private double _displayScaleY = 1.0;
    private bool _pendingInitialFitToWindow = true;
    private int _viewRotationQuarterTurns;
    private bool _viewFlipHorizontal;
    private bool _viewFlipVertical;

    // ==============================================================================================
    // Volume rendering state
    // ==============================================================================================

    private SeriesVolume? _volume;
    private IRenderBackend? _renderBackend;
    private SliceOrientation _volumeOrientation = SliceOrientation.Axial;
    private int _volumeSliceIndex;
    private short[]? _volumeSlicePixels;
    private byte[]? _volumeSliceBgraPixels;
    private VolumeProjectionMode _projectionMode = VolumeProjectionMode.Mpr;
    private double _projectionThicknessMm = 1.0;
    private double _planeTiltAroundColumn;
    private double _planeTiltAroundRow;
    private double _planeOffsetMm;
    private bool _isProjectionThicknessDragging;
    private double _projectionDragStartThicknessMm;
    private double _projectionDragStartY;
    private IPointer? _projectionPointer;
    private string _lastRenderBackendLabel = "CPU";
    private PlaneTiltConstraintAxis _planeTiltConstraintAxis;
    private bool _planeTiltConstraintModeActive;
    private bool _planeTiltControlPressed;

    // ── Segmentation mask overlay state ──────────────────────────
    private List<SegmentationMaskOverlay> _segmentationMaskOverlays = [];

    /// <summary>
    /// Describes a single segmentation mask to overlay on the viewport.
    /// </summary>
    public sealed record SegmentationMaskOverlay(
        SegmentationMask3D Mask,
        Services.SegmentationMaskBuffer Buffer,
        byte OverlayR, byte OverlayG, byte OverlayB, byte OverlayA);

    /// <summary>
    /// Sets the list of segmentation mask overlays to composite on each rendered slice.
    /// Pass an empty list to hide all overlays.
    /// </summary>
    public void SetSegmentationMaskOverlays(IReadOnlyList<SegmentationMaskOverlay> overlays)
    {
        _segmentationMaskOverlays = overlays.ToList();
        RenderImage(sharp: _isSharpRender);
        InvalidateVisual();
    }

    /// <summary>True when this panel is displaying a slice from a bound volume.</summary>
    public bool IsVolumeBound => _volume is not null;

    /// <summary>The currently bound volume, if any.</summary>
    public SeriesVolume? BoundVolume => _volume;

    /// <summary>The current slice orientation when volume-bound.</summary>
    public SliceOrientation VolumeOrientation => _volumeOrientation;

    /// <summary>The current slice index when volume-bound.</summary>
    public int VolumeSliceIndex => _volumeSliceIndex;
    public int VolumeSliceCount => GetCurrentSliceCount();
    public VolumeProjectionMode ProjectionMode => _projectionMode;
    public double ProjectionThicknessMm => _projectionThicknessMm;
    public string ProjectionModeLabel => GetProjectionModeLabel(_projectionMode);
    public string OrientationLabel => HasTiltedPlane ? $"{GetOrientationLabel(_volumeOrientation)} oblique" : GetOrientationLabel(_volumeOrientation);
    public bool HasTiltedPlane => Math.Abs(_planeTiltAroundColumn) > 1e-4 || Math.Abs(_planeTiltAroundRow) > 1e-4;
    public bool IsHorizontallyFlipped => _viewFlipHorizontal;
    public bool IsVerticallyFlipped => _viewFlipVertical;
    public int ViewRotationQuarterTurns => NormalizeQuarterTurns(_viewRotationQuarterTurns);
    public string LastRenderBackendLabel => _lastRenderBackendLabel;

    /// <summary>The active render backend, if any. Null when no volume is bound.</summary>
    public IRenderBackend? RenderBackend => _renderBackend;

    /// <summary>True when this panel is rendering via a remote server.</summary>
    public bool IsRemoteRendering => _renderBackend?.IsRemote == true;

    // ==============================================================================================
    // Rendering
    // ==============================================================================================

    private WriteableBitmap? _displayBitmap;
    private byte[] _lutR = new byte[256];
    private byte[] _lutG = new byte[256];
    private byte[] _lutB = new byte[256];
    private bool _dvrAutoColorLutEnabled;
    private byte[]? _renderBuffer;
    private byte[]? _viewTransformBuffer;

    // Progressive rendering: render fast at native resolution during interaction,
    // then re-render at display resolution after a short idle delay.
    private DispatcherTimer? _sharpRenderTimer;
    private bool _isSharpRender = true;  // true = render at display resolution, false = fast native-res

    // Transforms (created in code — Avalonia doesn't support x:Name on transforms)
    private readonly ScaleTransform _zoomTransform = new ScaleTransform(1, 1);
    private readonly TranslateTransform _panTransform = new TranslateTransform();

    // Shadow fields for pan position — avoids reading back from the Avalonia
    // transform object, which can introduce tiny precision drift that accumulates
    // during rapid incremental operations like edge-zoom.
    private double _panX;
    private double _panY;

    // ==============================================================================================
    // Mouse interaction state (ported from TdView: gXStart, gYStart, gFastSlope, gFastCen, etc.)
    // ==============================================================================================

    private Point _mouseDownPos;
    private Point _lastPointerPos;
    private bool _isRightDragging;  // window/level
    private bool _isLeftDragging;   // pan or edge-zoom
    private double _startWindowCenter, _startWindowWidth;
    private double _startPanX, _startPanY;
    private bool _isStackDragging;
    private int _lastStackScreenY;   // screen-space Y for stack drag (like Delphi Mouse.CursorPos)
    private DispatcherTimer? _stackDragTimer;

    // Integrated zoom/pan (ported from TdView: IsCursorInZoomRegion, FZoomRegion)
    // Edge zone = outer 1/6th of each dimension → drag to zoom
    // Center zone = inner area → drag to pan
    private bool _isEdgeZoom;        // locked at mouse-down: true = zoom mode, false = pan mode
    private int _lastMouseY;         // for incremental edge-zoom tracking
    private Point? _cursor3DImagePoint;
    private Point? _referenceLineStartImagePoint;
    private Point? _referenceLineEndImagePoint;
    private ActionToolbarMode _actionMode = ActionToolbarMode.ZoomPan;
    private NavigationTool _navigationTool;
    private bool _isPlaneTiltDragging;
    private Point _planeTiltDragStart;
    private double _planeTiltStartAroundColumn;
    private double _planeTiltStartAroundRow;
    private bool _preserveTiltOffsetDuringNextShow;

    // Pointer capture tracking
    private IPointer? _capturedPointer;
    private TopLevel? _capturedTopLevel;

    /// <summary>Last error message from LoadFile, if any.</summary>
    public string? LastError { get; private set; }

    // ==============================================================================================
    // Public properties
    // ==============================================================================================

    public double WindowCenter
    {
        get => _windowCenter;
        set { _windowCenter = value; ApplyActiveColorLut(); RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); NotifyViewStateChanged(); }
    }

    public double WindowWidth
    {
        get => _windowWidth;
        set { _windowWidth = Math.Max(1, value); ApplyActiveColorLut(); RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); NotifyViewStateChanged(); }
    }

    public double ZoomFactor => _zoomFactor;
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;
    public string Modality => _modality;
    public string PatientName => _patientName;
    public bool IsImageLoaded => _rawPixelData != null || _volumeSlicePixels != null;
    public int CurrentColorScheme => _colorScheme;
    public bool IsDvrAutoColorLutEnabled => _dvrAutoColorLutEnabled;
    public bool SupportsDvrAutoColorLut => _volume is not null && string.Equals(_modality, "CT", StringComparison.OrdinalIgnoreCase);
    public string FilePath => _fileName;
    public DicomSpatialMetadata? SpatialMetadata { get; private set; }

    private bool _showOverlay = true;
    private bool _showToolboxButton = true;
    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            _showOverlay = value;
            ApplyOverlayVisibility();
        }
    }

    public bool ShowToolboxButton
    {
        get => _showToolboxButton;
        set
        {
            _showToolboxButton = value;
            if (ToolboxButton is not null)
            {
                ToolboxButton.IsVisible = value && IsImageLoaded;
            }
        }
    }

    public void SetOverlayStudyInfo(
        string patientName,
        string patientId,
        string studyDate,
        string studyDescription,
        string institution,
        string modality)
    {
        _patientName = patientName ?? string.Empty;
        _patientId = patientId ?? string.Empty;
        _studyDate = studyDate ?? string.Empty;
        _studyDescription = studyDescription ?? string.Empty;
        _institution = institution ?? string.Empty;
        _modality = modality ?? string.Empty;
        _cachedIsMrLike = null;
        _cachedIsCtLike = null;
        UpdateOverlay();
    }

    // ==============================================================================================
    // Events
    // ==============================================================================================

    public event Action? WindowChanged;
    public event Action? ZoomChanged;
    public event Action? ImageLoaded;
    public event Action<int>? StackScrollRequested;
    public event Action? ViewStateChanged;
    public event Action<DicomHoverInfo?>? HoveredImagePointChanged;
    public event Action<DicomImagePointerInfo>? ImagePointPressed;
    public event Action? ImageDoubleClicked;
    public event Action? ToolboxRequested;

    public Control ToolboxPlacementTarget => ToolboxButton;

    public MouseWheelMode WheelMode { get; set; } = MouseWheelMode.Zoom;
    public ActionToolbarMode ActionMode
    {
        get => _actionMode;
        set
        {
            _actionMode = value;
            UpdateInteractiveCursor(_lastPointerPos);
        }
    }
    public NavigationTool NavigationTool
    {
        get => _navigationTool;
        set
        {
            _navigationTool = value;
            UpdateInteractiveCursor(_lastPointerPos);
        }
    }
    public int StackItemCount { get; set; } = 1;
    public bool StackSkipImages { get; set; } = true;

    // ==============================================================================================
    // Constructor
    // ==============================================================================================

    public DicomViewPanel()
    {
        InitializeComponent();
        SetColorLutInternal(1);

        // Set up image render transforms in code
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_zoomTransform);
        transformGroup.Children.Add(_panTransform);
        DicomImage.RenderTransform = transformGroup;

        // Wire up pointer events (Avalonia uses Pointer* instead of Mouse*)
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerCaptureLost += OnRootGridPointerCaptureLost;
        RootGrid.PointerWheelChanged += OnPointerWheelChanged;
        RootGrid.PointerExited += OnPointerExited;
        KeyDown += OnPanelKeyDown;
        KeyUp += OnPanelKeyUp;
        OrientationBadge.PointerPressed += OnOrientationBadgePointerPressed;
        ProjectionBadge.PointerPressed += OnProjectionBadgePointerPressed;
        ProjectionBadge.PointerMoved += OnProjectionBadgePointerMoved;
        ProjectionBadge.PointerReleased += OnProjectionBadgePointerReleased;
        ProjectionBadge.PointerCaptureLost += OnProjectionBadgePointerCaptureLost;
        CameraViewBadge.PointerPressed += OnDvrCameraViewBadgePointerPressed;

        SizeChanged += OnSizeChanged;
        InitializeOrientationContextMenu();
        InitializeProjectionContextMenu();
        InitializeDvrCameraViewContextMenu();
        UpdateInteractiveCursor();
    }

    private void InitializeOrientationContextMenu()
    {
        OrientationBadge.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                CreateOrientationMenuItem("Axial", SliceOrientation.Axial),
                CreateOrientationMenuItem("Coronal", SliceOrientation.Coronal),
                CreateOrientationMenuItem("Sagittal", SliceOrientation.Sagittal),
            }
        };
    }

    private MenuItem CreateOrientationMenuItem(string header, SliceOrientation orientation)
    {
        var item = new MenuItem { Header = header, Tag = orientation };
        item.Click += OnOrientationMenuItemClick;
        return item;
    }

    private void OnOrientationMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SliceOrientation orientation })
        {
            SetVolumeOrientation(orientation);
        }
    }

    private void InitializeProjectionContextMenu()
    {
        ProjectionBadge.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                CreateProjectionMenuItem("MPR", VolumeProjectionMode.Mpr),
                CreateProjectionMenuItem("MipPR", VolumeProjectionMode.MipPr),
                CreateProjectionMenuItem("MinPR", VolumeProjectionMode.MinPr),
                CreateProjectionMenuItem("MPVRT", VolumeProjectionMode.MpVrt),
                CreateProjectionMenuItem("DVR", VolumeProjectionMode.Dvr),
            }
        };
    }

    private MenuItem CreateProjectionMenuItem(string header, VolumeProjectionMode mode)
    {
        var item = new MenuItem { Header = header, Tag = mode };
        item.Click += OnProjectionMenuItemClick;
        return item;
    }

    private void OnProjectionMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: VolumeProjectionMode mode })
        {
            SetProjectionMode(mode);
        }
    }

    private void InitializeDvrCameraViewContextMenu()
    {
        CameraViewBadge.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                CreateDvrCameraViewMenuItem("Back View", DvrCameraViewPreset.Front),
                CreateDvrCameraViewMenuItem("Front View", DvrCameraViewPreset.Back),
                CreateDvrCameraViewMenuItem("Left Side View", DvrCameraViewPreset.Left),
                CreateDvrCameraViewMenuItem("Right Side View", DvrCameraViewPreset.Right),
                CreateDvrCameraViewMenuItem("View From Above", DvrCameraViewPreset.Top),
                CreateDvrCameraViewMenuItem("View From Below", DvrCameraViewPreset.Bottom),
            }
        };
    }

    private MenuItem CreateDvrCameraViewMenuItem(string header, DvrCameraViewPreset preset)
    {
        var item = new MenuItem { Header = header, Tag = preset };
        item.Click += OnDvrCameraViewMenuItemClick;
        return item;
    }

    private void OnDvrCameraViewMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: DvrCameraViewPreset preset })
        {
            SetDvrCameraViewPreset(preset);
        }
    }

    private void OnDvrCameraViewBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null || !IsDvrMode)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(CameraViewBadge);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        CameraViewBadge.ContextMenu?.Open(CameraViewBadge);
        e.Handled = true;
    }

    private void UpdateInteractiveCursor(Point? pos = null)
    {
        if (!IsImageLoaded)
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            return;
        }

        if (_measurementTool != MeasurementTool.None)
        {
            Cursor = _measurementTool == MeasurementTool.Modify
                ? new Cursor(StandardCursorType.DragMove)
                : new Cursor(StandardCursorType.Cross);
            return;
        }

        if (IsPlaneTiltToolActive())
        {
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return;
        }

        if (IsWindowActionMode())
        {
            Cursor = CreateWindowCursor();
            return;
        }

        if (IsScrollActionMode())
        {
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            return;
        }

        Point pointerPosition = pos ?? _mouseDownPos;
        Cursor = IsCursorInZoomRegion(pointerPosition)
            ? new Cursor(StandardCursorType.SizeNorthSouth)
            : new Cursor(StandardCursorType.Hand);
    }

    // ==============================================================================================
    // File Loading (ported from TdView.LoadData)
    // ==============================================================================================

    /// <summary>
    /// Loads a DICOM file for display. Extracts pixel data, metadata, and window presets.
    /// </summary>
    public bool LoadFile(string filePath)
    {
        LastError = null;

        _renderBackend?.Dispose();
        _renderBackend = null;
        _volume = null;
        _volumeSlicePixels = null;
        _volumeSliceBgraPixels = null;
        _volumeSliceIndex = 0;
        _projectionPointer = null;
        _isProjectionThicknessDragging = false;

        try
        {
            var file = DicomFile.Open(filePath, FellowOakDicom.FileReadOption.ReadAll);
            var dataset = file.Dataset;
            _fileName = filePath;
            SpatialMetadata = DicomSpatialMetadata.FromDataset(dataset, filePath);
            UpdateDisplayGeometry(SpatialMetadata?.ColumnSpacing ?? 1.0, SpatialMetadata?.RowSpacing ?? 1.0);

            // --- Extract image metadata ---
            _imageWidth = dataset.GetSingleValue<int>(DicomTag.Columns);
            _imageHeight = dataset.GetSingleValue<int>(DicomTag.Rows);
            _bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 8);
            _bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, _bitsAllocated);
            _samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, 1);
            _planarConfiguration = dataset.GetSingleValueOrDefault(DicomTag.PlanarConfiguration, 0);
            _isSigned = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0) == 1;
            _rescaleSlope = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleSlope, 1.0);
            _rescaleIntercept = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleIntercept, 0.0);

            if (_rescaleSlope == 0) _rescaleSlope = 1.0;

            _photometricInterpretation = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2").Trim().ToUpperInvariant();
            _isMonochrome1 = _photometricInterpretation.Contains("MONOCHROME1");

            // --- Patient / study metadata for overlay ---
            _patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "");
            _patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "");
            _studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "");
            _studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
            _institution = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
            _modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "");
            _cachedIsMrLike = null;
            _cachedIsCtLike = null;

            // --- Extract pixel data ---
            if (!dataset.Contains(DicomTag.PixelData))
            {
                LastError = "This DICOM file contains no pixel data.";
                return false;
            }

            // Transcode compressed transfer syntaxes (JPEG, JPEG2000, RLE, etc.)
            var syntax = file.Dataset.InternalTransferSyntax;
            if (syntax.IsEncapsulated)
            {
                file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
                dataset = file.Dataset;
            }

            var pixelData = DicomPixelData.Create(dataset);
            _frameCount = (int)pixelData.NumberOfFrames;
            var frame = pixelData.GetFrame(0);
            _rawPixelData = frame.Data;

            // Sanity check: verify decompressed buffer matches expected size
            int expectedBytes = ComputeExpectedPixelBytes(_imageWidth, _imageHeight, _samplesPerPixel, _bitsAllocated, _photometricInterpretation);
            if (_rawPixelData.Length < expectedBytes)
            {
                LastError = $"Pixel data size mismatch: got {_rawPixelData.Length} bytes, " +
                    $"expected {expectedBytes} ({_imageWidth}×{_imageHeight}, " +
                    $"{_bitsAllocated}-bit, {_samplesPerPixel} spp).";
                return false;
            }

            LoadBitmapOverlays(dataset);

            // --- Window Center / Width ---
            if (!TryGetDefaultWindowPreset(dataset, out double wc, out double ww))
            {
                (wc, ww) = DicomPixelRenderer.ComputeAutoWindow(
                    _rawPixelData, _imageWidth, _imageHeight,
                    _bitsAllocated, _bitsStored, _isSigned,
                    _samplesPerPixel, _rescaleSlope, _rescaleIntercept);
            }

            _windowCenter = wc;
            _windowWidth = Math.Max(1, ww);
            _defaultWindowCenter = _windowCenter;
            _defaultWindowWidth = _windowWidth;
            _volumeSliceBgraPixels = null;

            // --- Color LUT ---
            if (_isMonochrome1)
                SetColorLutInternal(1);
            else
                SetColorLutInternal(_colorScheme);

            _displayBitmap = null;
            DicomImage.Source = null;
            ApplyDisplayImageSize();
            DicomImage.InvalidateMeasure();

            // Hide placeholder
            PlaceholderText.IsVisible = false;

            // Render the image
            RenderImage();

            // Fit to window and center
            _fitToWindow = true;
            _pendingInitialFitToWindow = true;
            ApplyInitialFitToWindow();
            Set3DCursorOverlay(null);
            SetReferenceLineOverlay(null, null);
            ResetMeasurementStateForNewImage();

            UpdateOverlay();
            ImageLoaded?.Invoke();
            WindowChanged?.Invoke();
            ZoomChanged?.Invoke();

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Error loading DICOM file: {ex.Message}";
            SpatialMetadata = null;
            return false;
        }
    }

    public void LoadRemoteRenderedImage(byte[] bgraPixels, int width, int height, string sourceId = "")
    {
        LastError = null;

        _renderBackend?.Dispose();
        _renderBackend = null;
        _volume = null;
        _rawPixelData = null;
        _volumeSlicePixels = null;
        _projectionPointer = null;
        _isProjectionThicknessDragging = false;
        _planeTiltAroundColumn = 0;
        _planeTiltAroundRow = 0;
        _planeOffsetMm = 0;
        _bitmapOverlays.Clear();

        _imageWidth = Math.Max(1, width);
        _imageHeight = Math.Max(1, height);
        _bitsAllocated = 8;
        _bitsStored = 8;
        _samplesPerPixel = 1;
        _planarConfiguration = 0;
        _isSigned = false;
        _rescaleSlope = 1.0;
        _rescaleIntercept = 0.0;
        _windowCenter = 127;
        _windowWidth = 255;
        _defaultWindowCenter = _windowCenter;
        _defaultWindowWidth = _windowWidth;
        _lastRenderBackendLabel = "Remote image";
        _isMonochrome1 = false;
        _photometricInterpretation = "RGB";
        _frameCount = 1;
        _fileName = sourceId ?? string.Empty;
        SpatialMetadata = null;
        _volumeSliceBgraPixels = [.. bgraPixels];

        SetColorLutInternal(_colorScheme);

        _displayBitmap = null;
        DicomImage.Source = null;
        ApplyDisplayImageSize();
        DicomImage.InvalidateMeasure();

        PlaceholderText.IsVisible = false;

        RenderImage();

        _fitToWindow = true;
        _pendingInitialFitToWindow = true;
        ApplyInitialFitToWindow();
        Set3DCursorOverlay(null);
        SetReferenceLineOverlay(null, null);
        ResetMeasurementStateForNewImage();

        UpdateOverlay();
        ImageLoaded?.Invoke();
        WindowChanged?.Invoke();
        ZoomChanged?.Invoke();
    }

    private static bool TryGetDefaultWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        if (TryReadWindowPreset(dataset, out center, out width))
        {
            return true;
        }

        if (TryReadWindowPresetFromFunctionalGroups(dataset, SharedFunctionalGroupsSequenceTag, out center, out width))
        {
            return true;
        }

        if (TryReadWindowPresetFromFunctionalGroups(dataset, PerFrameFunctionalGroupsSequenceTag, out center, out width))
        {
            return true;
        }

        center = 0;
        width = 0;
        return false;
    }

    private static bool TryReadWindowPresetFromFunctionalGroups(DicomDataset dataset, DicomTag groupSequenceTag, out double center, out double width)
    {
        center = 0;
        width = 0;

        if (!dataset.Contains(groupSequenceTag))
        {
            return false;
        }

        DicomSequence sequence = dataset.GetSequence(groupSequenceTag);
        foreach (DicomDataset item in sequence.Items)
        {
            if (!item.Contains(FrameVoILutSequenceTag))
            {
                continue;
            }

            DicomSequence voiSequence = item.GetSequence(FrameVoILutSequenceTag);
            foreach (DicomDataset voiItem in voiSequence.Items)
            {
                if (TryReadWindowPreset(voiItem, out center, out width))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        center = 0;
        width = 0;

        if (!TryReadFirstNumericValue(dataset, DicomTag.WindowCenter, out center) ||
            !TryReadFirstNumericValue(dataset, DicomTag.WindowWidth, out width))
        {
            return false;
        }

        return width > 0;
    }

    private static bool TryReadFirstNumericValue(DicomDataset dataset, DicomTag tag, out double value)
    {
        value = 0;

        if (!dataset.Contains(tag))
        {
            return false;
        }

        string? rawValue = dataset.GetString(tag);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        foreach (string part in rawValue.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return false;
    }

    public void ClearImage()
    {
        _rawPixelData = null;
        _renderBackend?.Dispose();
        _renderBackend = null;
        _volume = null;
        _volumeSlicePixels = null;
        _volumeSliceBgraPixels = null;
        _planeTiltAroundColumn = 0;
        _planeTiltAroundRow = 0;
        _planeOffsetMm = 0;
        _isProjectionThicknessDragging = false;
        _projectionPointer = null;
        _bitmapOverlays.Clear();
        _photometricInterpretation = "MONOCHROME2";
        _planarConfiguration = 0;
        _displayBitmap = null;
        _imageWidth = 0;
        _imageHeight = 0;
        _displayScaleX = 1.0;
        _displayScaleY = 1.0;
        _frameCount = 1;
        _fileName = string.Empty;
        SpatialMetadata = null;
        DicomImage.Source = null;
        DicomImage.Width = double.NaN;
        DicomImage.Height = double.NaN;
        PlaceholderText.IsVisible = true;
        Set3DCursorOverlay(null);
        SetReferenceLineOverlay(null, null);
        PixelLensPanel.IsVisible = false;
        MeasurementOverlay.Children.Clear();
        ResetMeasurementStateForNewImage();
        UpdateOverlay();
        UpdateSecondaryCaptureButton();
    }

    // ==============================================================================================
    // Volume binding — loads a slice from a pre-built SeriesVolume
    // ==============================================================================================

    /// <summary>
    /// Binds this panel to a <see cref="SeriesVolume"/> and displays the given slice.
    /// Replaces any previously loaded file or volume.
    /// </summary>
    public void BindVolume(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        BindVolumeWithBackend(volume, new LocalRenderBackend(volume), orientation, sliceIndex);
    }

    /// <summary>
    /// Binds this panel to a volume using a specific render backend.
    /// For local rendering, use <see cref="LocalRenderBackend"/>.
    /// For remote rendering, use <see cref="RemoteRenderBackend"/>.
    /// </summary>
    public void BindVolumeWithBackend(SeriesVolume volume, IRenderBackend renderBackend, SliceOrientation orientation, int sliceIndex)
    {
        _renderBackend?.Dispose();
        _rawPixelData = null; // detach from legacy file path
        _volume = volume;
        _renderBackend = renderBackend;
        _volumeOrientation = orientation;
        _planeTiltAroundColumn = 0;
        _planeTiltAroundRow = 0;
        _planeOffsetMm = 0;
        _pendingInitialFitToWindow = true;
        _projectionThicknessMm = Math.Max(GetMinimumProjectionThicknessMm(), _renderBackend.GetSliceSpacing(orientation));
        _isMonochrome1 = volume.IsMonochrome1;
        _samplesPerPixel = 1;
        _bitsAllocated = 16;
        _bitsStored = 16;
        _planarConfiguration = 0;
        _isSigned = true;
        _rescaleSlope = 1.0;
        _rescaleIntercept = 0.0;
        _photometricInterpretation = volume.IsMonochrome1 ? "MONOCHROME1" : "MONOCHROME2";
        _bitmapOverlays.Clear();

        // Window defaults from volume
        _windowCenter = volume.DefaultWindowCenter;
        _windowWidth = Math.Max(1, volume.DefaultWindowWidth);
        _defaultWindowCenter = _windowCenter;
        _defaultWindowWidth = _windowWidth;
        _volumeSliceBgraPixels = null;

        if (_isMonochrome1)
            SetColorLutInternal(1);
        else
            SetColorLutInternal(_colorScheme);

        ShowVolumeSlice(sliceIndex);
    }

    /// <summary>
    /// Pushes the panel's current windowing, colour scheme, and view transform
    /// to the render backend. For local backends these are no-ops; for remote
    /// backends they ensure the server applies the correct parameters.
    /// Called automatically before every render request.
    /// </summary>
    private void SyncViewportStateToBackend()
    {
        if (_renderBackend is null)
            return;

        _renderBackend.SetWindowing(_windowCenter, _windowWidth);
        _renderBackend.SetColorScheme(_colorScheme);
        _renderBackend.SetViewTransform(
            _zoomFactor, _panX, _panY,
            _viewFlipHorizontal, _viewFlipVertical, _viewRotationQuarterTurns);
        _renderBackend.SetOutputSize(
            GetSourceRenderPixelWidth(),
            GetSourceRenderPixelHeight());
    }

    /// <summary>
    /// Navigates to a different slice within the currently bound volume.
    /// Has no effect if no volume is bound.
    /// </summary>
    public bool ShowVolumeSlice(int sliceIndex)
    {
        if (_volume is null || _renderBackend is null)
            return false;

        // Sync viewport state to the backend before rendering.
        // For local backends these are no-ops; for remote backends they ensure
        // the server applies the correct windowing, colour scheme, and transform.
        SyncViewportStateToBackend();

        double previousDisplayWidth = GetDisplayWidth();
        double previousDisplayHeight = GetDisplayHeight();
        int previousImageWidth = _imageWidth;
        int previousImageHeight = _imageHeight;

        int maxSlice = GetCurrentSliceCount() - 1;
        sliceIndex = Math.Clamp(sliceIndex, 0, maxSlice);
        VolumeSlicePlane? requestedPlane = _preserveTiltOffsetDuringNextShow
            ? GetCurrentSlicePlane()
            : GetCurrentSlicePlaneForSliceIndex(sliceIndex);

        if (requestedPlane is not null)
        {
            _planeOffsetMm = requestedPlane.CurrentOffsetMm;
            _volumeSliceIndex = requestedPlane.GetSliceIndexForOffset(_planeOffsetMm);
        }
        else
        {
            _volumeSliceIndex = sliceIndex;
        }

        // DVR with active 3D camera: use the arbitrary-view renderer.
        // When the backend supports high-quality interactive, render at full quality.
        ReslicedImage resliced;
        if (IsDvrMode && _dvrRenderState is not null)
        {
            UpdateDvrRenderState(highQuality: CanUseGpuForCurrentDvr());
            resliced = _renderBackend.ComputeDirectVolumeRenderingView(
                _dvrRenderState, _dvrTransferFunction);
        }
        else
        {
            resliced = requestedPlane is not null
                ? _renderBackend.RenderSlab(
                    requestedPlane,
                    _projectionThicknessMm,
                    _projectionMode)
                : _renderBackend.RenderSlab(
                    _volumeOrientation,
                    sliceIndex,
                    _projectionThicknessMm,
                    _projectionMode);
        }

                _preserveTiltOffsetDuringNextShow = false;
        _lastRenderBackendLabel = string.IsNullOrWhiteSpace(resliced.RenderBackendLabel) ? "CPU" : resliced.RenderBackendLabel;

        _volumeSlicePixels = resliced.Pixels;
        _volumeSliceBgraPixels = resliced.BgraPixels;
        _imageWidth = resliced.Width;
        _imageHeight = resliced.Height;
        _frameCount = GetCurrentSliceCount();
        StackItemCount = _frameCount;
        UpdateDisplayGeometry(resliced.PixelSpacingX, resliced.PixelSpacingY);

        SpatialMetadata = resliced.SpatialMetadata;
        if (SpatialMetadata is null && IsDvrMode)
        {
            SpatialMetadata = requestedPlane is not null
                ? requestedPlane.CreateSpatialMetadata(_volume)
                : _renderBackend.GetSliceSpatialMetadata(_volumeOrientation, _volumeSliceIndex);
        }
        _fileName = SpatialMetadata?.FilePath ?? "";
        ApplyDisplayImageSize();

        bool geometryChanged = previousImageWidth != _imageWidth
            || previousImageHeight != _imageHeight
            || Math.Abs(previousDisplayWidth - GetDisplayWidth()) > 0.01
            || Math.Abs(previousDisplayHeight - GetDisplayHeight()) > 0.01;

        if (_fitToWindow && !_pendingInitialFitToWindow && geometryChanged)
        {
            ApplyFitToWindowLayoutWithoutRender();
        }

        PlaceholderText.IsVisible = false;
        if (IsDvrMode)
        {
            bool highQuality = _renderBackend.SupportsHighQualityInteractive;
            RenderImage(sharp: highQuality);
            if (!highQuality)
            {
                ScheduleDvrSharpRender();
            }
        }
        else
        {
            RenderImageFastThenSharp();
        }

        if (_fitToWindow && _pendingInitialFitToWindow)
        {
            ApplyInitialFitToWindow();
        }

        Set3DCursorOverlay(null);
        SetReferenceLineOverlay(null, null);
        ResetMeasurementStateForNewImage();
        UpdateOverlay();
        ImageLoaded?.Invoke();
        WindowChanged?.Invoke();
        ZoomChanged?.Invoke();

        return true;
    }

    /// <summary>
    /// Changes the slice orientation of the currently bound volume and resets
    /// the slice index to the middle of the new axis.
    /// </summary>
    public bool SetVolumeOrientation(SliceOrientation orientation)
    {
        if (_volume is null || _volumeOrientation == orientation)
            return false;

        _volumeOrientation = orientation;
        _planeTiltAroundColumn = 0;
        _planeTiltAroundRow = 0;
        _planeOffsetMm = 0;
        _projectionThicknessMm = Math.Clamp(
            _projectionThicknessMm,
            GetMinimumProjectionThicknessMm(),
            GetMaximumProjectionThicknessMm());
        int midSlice = GetCurrentSliceCount() / 2;
        if (IsDvrMode)
        {
            InitializeDvrCamera(resetTransferWindow: false);
        }

        bool changed = ShowVolumeSlice(midSlice);
        if (changed)
        {
            UpdateOverlay();
            NotifyViewStateChanged();
        }

        return changed;
    }

    public bool SetProjectionMode(VolumeProjectionMode mode)
    {
        if (_volume is null || _projectionMode == mode)
        {
            return false;
        }

        bool wasDvr = _projectionMode == VolumeProjectionMode.Dvr;
        _projectionMode = mode;

        if (mode == VolumeProjectionMode.Dvr)
        {
            _volumeOrientation = SliceOrientation.Axial;
            _planeTiltAroundColumn = 0;
            _planeTiltAroundRow = 0;
            _planeOffsetMm = 0;
            _volumeSliceIndex = Math.Max(0, (_renderBackend?.GetSliceCount(SliceOrientation.Axial) ?? _volume.SizeZ) / 2);
            _projectionThicknessMm = GetMaximumProjectionThicknessMm();
            _dvrCameraViewPreset = DvrCameraViewPreset.Bottom;
            (_dvrInitialForward, _dvrInitialUp) = GetStandardDvrCameraBasis(_dvrCameraViewPreset);
            _hasExplicitDvrCameraBasis = true;
            // Initialise the 3D camera; first render will go through ShowVolumeSlice
            // which detects DVR mode and uses the arbitrary-view renderer.
            InitializeDvrCamera(resetTransferWindow: true);
        }
        else if (wasDvr)
        {
            // Restore the windowing that was active before DVR mode
            _windowCenter = _preDvrWindowCenter;
            _windowWidth = Math.Max(1, _preDvrWindowWidth);
        }

        ApplyActiveColorLut();

        ShowVolumeSlice(_volumeSliceIndex);
        UpdateOverlay();
        NotifyViewStateChanged();
        return true;
    }

    public bool SetProjectionThicknessMm(double thicknessMm)
    {
        if (_volume is null)
        {
            return false;
        }

        double clamped = Math.Clamp(thicknessMm, GetMinimumProjectionThicknessMm(), GetMaximumProjectionThicknessMm());
        if (Math.Abs(clamped - _projectionThicknessMm) < 0.05)
        {
            return false;
        }

        _projectionThicknessMm = clamped;
        ShowVolumeSlice(_volumeSliceIndex);
        UpdateOverlay();
        NotifyViewStateChanged();
        return true;
    }

    private double GetMinimumProjectionThicknessMm()
    {
        if (_volume is null || _renderBackend is null)
        {
            return 1.0;
        }

        VolumeSlicePlane? plane = GetCurrentSlicePlane();
        return plane is not null
            ? Math.Max(0.1, plane.SliceSpacingMm)
            : Math.Max(0.1, _renderBackend.GetSliceSpacing(_volumeOrientation));
    }

    private double GetMaximumProjectionThicknessMm()
    {
        if (_volume is null || _renderBackend is null)
        {
            return 500.0;
        }

        VolumeSlicePlane? plane = GetCurrentSlicePlane();
        if (plane is not null)
        {
            return Math.Max(GetMinimumProjectionThicknessMm(), plane.DepthRangeMm + plane.SliceSpacingMm);
        }

        return Math.Max(
            GetMinimumProjectionThicknessMm(),
            _renderBackend.GetSliceSpacing(_volumeOrientation) * Math.Max(1, _renderBackend.GetSliceCount(_volumeOrientation)));
    }

    private int GetCurrentSliceCount()
    {
        if (_volume is null || _renderBackend is null)
        {
            return 0;
        }

        VolumeSlicePlane? plane = GetCurrentSlicePlane();
        return plane?.SliceCount ?? _renderBackend.GetSliceCount(_volumeOrientation);
    }

    private VolumeSlicePlane? GetCurrentSlicePlane(int? sliceIndex = null)
    {
        if (_volume is null || _renderBackend is null || !HasTiltedPlane)
        {
            return null;
        }

        VolumeSlicePlane plane = _renderBackend.CreateSlicePlane(
            _volumeOrientation,
            _planeTiltAroundColumn,
            _planeTiltAroundRow,
            _planeOffsetMm);

        if (sliceIndex is int explicitIndex)
        {
            return plane.WithSliceIndex(explicitIndex);
        }

        return plane.ClampOffset();
    }

    private VolumeSlicePlane? GetCurrentSlicePlaneForSliceIndex(int sliceIndex)
    {
        if (_volume is null || _renderBackend is null || !HasTiltedPlane)
        {
            return null;
        }

        VolumeSlicePlane plane = _renderBackend.CreateSlicePlane(
            _volumeOrientation,
            _planeTiltAroundColumn,
            _planeTiltAroundRow,
            _planeOffsetMm);

        return plane.WithSliceIndex(sliceIndex);
    }

    private double GetCurrentPlaneOffsetMm()
    {
        return HasTiltedPlane ? _planeOffsetMm : 0;
    }

    private bool IsPlaneTiltToolActive() => _navigationTool == NavigationTool.TiltPlane && _volume is not null;

    private bool BeginPlaneTiltDrag(Point pos, IPointer pointer)
    {
        if (!IsPlaneTiltToolActive())
        {
            return false;
        }

        _isPlaneTiltDragging = true;
        _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
        _planeTiltConstraintModeActive = false;
        _planeTiltDragStart = pos;
        _planeTiltStartAroundColumn = _planeTiltAroundColumn;
        _planeTiltStartAroundRow = _planeTiltAroundRow;
        if (IsDvrMode && _hasExplicitDvrCameraBasis)
        {
            _planeTiltStartAroundColumn = _dvrAzimuth;
            _planeTiltStartAroundRow = _dvrElevation;
        }
        _planeTiltControlPressed = false;
        _planeOffsetMm = HasTiltedPlane
            ? GetCurrentPlaneOffsetMm()
            : (_renderBackend ?? (IRenderBackend)new LocalRenderBackend(_volume!)).CreateSlicePlane(_volumeOrientation, 0, 0, 0).WithSliceIndex(_volumeSliceIndex).CurrentOffsetMm;
        _isLeftDragging = true;
        _mouseDownPos = pos;
        Focus();
        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        AttachCapturedPointerHandlers();
        Cursor = new Cursor(StandardCursorType.SizeAll);
        return true;
    }

    private bool UpdatePlaneTiltDrag(Point pos, KeyModifiers modifiers)
    {
        if (!_isPlaneTiltDragging || _volume is null)
        {
            return false;
        }

        const double sensitivity = Math.PI / 300.0;
        double dx = pos.X - _planeTiltDragStart.X;
        double dy = pos.Y - _planeTiltDragStart.Y;
        bool constrainAxis = _planeTiltConstraintModeActive || _planeTiltControlPressed || modifiers.HasFlag(KeyModifiers.Control);

        if (IsDvrMode && _hasExplicitDvrCameraBasis)
        {
            if (constrainAxis && !_planeTiltConstraintModeActive)
            {
                _planeTiltConstraintModeActive = true;
                _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
                _planeTiltDragStart = pos;
                _planeTiltStartAroundColumn = _dvrAzimuth;
                _planeTiltStartAroundRow = _dvrElevation;
                dx = 0;
                dy = 0;
            }

            double nextAzimuth;
            double nextElevation;
            if (constrainAxis)
            {
                if (_planeTiltConstraintAxis == PlaneTiltConstraintAxis.None)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy) && Math.Abs(dx) > 1e-3)
                    {
                        _planeTiltConstraintAxis = PlaneTiltConstraintAxis.Horizontal;
                    }
                    else if (Math.Abs(dy) > 1e-3)
                    {
                        _planeTiltConstraintAxis = PlaneTiltConstraintAxis.Vertical;
                    }
                }

                nextAzimuth = _planeTiltStartAroundColumn;
                nextElevation = _planeTiltStartAroundRow;
                switch (_planeTiltConstraintAxis)
                {
                    case PlaneTiltConstraintAxis.Horizontal:
                        nextAzimuth = _planeTiltStartAroundColumn + dx * sensitivity;
                        break;
                    case PlaneTiltConstraintAxis.Vertical:
                        nextElevation = Math.Clamp(_planeTiltStartAroundRow - dy * sensitivity, -Math.PI * 0.48, Math.PI * 0.48);
                        break;
                }
            }
            else
            {
                _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
                nextAzimuth = _planeTiltStartAroundColumn + dx * sensitivity;
                nextElevation = Math.Clamp(_planeTiltStartAroundRow - dy * sensitivity, -Math.PI * 0.48, Math.PI * 0.48);
            }

            if (Math.Abs(nextAzimuth - _dvrAzimuth) < 1e-5 && Math.Abs(nextElevation - _dvrElevation) < 1e-5)
            {
                return true;
            }

            _dvrAzimuth = nextAzimuth;
            _dvrElevation = nextElevation;
            _dvrCameraViewPreset = DvrCameraViewPreset.Custom;

            RenderDvrViewFast();
            ScheduleDvrSharpRender();
            UpdateOverlay();
            NotifyViewStateChanged();
            return true;
        }

        if (constrainAxis && !_planeTiltConstraintModeActive)
        {
            _planeTiltConstraintModeActive = true;
            _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
            _planeTiltDragStart = pos;
            _planeTiltStartAroundColumn = _planeTiltAroundColumn;
            _planeTiltStartAroundRow = _planeTiltAroundRow;
            dx = 0;
            dy = 0;
        }

        double nextAroundColumn;
        double nextAroundRow;

        if (constrainAxis)
        {
            if (_planeTiltConstraintAxis == PlaneTiltConstraintAxis.None)
            {
                if (Math.Abs(dx) >= Math.Abs(dy) && Math.Abs(dx) > 1e-3)
                {
                    _planeTiltConstraintAxis = PlaneTiltConstraintAxis.Horizontal;
                }
                else if (Math.Abs(dy) > 1e-3)
                {
                    _planeTiltConstraintAxis = PlaneTiltConstraintAxis.Vertical;
                }
            }

            nextAroundColumn = _planeTiltStartAroundColumn;
            nextAroundRow = _planeTiltStartAroundRow;
            switch (_planeTiltConstraintAxis)
            {
                case PlaneTiltConstraintAxis.Horizontal:
                    nextAroundColumn = _planeTiltStartAroundColumn + dx * sensitivity;
                    break;
                case PlaneTiltConstraintAxis.Vertical:
                    nextAroundRow = _planeTiltStartAroundRow - dy * sensitivity;
                    break;
            }
        }
        else
        {
            _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
            nextAroundColumn = Math.Clamp(_planeTiltStartAroundColumn + dx * sensitivity, -Math.PI * 0.45, Math.PI * 0.45);
            nextAroundRow = Math.Clamp(_planeTiltStartAroundRow - dy * sensitivity, -Math.PI * 0.45, Math.PI * 0.45);
        }

        if (Math.Abs(nextAroundColumn - _planeTiltAroundColumn) < 1e-5 && Math.Abs(nextAroundRow - _planeTiltAroundRow) < 1e-5)
        {
            return true;
        }

        _planeTiltAroundColumn = nextAroundColumn;
        _planeTiltAroundRow = nextAroundRow;

        VolumeSlicePlane? plane = GetCurrentSlicePlane();
        if (plane is not null)
        {
            _planeOffsetMm = Math.Clamp(_planeOffsetMm, plane.MinOffsetMm, plane.MaxOffsetMm);
            _volumeSliceIndex = plane.GetSliceIndexForOffset(_planeOffsetMm);
        }

        _preserveTiltOffsetDuringNextShow = true;
        ShowVolumeSlicePreservingNavigation(_volumeSliceIndex);
        UpdateOverlay();
        NotifyViewStateChanged();
        return true;
    }

    private bool EndPlaneTiltDrag()
    {
        if (!_isPlaneTiltDragging)
        {
            return false;
        }

        _isPlaneTiltDragging = false;
        _planeTiltConstraintAxis = PlaneTiltConstraintAxis.None;
        _planeTiltConstraintModeActive = false;
        _planeTiltControlPressed = false;
        return true;
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        bool controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (controlPressed)
        {
            bool handled = e.Key switch
            {
                Key.Z when shiftPressed => TryRedoRoiStep(),
                Key.Z => TryUndoRoiStep(),
                Key.Y => TryRedoRoiStep(),
                _ => false,
            };

            if (handled)
            {
                e.Handled = true;
                return;
            }
        }

        if (!_isPlaneTiltDragging)
        {
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _planeTiltControlPressed = true;
        }
    }

    private void OnPanelKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _planeTiltControlPressed = false;
        }
    }

    private bool ShowVolumeSlicePreservingNavigation(int sliceIndex)
    {
        bool hasNavigationState = TryCaptureNavigationState(out NavigationState navigationState);
        SpatialVector3D patientCenter = default;
        bool hasPatientCenter = hasNavigationState && TryCaptureNavigationPatientCenter(navigationState, out patientCenter);
        bool changed = ShowVolumeSlice(sliceIndex);
        if (!changed || !hasNavigationState || _pendingInitialFitToWindow)
        {
            return changed;
        }

        if (hasPatientCenter)
        {
            ApplyAbsoluteNavigationStateToPatientPoint(navigationState, patientCenter);
        }
        else
        {
            ApplyAbsoluteNavigationState(navigationState);
        }

        return true;
    }

    private bool TryCaptureNavigationPatientCenter(NavigationState navigationState, out SpatialVector3D patientCenter)
    {
        patientCenter = default;
        if (SpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return false;
        }

        patientCenter = metadata.PatientPointFromPixel(navigationState.CenterImagePoint);
        return true;
    }

    private void ApplyAbsoluteNavigationState(NavigationState state)
    {
        if (_rawPixelData is null && _volumeSlicePixels is null)
        {
            return;
        }

        Point clampedCenterImagePoint = ClampImagePointToCurrentImage(state.CenterImagePoint);

        _fitToWindow = false;
        _zoomFactor = Math.Clamp(state.ZoomFactor, 0.01, 20.0);
        ApplyZoomTransform();
        RenderImage();

        double cx = RootGrid.Bounds.Width / 2.0;
        double cy = RootGrid.Bounds.Height / 2.0;
        Point displayPoint = ImageToDisplayPoint(clampedCenterImagePoint);
        _panX = cx - (displayPoint.X * _zoomFactor);
        _panY = cy - (displayPoint.Y * _zoomFactor);
        _panTransform.X = _panX;
        _panTransform.Y = _panY;
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        UpdateOverlay();
        ZoomChanged?.Invoke();
    }

    private void ApplyAbsoluteNavigationStateToPatientPoint(NavigationState state, SpatialVector3D patientPoint)
    {
        if (SpatialMetadata is not DicomSpatialMetadata metadata)
        {
            ApplyAbsoluteNavigationState(state);
            return;
        }

        ApplyAbsoluteNavigationState(state with { CenterImagePoint = ClampImagePointToCurrentImage(metadata.PixelPointFromPatient(patientPoint)) });
    }

    private Point ClampImagePointToCurrentImage(Point imagePoint)
    {
        double maxX = Math.Max(0, _imageWidth - 1);
        double maxY = Math.Max(0, _imageHeight - 1);
        return new Point(
            Math.Clamp(imagePoint.X, 0, maxX),
            Math.Clamp(imagePoint.Y, 0, maxY));
    }

    private static string GetProjectionModeLabel(VolumeProjectionMode mode) => mode switch
    {
        VolumeProjectionMode.Mpr => "MPR",
        VolumeProjectionMode.MipPr => "MipPR",
        VolumeProjectionMode.MinPr => "MinPR",
        VolumeProjectionMode.MpVrt => "MPVRT",
        VolumeProjectionMode.Dvr => "DVR",
        _ => "MPR",
    };

    private static string GetOrientationLabel(SliceOrientation orientation) => orientation switch
    {
        SliceOrientation.Axial => "Axial",
        SliceOrientation.Coronal => "Coronal",
        SliceOrientation.Sagittal => "Sagittal",
        _ => "Axial",
    };

    // ==============================================================================================
    // Rendering (ported from TdView.SetDimension → TdcmImgObj pipeline)
    // ==============================================================================================

    private void RenderImage() => RenderImage(sharp: true);

    /// <summary>
    /// Renders the image at fast (native) or sharp (display) resolution.
    /// Use <see cref="RenderImageFastThenSharp"/> during continuous interaction
    /// to get immediate visual feedback with deferred high-quality rendering.
    /// </summary>
    private void RenderImage(bool sharp)
    {
        _isSharpRender = sharp;
        int sourceRenderWidth = GetSourceRenderPixelWidth();
        int sourceRenderHeight = GetSourceRenderPixelHeight();
        int renderWidth = GetRenderPixelWidth();
        int renderHeight = GetRenderPixelHeight();
        if (!EnsureDisplayBitmap(renderWidth, renderHeight))
        {
            return;
        }

        // Volume-based pre-colored rendering path
        if (_volumeSliceBgraPixels is not null && _displayBitmap is not null)
        {
            int sourcePixelCount = sourceRenderWidth * sourceRenderHeight;
            int sourceRequiredBytes = sourcePixelCount * 4;
            byte[] sourceBgra;

            if (sourceRenderWidth == _imageWidth && sourceRenderHeight == _imageHeight)
            {
                // Need a mutable copy if we're compositing overlays into native-size buffer.
                if (_segmentationMaskOverlays.Count > 0)
                {
                    sourceBgra = HasViewTransform()
                        ? EnsureViewTransformBuffer(sourceRequiredBytes)
                        : EnsureRenderBuffer(sourceRequiredBytes);
                    Buffer.BlockCopy(_volumeSliceBgraPixels, 0, sourceBgra, 0, sourceRequiredBytes);
                }
                else
                {
                    sourceBgra = _volumeSliceBgraPixels;
                }
            }
            else
            {
                sourceBgra = HasViewTransform()
                    ? EnsureViewTransformBuffer(sourceRequiredBytes)
                    : EnsureRenderBuffer(sourceRequiredBytes);
                ResampleBgraBuffer(_volumeSliceBgraPixels, _imageWidth, _imageHeight, sourceBgra, sourceRenderWidth, sourceRenderHeight);
            }

            ApplySegmentationMaskOverlays(sourceBgra, sourceRenderWidth, sourceRenderHeight);

            byte[] outputBgra = sourceBgra;
            int usedLength = sourceRequiredBytes;
            if (HasViewTransform())
            {
                int requiredBytes = renderWidth * renderHeight * 4;
                outputBgra = EnsureRenderBuffer(requiredBytes);
                TransformBgraBuffer(sourceBgra, sourceRenderWidth, sourceRenderHeight, outputBgra);
                usedLength = requiredBytes;
            }

            CopyToDisplayBitmap(outputBgra, usedLength, renderWidth, renderHeight);
            return;
        }

        // Volume-based rendering path
        if (_volumeSlicePixels is not null && _displayBitmap is not null)
        {
            int sourcePixelCount = sourceRenderWidth * sourceRenderHeight;
            int sourceRequiredBytes = sourcePixelCount * 4;
            byte[] sourceBgra = HasViewTransform()
                ? EnsureViewTransformBuffer(sourceRequiredBytes)
                : EnsureRenderBuffer(sourceRequiredBytes);

            DicomPixelRenderer.RenderRescaled16BitScaled(
                _volumeSlicePixels,
                _imageWidth, _imageHeight,
                _windowCenter, _windowWidth,
                _lutR, _lutG, _lutB,
                _isMonochrome1,
                sourceRenderWidth,
                sourceRenderHeight,
                sourceBgra);

            ApplySegmentationMaskOverlays(sourceBgra, sourceRenderWidth, sourceRenderHeight);

            byte[] outputBgra = sourceBgra;
            int usedLength = sourceRequiredBytes;
            if (HasViewTransform())
            {
                int requiredBytes = renderWidth * renderHeight * 4;
                outputBgra = EnsureRenderBuffer(requiredBytes);
                TransformBgraBuffer(sourceBgra, sourceRenderWidth, sourceRenderHeight, outputBgra);
                usedLength = requiredBytes;
            }

            CopyToDisplayBitmap(outputBgra, usedLength, renderWidth, renderHeight);
            return;
        }

        // Legacy file-based rendering path
        if (_rawPixelData == null || _displayBitmap == null) return;

        {
            int sourcePixelCount = sourceRenderWidth * sourceRenderHeight;
            int sourceRequiredBytes = sourcePixelCount * 4;
            byte[] sourceBgra = HasViewTransform()
                ? EnsureViewTransformBuffer(sourceRequiredBytes)
                : EnsureRenderBuffer(sourceRequiredBytes);

            DicomPixelRenderer.RenderScaled(
                _rawPixelData,
                _imageWidth, _imageHeight,
                _bitsAllocated, _bitsStored,
                _isSigned, _samplesPerPixel,
                _rescaleSlope, _rescaleIntercept,
                _windowCenter, _windowWidth,
                _lutR, _lutG, _lutB,
                _isMonochrome1,
                _photometricInterpretation,
                _planarConfiguration,
                sourceRenderWidth,
                sourceRenderHeight,
                sourceBgra);

            ApplyBitmapOverlays(sourceBgra, sourceRenderWidth, sourceRenderHeight);

            byte[] outputBgra = sourceBgra;
            int usedLength = sourceRequiredBytes;
            if (HasViewTransform())
            {
                int requiredBytes = renderWidth * renderHeight * 4;
                outputBgra = EnsureRenderBuffer(requiredBytes);
                TransformBgraBuffer(sourceBgra, sourceRenderWidth, sourceRenderHeight, outputBgra);
                usedLength = requiredBytes;
            }

            CopyToDisplayBitmap(outputBgra, usedLength, renderWidth, renderHeight);
        }
    }

    /// <summary>
    /// Renders the image during continuous interactions (scroll, tilt, window drag, zoom).
    /// When OpenCL is available (powerful workstation), renders at sharp display resolution
    /// immediately — the hardware is fast enough. Otherwise falls back to rendering at native
    /// resolution first with a deferred sharp re-render after an idle delay.
    /// </summary>
    private void RenderImageFastThenSharp()
    {
        if (_renderBackend?.SupportsHighQualityInteractive ?? VolumeComputeBackend.CanUseOpenCl)
        {
            // Powerful hardware: render sharp immediately — no deferred pass needed.
            RenderImage(sharp: true);
            return;
        }

        RenderImage(sharp: false);
        ScheduleSharpRender();
    }

    private void ScheduleSharpRender()
    {
        // Backend already renders at full quality during interaction — no deferred pass needed.
        if (_renderBackend?.SupportsHighQualityInteractive ?? VolumeComputeBackend.CanUseOpenCl)
        {
            return;
        }

        if (_sharpRenderTimer is null)
        {
            _sharpRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _sharpRenderTimer.Tick += (_, _) =>
            {
                _sharpRenderTimer.Stop();
                RenderImage(sharp: true);
            };
        }

        // Restart the timer on each call — only fires after 150ms idle
        _sharpRenderTimer.Stop();
        _sharpRenderTimer.Start();
    }

    private bool EnsureDisplayBitmap(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }

        if (_displayBitmap is not null &&
            _displayBitmap.PixelSize.Width == pixelWidth &&
            _displayBitmap.PixelSize.Height == pixelHeight)
        {
            return true;
        }

        _displayBitmap = new WriteableBitmap(
            new PixelSize(pixelWidth, pixelHeight),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Opaque);
        DicomImage.Source = _displayBitmap;
        DicomImage.InvalidateMeasure();
        return true;
    }

    private byte[] EnsureRenderBuffer(int requiredBytes)
    {
        if (_renderBuffer is null || _renderBuffer.Length < requiredBytes)
        {
            _renderBuffer = new byte[requiredBytes];
        }

        return _renderBuffer;
    }

    private byte[] EnsureViewTransformBuffer(int requiredBytes)
    {
        if (_viewTransformBuffer is null || _viewTransformBuffer.Length < requiredBytes)
        {
            _viewTransformBuffer = new byte[requiredBytes];
        }

        return _viewTransformBuffer;
    }

    private void CopyToDisplayBitmap(byte[] outputBgra, int usedLength, int bitmapWidth, int bitmapHeight)
    {
        if (_displayBitmap is null) return;

        using (var fb = _displayBitmap.Lock())
        {
            int stride = fb.RowBytes;
            int rowSize = bitmapWidth * 4;
            IntPtr addr = fb.Address;

            if (stride == rowSize)
            {
                Marshal.Copy(outputBgra, 0, addr, usedLength);
            }
            else
            {
                for (int y = 0; y < bitmapHeight; y++)
                    Marshal.Copy(outputBgra, y * rowSize, IntPtr.Add(addr, y * stride), rowSize);
            }
        }

        DicomImage.InvalidateVisual();
    }

    private void LoadBitmapOverlays(DicomDataset dataset)
    {
        _bitmapOverlays.Clear();

        for (ushort group = 0x6000; group <= 0x601E; group += 2)
        {
            var rowsTag = new DicomTag(group, 0x0010);
            var colsTag = new DicomTag(group, 0x0011);
            if (!dataset.Contains(rowsTag) || !dataset.Contains(colsTag))
            {
                continue;
            }

            int rows = dataset.GetSingleValueOrDefault(rowsTag, 0);
            int columns = dataset.GetSingleValueOrDefault(colsTag, 0);
            if (rows <= 0 || columns <= 0)
            {
                continue;
            }

            int originRow = 1;
            int originColumn = 1;
            var originTag = new DicomTag(group, 0x0050);
            if (dataset.Contains(originTag))
            {
                try
                {
                    short[] originValues = dataset.GetValues<short>(originTag);
                    if (originValues.Length >= 2)
                    {
                        originRow = originValues[0];
                        originColumn = originValues[1];
                    }
                }
                catch
                {
                }
            }

            byte[]? overlayBytes = ReadTagValueAsBytes(dataset, new DicomTag(group, 0x3000));
            bool[]? pixels = overlayBytes is { Length: > 0 }
                ? UnpackOverlayBits(overlayBytes, rows * columns)
                : TryExtractEmbeddedOverlay(dataset, group, rows, columns);

            if (pixels is null)
            {
                continue;
            }

            _bitmapOverlays.Add(new BitmapOverlayPlane(
                group,
                columns,
                rows,
                originColumn - 1,
                originRow - 1,
                pixels));
        }
    }

    private bool[]? TryExtractEmbeddedOverlay(DicomDataset dataset, ushort group, int rows, int columns)
    {
        var bitsAllocatedTag = new DicomTag(group, 0x0100);
        var bitPositionTag = new DicomTag(group, 0x0102);
        if (_rawPixelData is null || !dataset.Contains(bitsAllocatedTag) || !dataset.Contains(bitPositionTag))
        {
            return null;
        }

        int overlayBitsAllocated = dataset.GetSingleValueOrDefault(bitsAllocatedTag, 0);
        int overlayBitPosition = dataset.GetSingleValueOrDefault(bitPositionTag, -1);
        if (overlayBitsAllocated <= 1 || overlayBitPosition < 0)
        {
            return null;
        }

        int pixelCount = rows * columns;
        bool[] pixels = new bool[pixelCount];

        if (_bitsAllocated >= 16)
        {
            var source = MemoryMarshal.Cast<byte, ushort>(_rawPixelData.AsSpan());
            int count = Math.Min(source.Length, pixelCount);
            for (int index = 0; index < count; index++)
            {
                pixels[index] = ((source[index] >> overlayBitPosition) & 0x1) != 0;
            }
        }
        else
        {
            int count = Math.Min(_rawPixelData.Length, pixelCount);
            for (int index = 0; index < count; index++)
            {
                pixels[index] = (((int)_rawPixelData[index] >> overlayBitPosition) & 0x1) != 0;
            }
        }

        return pixels;
    }

    private void ApplyBitmapOverlays(byte[] outputBgra, int outputWidth, int outputHeight)
    {
        if (_bitmapOverlays.Count == 0 || outputWidth <= 0 || outputHeight <= 0)
        {
            return;
        }

        const int overlayR = 255;
        const int overlayG = 216;
        const int overlayB = 32;
        const double overlayWeight = 0.72;
        const double baseWeight = 1.0 - overlayWeight;

        foreach (BitmapOverlayPlane overlay in _bitmapOverlays)
        {
            for (int overlayY = 0; overlayY < overlay.Height; overlayY++)
            {
                int imageY = overlay.OriginY + overlayY;
                if (imageY < 0 || imageY >= _imageHeight)
                {
                    continue;
                }

                for (int overlayX = 0; overlayX < overlay.Width; overlayX++)
                {
                    int maskIndex = (overlayY * overlay.Width) + overlayX;
                    if (!overlay.Pixels[maskIndex])
                    {
                        continue;
                    }

                    int imageX = overlay.OriginX + overlayX;
                    if (imageX < 0 || imageX >= _imageWidth)
                    {
                        continue;
                    }

                    int scaledX = Math.Clamp((int)Math.Round(((imageX + 0.5) * outputWidth / _imageWidth) - 0.5), 0, outputWidth - 1);
                    int scaledY = Math.Clamp((int)Math.Round(((imageY + 0.5) * outputHeight / _imageHeight) - 0.5), 0, outputHeight - 1);
                    int outputIndex = ((scaledY * outputWidth) + scaledX) * 4;
                    outputBgra[outputIndex] = (byte)Math.Clamp((outputBgra[outputIndex] * baseWeight) + (overlayB * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 1] = (byte)Math.Clamp((outputBgra[outputIndex + 1] * baseWeight) + (overlayG * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 2] = (byte)Math.Clamp((outputBgra[outputIndex + 2] * baseWeight) + (overlayR * overlayWeight), 0, 255);
                    outputBgra[outputIndex + 3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Composites visible segmentation mask overlays onto the BGRA pixel buffer.
    /// Supports axial, coronal and sagittal orientations.
    /// Uses <see cref="Parallel.For"/> for the scaled path to spread the per-row work
    /// across available cores instead of blocking the UI thread in a tight nested loop.
    /// </summary>
    private void ApplySegmentationMaskOverlays(byte[] outputBgra, int outputWidth, int outputHeight)
    {
        if (_segmentationMaskOverlays.Count == 0 || _volume is null || outputWidth <= 0 || outputHeight <= 0)
        {
            return;
        }

        foreach (SegmentationMaskOverlay overlay in _segmentationMaskOverlays)
        {
            // Skip tilted planes — the mask is axis-aligned with the volume.
            if (HasTiltedPlane)
            {
                continue;
            }

            // The mask grid must match the volume grid dimensions.
            VolumeGridGeometry g = overlay.Mask.Geometry;
            if (g.SizeX != _volume.SizeX || g.SizeY != _volume.SizeY || g.SizeZ != _volume.SizeZ)
            {
                continue;
            }

            int sliceIndex = _volumeSliceIndex;
            byte[] bits = overlay.Mask.Storage.Data;
            byte oB = overlay.OverlayB, oG = overlay.OverlayG, oR = overlay.OverlayR;
            double alpha = overlay.OverlayA / 255.0;
            double invAlpha = 1.0 - alpha;

            // Determine mask dimensions and voxel-addressing based on orientation.
            int maskW, maskH;
            Func<int, int, int>? linearIndexFunc;

            switch (_volumeOrientation)
            {
                case SliceOrientation.Axial:
                    if (sliceIndex < 0 || sliceIndex >= g.SizeZ) continue;
                    maskW = g.SizeX;
                    maskH = g.SizeY;
                    {
                        int baseIdx = sliceIndex * maskW * maskH;
                        linearIndexFunc = (mx, my) => baseIdx + (my * maskW) + mx;
                    }
                    break;

                case SliceOrientation.Coronal:
                    if (sliceIndex < 0 || sliceIndex >= g.SizeY) continue;
                    maskW = g.SizeX;
                    maskH = g.SizeZ;
                    linearIndexFunc = (mx, mz) =>
                    {
                        // Coronal: row=Z (top=last Z), col=X
                        int z = g.SizeZ - 1 - mz;
                        return (z * g.SizeY * g.SizeX) + (sliceIndex * g.SizeX) + mx;
                    };
                    break;

                case SliceOrientation.Sagittal:
                    if (sliceIndex < 0 || sliceIndex >= g.SizeX) continue;
                    maskW = g.SizeY;
                    maskH = g.SizeZ;
                    linearIndexFunc = (my, mz) =>
                    {
                        // Sagittal: row=Z (top=last Z), col=Y
                        int z = g.SizeZ - 1 - mz;
                        return (z * g.SizeY * g.SizeX) + (my * g.SizeX) + sliceIndex;
                    };
                    break;

                default:
                    continue;
            }

            // Fast-path: native resolution — delegate to the optimised buffer composite for axial.
            if (_volumeOrientation == SliceOrientation.Axial
                && outputWidth == _imageWidth && outputHeight == _imageHeight)
            {
                overlay.Buffer.CompositeAxialSliceIntoBgra(
                    sliceIndex, outputBgra, outputWidth * 4,
                    outputWidth, outputHeight,
                    oB, oG, oR, overlay.OverlayA);
                continue;
            }

            // Scaled / non-axial: parallel per-row compositing.
            double scaleX = (double)outputWidth / maskW;
            double scaleY = (double)outputHeight / maskH;

            Parallel.For(0, maskH, my =>
            {
                int oy = Math.Clamp((int)(my * scaleY), 0, outputHeight - 1);
                for (int mx = 0; mx < maskW; mx++)
                {
                    int linearIndex = linearIndexFunc(mx, my);
                    int byteIndex = linearIndex >> 3;
                    byte bitMask = (byte)(1 << (linearIndex & 7));

                    if ((bits[byteIndex] & bitMask) == 0)
                    {
                        continue;
                    }

                    int ox = Math.Clamp((int)(mx * scaleX), 0, outputWidth - 1);
                    int px = ((oy * outputWidth) + ox) * 4;
                    outputBgra[px] = (byte)Math.Clamp(outputBgra[px] * invAlpha + oB * alpha, 0, 255);
                    outputBgra[px + 1] = (byte)Math.Clamp(outputBgra[px + 1] * invAlpha + oG * alpha, 0, 255);
                    outputBgra[px + 2] = (byte)Math.Clamp(outputBgra[px + 2] * invAlpha + oR * alpha, 0, 255);
                    outputBgra[px + 3] = 255;
                }
            });
        }
    }

    private static int ComputeExpectedPixelBytes(int width, int height, int samplesPerPixel, int bitsAllocated, string photometricInterpretation)
    {
        int bytesPerSample = Math.Max(1, bitsAllocated / 8);
        string photometric = (photometricInterpretation ?? string.Empty).Trim().ToUpperInvariant();
        if (samplesPerPixel >= 3 && (photometric == "YBR_FULL_422" || photometric == "YBR_PARTIAL_422"))
        {
            return width * height * 2 * bytesPerSample;
        }

        return width * height * samplesPerPixel * bytesPerSample;
    }

    private static byte[]? ReadTagValueAsBytes(DicomDataset dataset, DicomTag tag)
    {
        try
        {
            var item = dataset.GetDicomItem<DicomItem>(tag);
            if (item is DicomElement element)
            {
                return element.Buffer?.Data;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool[] UnpackOverlayBits(byte[] packedBits, int pixelCount)
    {
        bool[] pixels = new bool[pixelCount];
        for (int index = 0; index < pixelCount; index++)
        {
            int byteIndex = index >> 3;
            int bitIndex = index & 0x7;
            if (byteIndex >= packedBits.Length)
            {
                break;
            }

            pixels[index] = ((packedBits[byteIndex] >> bitIndex) & 0x1) != 0;
        }

        return pixels;
    }

    // ==============================================================================================
    // Color LUT (ported from TdView.SetColorLUT)
    // ==============================================================================================

    private void SetColorLutInternal(int scheme)
    {
        _colorScheme = scheme;
        var (r, g, b) = ShouldUseDvrAutoColorLut()
            ? ColorLut.CreateAutoCtDvrLut(
                _windowCenter,
                _windowWidth,
                _dvrTransferCenter,
                _dvrTransferWidth,
                _dvrPreset)
            : ColorLut.GetLut(scheme);

        _lutR = r;
        _lutG = g;
        _lutB = b;
    }

    private void ApplyActiveColorLut()
    {
        SetColorLutInternal(_colorScheme);
    }

    private bool ShouldUseDvrAutoColorLut()
    {
        return _dvrAutoColorLutEnabled && IsDvrMode && SupportsDvrAutoColorLut;
    }

    public void SetDvrAutoColorLutEnabled(bool enabled)
    {
        if (_dvrAutoColorLutEnabled == enabled)
        {
            return;
        }

        _dvrAutoColorLutEnabled = enabled;
        if (_volume is not null)
        {
            RebuildDvrTransferFunction();
        }

        ApplyActiveColorLut();

        if (IsDvrMode)
        {
            RenderDvrViewFast();
            ScheduleDvrSharpRender();
        }
        else
        {
            RenderImage();
        }

        UpdateOverlay();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Changes the color lookup table and re-renders.
    /// </summary>
    public void SetColorScheme(int scheme)
    {
        SetColorLutInternal(scheme);
        RenderImage();
        UpdateOverlay();
        NotifyViewStateChanged();
    }

    // ==============================================================================================
    // Zoom (ported from TdView.DetermineZoom, ImageZoom, dcmZoomIn/Out, SetFitInZoom)
    // ==============================================================================================

    /// <summary>
    /// Computes and applies fit-to-window zoom (ported from TdView.DetermineZoom).
    /// </summary>
    public void ApplyFitToWindow()
    {
        double fitZoomFactor = GetFitToWindowZoomFactor();
        if (fitZoomFactor <= 0)
        {
            return;
        }

        _zoomFactor = fitZoomFactor;

        _fitToWindow = true;
        ApplyZoomTransform();
        CenterImage();
    RenderImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    private void ApplyInitialFitToWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_fitToWindow || (_rawPixelData == null && _volumeSlicePixels == null))
            {
                return;
            }

            if (RootGrid.Bounds.Width <= 0 || RootGrid.Bounds.Height <= 0)
            {
                return;
            }

            ApplyFitToWindow();
            _pendingInitialFitToWindow = false;
            UpdateOverlay();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyFitToWindowLayoutWithoutRender()
    {
        double fitZoomFactor = GetFitToWindowZoomFactor();
        if (fitZoomFactor <= 0)
        {
            return;
        }

        _zoomFactor = fitZoomFactor;
        _fitToWindow = true;
        ApplyZoomTransform();
        CenterImage();
    }

    /// <summary>
    /// Sets zoom to 1:1 native resolution.
    /// </summary>
    public void ZoomToOriginal()
    {
        _fitToWindow = false;
        _zoomFactor = 1.0;
        ApplyZoomTransform();
        CenterImage();
        RenderImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Zooms in by 10% (ported from TdView.dcmZoomIn).
    /// </summary>
    public void ZoomIn()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Min(20.0, _zoomFactor * 1.1);
        ApplyZoomTransform();
        RenderImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Zooms out by 10% (ported from TdView.dcmZoomOut).
    /// </summary>
    public void ZoomOut()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Max(0.01, _zoomFactor / 1.1);
        ApplyZoomTransform();
        RenderImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    /// <summary>
    /// Sets an exact zoom factor.
    /// </summary>
    public void SetZoom(double factor)
    {
        _fitToWindow = false;
        _zoomFactor = Math.Clamp(factor, 0.01, 20.0);
        ApplyZoomTransform();
        RenderImage();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public DisplayState CaptureDisplayState()
    {
        return new DisplayState(
            _windowCenter,
            _windowWidth,
            _zoomFactor,
            _fitToWindow,
            _panX,
            _panY,
            _colorScheme,
            _volumeOrientation,
            _projectionMode,
            _projectionThicknessMm,
            _planeTiltAroundColumn,
                _planeTiltAroundRow,
                _viewRotationQuarterTurns,
                _viewFlipHorizontal,
                _viewFlipVertical);
    }

    public bool TryCaptureNavigationState(out NavigationState state)
    {
        state = default!;

        Point controlCenter = new(RootGrid.Bounds.Width / 2.0, RootGrid.Bounds.Height / 2.0);
        if (!TryGetImagePoint(controlCenter, out Point centerImagePoint))
        {
            if ((_rawPixelData is null && _volumeSlicePixels is null) || _zoomFactor <= 0)
            {
                return false;
            }

            centerImagePoint = DisplayToImagePoint(new Point(
                (controlCenter.X - _panX) / _zoomFactor,
                (controlCenter.Y - _panY) / _zoomFactor));
        }

        double fitZoomFactor = GetFitToWindowZoomFactor();
        double relativeZoomFactor = fitZoomFactor > 0
            ? Math.Clamp(_zoomFactor / fitZoomFactor, 0.01, 20.0)
            : Math.Clamp(_zoomFactor, 0.01, 20.0);

        state = new NavigationState(
            _zoomFactor,
            relativeZoomFactor,
            _fitToWindow,
            centerImagePoint);
        return true;
    }

    public void ApplyDisplayState(DisplayState state)
    {
        _windowCenter = state.WindowCenter;
        _windowWidth = Math.Max(1, state.WindowWidth);

        if (_colorScheme != state.ColorScheme)
        {
            SetColorLutInternal(state.ColorScheme);
        }

        _volumeOrientation = state.Orientation;
        _projectionMode = state.ProjectionMode;
        _planeTiltAroundColumn = state.PlaneTiltAroundColumnRadians;
        _planeTiltAroundRow = state.PlaneTiltAroundRowRadians;
        _projectionThicknessMm = Math.Clamp(
            state.ProjectionThicknessMm,
            GetMinimumProjectionThicknessMm(),
            GetMaximumProjectionThicknessMm());
        _viewRotationQuarterTurns = NormalizeQuarterTurns(state.ViewRotationQuarterTurns);
        _viewFlipHorizontal = state.ViewFlipHorizontal;
        _viewFlipVertical = state.ViewFlipVertical;
        if (_projectionMode == VolumeProjectionMode.Dvr)
        {
            InitializeDvrCamera(resetTransferWindow: false);
        }
        ApplyActiveColorLut();

        if (state.FitToWindow)
        {
            _fitToWindow = true;
            ApplyFitToWindow();
        }
        else
        {
            _fitToWindow = false;
            _zoomFactor = Math.Clamp(state.ZoomFactor, 0.01, 20.0);
            ApplyZoomTransform();
            _panX = state.PanX;
            _panY = state.PanY;
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            ZoomChanged?.Invoke();
            NotifyViewStateChanged();
        }

        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
    }

    public void ApplyNavigationState(NavigationState state)
    {
        if (_rawPixelData is null && _volumeSlicePixels is null)
        {
            return;
        }

        if (state.FitToWindow)
        {
            _fitToWindow = true;
            ApplyFitToWindow();
            UpdateOverlay();
            ZoomChanged?.Invoke();
            NotifyViewStateChanged();
            return;
        }

        _fitToWindow = false;
        double fitZoomFactor = GetFitToWindowZoomFactor();
        double targetZoomFactor = fitZoomFactor > 0
            ? fitZoomFactor * Math.Clamp(state.RelativeZoomFactor, 0.01, 20.0)
            : Math.Clamp(state.ZoomFactor, 0.01, 20.0);
        _zoomFactor = Math.Clamp(targetZoomFactor, 0.01, 20.0);
        ApplyZoomTransform();
        RenderImage();

        double cx = RootGrid.Bounds.Width / 2.0;
        double cy = RootGrid.Bounds.Height / 2.0;
        Point displayPoint = ImageToDisplayPoint(state.CenterImagePoint);
        _panX = cx - (displayPoint.X * _zoomFactor);
        _panY = cy - (displayPoint.Y * _zoomFactor);
        _panTransform.X = _panX;
        _panTransform.Y = _panY;
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        UpdateOverlay();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
    }

    private void ApplyZoomTransform()
    {
        _zoomTransform.ScaleX = 1;
        _zoomTransform.ScaleY = 1;
        ApplyDisplayImageSize();

        // Switch interpolation: nearest-neighbor for zoomed-in pixel inspection,
        // high quality for zoomed-out overview
        RenderOptions.SetBitmapInterpolationMode(DicomImage,
            _zoomFactor > 2.0 ? BitmapInterpolationMode.LowQuality : BitmapInterpolationMode.HighQuality);
    }

    private double GetFitToWindowZoomFactor()
    {
        if (_imageWidth == 0 || _imageHeight == 0)
        {
            return 0;
        }

        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return 0;
        }

        double displayWidth = GetDisplayWidth();
        double displayHeight = GetDisplayHeight();
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return 0;
        }

        double scaleX = canvasWidth / displayWidth;
        double scaleY = canvasHeight / displayHeight;
        return Math.Min(scaleX, scaleY);
    }

    private void CenterImage()
    {
        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        double displayWidth = GetDisplayWidth() * _zoomFactor;
        double displayHeight = GetDisplayHeight() * _zoomFactor;

        _panX = (canvasWidth - displayWidth) / 2.0;
        _panY = (canvasHeight - displayHeight) / 2.0;
        _panTransform.X = _panX;
        _panTransform.Y = _panY;
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
    }

    /// <summary>
    /// Resets window center/width to the DICOM default or auto-computed values.
    /// </summary>
    public void ResetWindowLevel()
    {
        if (IsDvrMode)
        {
            ResetDvrTransferWindow();
            RenderDvrViewFast();
            ScheduleDvrSharpRender();
            UpdateOverlay();
            WindowChanged?.Invoke();
            NotifyViewStateChanged();
            return;
        }

        _windowCenter = _defaultWindowCenter;
        _windowWidth = _defaultWindowWidth;
        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public void SetWindowLevel(double center, double width)
    {
        _windowCenter = center;
        _windowWidth = Math.Max(1, width);
        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
        NotifyViewStateChanged();
    }

    public void Set3DCursorOverlay(Point? imagePoint)
    {
        _cursor3DImagePoint = imagePoint;
        Update3DCursorOverlay();
    }

    public void SetReferenceLineOverlay(Point? startImagePoint, Point? endImagePoint)
    {
        _referenceLineStartImagePoint = startImagePoint;
        _referenceLineEndImagePoint = endImagePoint;
        UpdateReferenceLineOverlay();
    }

    private void NotifyViewStateChanged() => ViewStateChanged?.Invoke();

    // ==============================================================================================
    // Overlay (ported from TdView.OverlayData — simplified 4-corner text)
    // ==============================================================================================

    private void UpdateOverlay()
    {
        bool hasImage = _rawPixelData != null || _volumeSlicePixels != null;
        if (!hasImage)
        {
            OverlayTopLeft.Text = "";
            OverlayTopRight.Text = "";
            OverlayTopCenter.Text = "";
            OverlayCenterLeft.Text = "";
            OverlayCenterRight.Text = "";
            OverlayBottomCenter.Text = "";
            OverlayBottomLeft.Text = "";
            OverlayBottomRight.Text = "";
            ApplyOverlayVisibility();
            return;
        }

        OverlayTopLeft.Text = string.IsNullOrEmpty(_patientName)
            ? ""
            : $"{_patientName}\n{_patientId}";

        OverlayTopRight.Text = string.Join("\n",
            new[] { _institution, FormatStudyDate(_studyDate), _studyDescription }
                .Where(s => !string.IsNullOrEmpty(s)));
        OrientationBadgeText.Text = _volume is null
            ? string.Empty
            : GetOrientationLabel(_volumeOrientation);
        ProjectionBadgeText.Text = _volume is null
            ? string.Empty
            : $"{GetProjectionModeLabel(_projectionMode)}  {_projectionThicknessMm:F1} mm";
        CameraViewBadgeText.Text = _volume is null || !IsDvrMode
            ? string.Empty
            : GetDvrCameraViewBadgeLabel();

        if (IsDvrMode)
        {
            OverlayCenterRight.Text = string.Empty;
            OverlayCenterLeft.Text = string.Empty;
            OverlayTopCenter.Text = string.Empty;
            OverlayBottomCenter.Text = string.Empty;
        }
        else
        {
            OverlayCenterRight.Text = GetHorizontalOrientationLabel(isRightEdge: true);
            OverlayCenterLeft.Text = GetHorizontalOrientationLabel(isRightEdge: false);
            OverlayTopCenter.Text = GetVerticalOrientationLabel(isBottomEdge: false);
            OverlayBottomCenter.Text = string.Empty;
        }

        OverlayBottomLeft.Text = IsDvrMode
            ? $"TF W: {DvrTransferWidth:F0}  TF C: {DvrTransferCenter:F0}"
            : $"W: {_windowWidth:F0}  C: {_windowCenter:F0}";

        string zoomPct = $"Zoom: {_zoomFactor * 100:F0}%";
        string dims = $"{_imageWidth}×{_imageHeight}  {_bitsStored}-bit";
        string info = string.IsNullOrEmpty(_modality) ? dims : $"{dims}  {_modality}";

        if (_volume is not null)
        {
            int sliceCount = GetCurrentSliceCount();
            string orientLabel = _volumeOrientation switch
            {
                SliceOrientation.Axial => "Axial",
                SliceOrientation.Coronal => "Coronal",
                SliceOrientation.Sagittal => "Sagittal",
                _ => ""
            };
            info += $"\n{orientLabel}{(HasTiltedPlane ? " oblique" : string.Empty)} {_volumeSliceIndex + 1}/{sliceCount}  [Volume]";
        }
        else if (_frameCount > 1)
        {
            info += $"  [{_frameCount} frames]";
        }

        OverlayBottomRight.Text = $"{zoomPct}\n{info}";
        ApplyOverlayVisibility();
    }

    private void ApplyOverlayVisibility()
    {
        bool visible = _showOverlay;
        OverlayTopLeft.IsVisible = visible;
        OverlayTopRight.IsVisible = visible;
        OrientationBadge.IsVisible = visible && _volume is not null;
        ProjectionBadge.IsVisible = visible && _volume is not null;
        CameraViewBadge.IsVisible = visible && _volume is not null && IsDvrMode;
        OverlayTopCenter.IsVisible = visible && !string.IsNullOrEmpty(OverlayTopCenter.Text);
        OverlayCenterLeft.IsVisible = visible && !string.IsNullOrEmpty(OverlayCenterLeft.Text);
        OverlayCenterRight.IsVisible = visible && !string.IsNullOrEmpty(OverlayCenterRight.Text);
        OverlayBottomCenter.IsVisible = false;
        OverlayBottomLeft.IsVisible = visible;
        OverlayBottomRight.IsVisible = visible;
        ToolboxButton.IsVisible = _showToolboxButton && IsImageLoaded;
    }

    private void OnToolboxButtonClick(object? sender, RoutedEventArgs e)
    {
        ToolboxRequested?.Invoke();
        e.Handled = true;
    }

    private string GetHorizontalOrientationLabel(bool isRightEdge)
    {
        if (!TryGetScreenAxisDirections(out SpatialVector3D rightDirection, out _))
        {
            return string.Empty;
        }

        SpatialVector3D direction = isRightEdge ? rightDirection : rightDirection * -1;
        return FormatPatientOrientation(direction);
    }

    private string GetVerticalOrientationLabel(bool isBottomEdge)
    {
        if (!TryGetScreenAxisDirections(out _, out SpatialVector3D downDirection))
        {
            return string.Empty;
        }

        SpatialVector3D direction = isBottomEdge ? downDirection : downDirection * -1;
        return FormatPatientOrientation(direction);
    }

    private bool TryGetScreenAxisDirections(out SpatialVector3D rightDirection, out SpatialVector3D downDirection)
    {
        rightDirection = default;
        downDirection = default;
        if (SpatialMetadata is null)
        {
            return false;
        }

        rightDirection = SpatialMetadata.RowDirection;
        downDirection = SpatialMetadata.ColumnDirection;

        if (IsEffectivelyHorizontallyFlipped())
        {
            rightDirection *= -1;
        }

        if (IsEffectivelyVerticallyFlipped())
        {
            downDirection *= -1;
        }

        switch (NormalizeQuarterTurns(_viewRotationQuarterTurns))
        {
            case 1:
            {
                SpatialVector3D previousRight = rightDirection;
                rightDirection = downDirection * -1;
                downDirection = previousRight;
                break;
            }
            case 2:
                rightDirection *= -1;
                downDirection *= -1;
                break;
            case 3:
            {
                SpatialVector3D previousRight = rightDirection;
                rightDirection = downDirection;
                downDirection = previousRight * -1;
                break;
            }
        }

        return true;
    }

    private static string FormatPatientOrientation(SpatialVector3D direction)
    {
        const double minimumComponent = 0.2;

        var components = new (double Value, string Positive, string Negative)[]
        {
            (direction.X, "L", "R"),
            (direction.Y, "P", "A"),
            (direction.Z, "H", "F"),
        };

        var ordered = components
            .OrderByDescending(component => Math.Abs(component.Value))
            .ToArray();

        string label = string.Concat(
            ordered
                .Where(component => Math.Abs(component.Value) >= minimumComponent)
                .Select(component => component.Value >= 0 ? component.Positive : component.Negative));

        return label.Length > 0 ? label : string.Empty;
    }

    private sealed record BitmapOverlayPlane(
        ushort Group,
        int Width,
        int Height,
        int OriginX,
        int OriginY,
        bool[] Pixels);

    public bool TryGetImagePoint(Point controlPoint, out Point imagePoint)
    {
        imagePoint = default;

        if ((_rawPixelData == null && _volumeSlicePixels == null) || _zoomFactor <= 0)
        {
            return false;
        }

        imagePoint = DisplayToImagePoint(new Point(
            (controlPoint.X - _panX) / _zoomFactor,
            (controlPoint.Y - _panY) / _zoomFactor));
        return imagePoint.X >= 0 && imagePoint.Y >= 0 && imagePoint.X < _imageWidth && imagePoint.Y < _imageHeight;
    }

    private void Update3DCursorOverlay()
    {
        if (_cursor3DImagePoint is null || (_rawPixelData == null && _volumeSlicePixels == null) || _zoomFactor <= 0)
        {
            Cursor3DHorizontal.IsVisible = false;
            Cursor3DVertical.IsVisible = false;
            Cursor3DMarker.IsVisible = false;
            UpdateReferenceLineOverlay();
            return;
        }

        Point displayPoint = ImageToDisplayPoint(_cursor3DImagePoint.Value);
        double displayX = _panX + (displayPoint.X * _zoomFactor);
        double displayY = _panY + (displayPoint.Y * _zoomFactor);

        Cursor3DHorizontal.StartPoint = new Point(0, displayY);
        Cursor3DHorizontal.EndPoint = new Point(RootGrid.Bounds.Width, displayY);
        Cursor3DVertical.StartPoint = new Point(displayX, 0);
        Cursor3DVertical.EndPoint = new Point(displayX, RootGrid.Bounds.Height);

        Canvas.SetLeft(Cursor3DMarker, displayX - (Cursor3DMarker.Width / 2));
        Canvas.SetTop(Cursor3DMarker, displayY - (Cursor3DMarker.Height / 2));

        bool isVisible = displayX >= -12 && displayX <= RootGrid.Bounds.Width + 12 &&
            displayY >= -12 && displayY <= RootGrid.Bounds.Height + 12;
        Cursor3DHorizontal.IsVisible = isVisible;
        Cursor3DVertical.IsVisible = isVisible;
        Cursor3DMarker.IsVisible = isVisible;
        UpdateReferenceLineOverlay();
    }

    private void UpdateReferenceLineOverlay()
    {
        if (_referenceLineStartImagePoint is null ||
            _referenceLineEndImagePoint is null ||
            (_rawPixelData == null && _volumeSlicePixels == null) ||
            _zoomFactor <= 0)
        {
            ReferenceLinePrimary.IsVisible = false;
            return;
        }

        Point start = _referenceLineStartImagePoint.Value;
        Point end = _referenceLineEndImagePoint.Value;
        Point startDisplay = ImageToDisplayPoint(start);
        Point endDisplay = ImageToDisplayPoint(end);
        double startX = _panX + (startDisplay.X * _zoomFactor);
        double startY = _panY + (startDisplay.Y * _zoomFactor);
        double endX = _panX + (endDisplay.X * _zoomFactor);
        double endY = _panY + (endDisplay.Y * _zoomFactor);

        ReferenceLinePrimary.StartPoint = new Point(startX, startY);
        ReferenceLinePrimary.EndPoint = new Point(endX, endY);
        ReferenceLinePrimary.IsVisible = true;
    }

    private static string FormatStudyDate(string dcmDate)
    {
        if (string.IsNullOrEmpty(dcmDate) || dcmDate.Length < 8) return dcmDate;
        return $"{dcmDate[..4]}-{dcmDate[4..6]}-{dcmDate[6..8]}";
    }

    // ==============================================================================================
    // Pointer Handlers — Avalonia unified pointer events
    // ==============================================================================================

    /// <summary>
    /// Determines if the pointer is in the edge (zoom) region.
    /// Ported from TdView.IsCursorInZoomRegion: outer 1/6th of each dimension.
    /// </summary>
    private bool IsCursorInZoomRegion(Point pos)
    {
        double frameW = RootGrid.Bounds.Width / 6.0;
        double frameH = RootGrid.Bounds.Height / 6.0;

        return pos.X < frameW
            || pos.X > RootGrid.Bounds.Width - frameW
            || pos.Y < frameH
            || pos.Y > RootGrid.Bounds.Height - frameH;
    }

    private bool IsScrollActionMode() => ActionMode == ActionToolbarMode.ScrollStack;

    private bool IsWindowActionMode() => ActionMode == ActionToolbarMode.Window;

    private void BeginWindowLevelDrag(Point pos, IPointer pointer)
    {
        _isRightDragging = true;
        _mouseDownPos = pos;
        if (IsDvrMode)
        {
            BeginDvrTransferDrag();
        }
        else
        {
            _startWindowCenter = _windowCenter;
            _startWindowWidth = _windowWidth;
        }

        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        AttachCapturedPointerHandlers();
        Cursor = CreateWindowCursor();
    }

    private void AttachCapturedPointerHandlers()
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ReferenceEquals(_capturedTopLevel, topLevel))
        {
            return;
        }

        DetachCapturedPointerHandlers();
        _capturedTopLevel = topLevel;

        // Subscribe during the Tunnel phase with handledEventsToo so the
        // handler fires even when a sibling panel (or the captured element
        // itself) has already set e.Handled = true.  This is the reliable
        // fallback that keeps stack-drag, pan, and edge-zoom working when
        // the pointer leaves the panel boundary in a multi-viewport grid.
        _capturedTopLevel.AddHandler(
            InputElement.PointerMovedEvent,
            OnCapturedTopLevelPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _capturedTopLevel.AddHandler(
            InputElement.PointerReleasedEvent,
            OnCapturedTopLevelPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void DetachCapturedPointerHandlers()
    {
        if (_capturedTopLevel is null)
        {
            return;
        }

        _capturedTopLevel.RemoveHandler(InputElement.PointerMovedEvent, OnCapturedTopLevelPointerMoved);
        _capturedTopLevel.RemoveHandler(InputElement.PointerReleasedEvent, OnCapturedTopLevelPointerReleased);
        _capturedTopLevel = null;
    }

    private void OnCapturedTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_capturedPointer is null || (!_isLeftDragging && !_isRightDragging))
        {
            return;
        }

        // When the pointer is inside our bounds the normal RootGrid.PointerMoved
        // handler covers it — skip here to avoid double-processing.
        Point pos = e.GetPosition(RootGrid);
        Rect bounds = new(RootGrid.Bounds.Size);
        if (bounds.Contains(pos))
        {
            return;
        }

        // Pointer is outside the panel (e.g. fast-stack drag beyond the edge in
        // a multi-viewport grid).  This Tunnel + handledEventsToo subscription
        // is the only reliable way to keep receiving moves when a sibling panel
        // or the platform capture swallows the event.
        OnPointerMoved(RootGrid, e);
    }

    private void OnCapturedTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_capturedPointer is null || (!_isLeftDragging && !_isRightDragging))
        {
            return;
        }

        Point pos = e.GetPosition(RootGrid);
        Rect bounds = new(RootGrid.Bounds.Size);
        if (bounds.Contains(pos))
        {
            return;
        }

        OnPointerReleased(RootGrid, e);
    }

    private void OnProjectionBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(ProjectionBadge);
        if (point.Properties.IsRightButtonPressed)
        {
            ProjectionBadge.ContextMenu?.Open(ProjectionBadge);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isProjectionThicknessDragging = true;
        _projectionDragStartY = e.GetPosition(RootGrid).Y;
        _projectionDragStartThicknessMm = _projectionThicknessMm;
        _projectionPointer = e.Pointer;
        e.Pointer.Capture(ProjectionBadge);
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        e.Handled = true;
    }

    private void OnOrientationBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(OrientationBadge);
        if (point.Properties.IsRightButtonPressed)
        {
            OrientationBadge.ContextMenu?.Open(OrientationBadge);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        SliceOrientation nextOrientation = _volumeOrientation switch
        {
            SliceOrientation.Axial => SliceOrientation.Coronal,
            SliceOrientation.Coronal => SliceOrientation.Sagittal,
            SliceOrientation.Sagittal => SliceOrientation.Axial,
            _ => SliceOrientation.Axial,
        };

        SetVolumeOrientation(nextOrientation);
        e.Handled = true;
    }

    private void OnProjectionBadgePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isProjectionThicknessDragging || _volume is null)
        {
            return;
        }

        double currentY = e.GetPosition(RootGrid).Y;
        double deltaY = _projectionDragStartY - currentY;
        double baseThickness = Math.Max(_projectionDragStartThicknessMm, GetMinimumProjectionThicknessMm());
        double sensitivity = Math.Clamp(baseThickness / 70.0, 0.04, 1.25);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            sensitivity *= 0.35;
        }

        SetProjectionThicknessMm(_projectionDragStartThicknessMm + (deltaY * sensitivity));
        e.Handled = true;
    }

    private void OnProjectionBadgePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isProjectionThicknessDragging)
        {
            return;
        }

        EndProjectionThicknessDrag();
        e.Handled = true;
    }

    private void OnProjectionBadgePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndProjectionThicknessDrag();
    }

    private void EndProjectionThicknessDrag()
    {
        _isProjectionThicknessDragging = false;
        _projectionPointer?.Capture(null);
        _projectionPointer = null;
        UpdateZoneCursor(_mouseDownPos);
    }

    private static Cursor CreateWindowCursor()
    {
        if (s_windowCursor is not null)
        {
            return s_windowCursor;
        }

        lock (s_windowCursorLock)
        {
            if (s_windowCursor is not null)
            {
                return s_windowCursor;
            }

            try
            {
                Uri uri = new("avares://KPACS.Viewer/Assets/Cursors/window-cursor.png");
                using Stream stream = AssetLoader.Open(uri);
                var bitmap = new Bitmap(stream);
                s_windowCursor = new Cursor(bitmap, new PixelPoint(16, 16));
            }
            catch
            {
                s_windowCursor = new Cursor(StandardCursorType.Cross);
            }

            return s_windowCursor;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        var pos = e.GetPosition(RootGrid);

        _lastPointerPos = pos;

        if (HandleMeasurementPointerPressed(point, pos, e))
        {
            return;
        }

        if (point.Properties.IsLeftButtonPressed &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            TryGetImagePoint(pos, out Point shiftImagePoint))
        {
            ImagePointPressed?.Invoke(new DicomImagePointerInfo(shiftImagePoint, e.KeyModifiers));
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && BeginPlaneTiltDrag(pos, e.Pointer))
        {
            e.Handled = true;
            return;
        }

        // Double-click left: let the host window decide how to handle layout focus.
        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            ImageDoubleClicked?.Invoke();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && TryGetImagePoint(pos, out Point imagePoint))
        {
            ImagePointPressed?.Invoke(new DicomImagePointerInfo(imagePoint, e.KeyModifiers));
        }

        if (point.Properties.IsRightButtonPressed)
        {
            BeginWindowLevelDrag(pos, e.Pointer);
            e.Handled = true;
        }
        else if (point.Properties.IsMiddleButtonPressed)
        {
            _isLeftDragging = true;
            _isStackDragging = true;
            _mouseDownPos = pos;
            BeginStackDragTimer(e);
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            e.Pointer.Capture(RootGrid);
            _capturedPointer = e.Pointer;
            AttachCapturedPointerHandlers();
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _isLeftDragging = true;
            _mouseDownPos = pos;

            if (IsScrollActionMode())
            {
                // ScrollStack tool mode: left-drag behaves like middle-drag
                // (fast stack) so users without a middle button can still
                // drag-scroll through slices.
                _isStackDragging = true;
                BeginStackDragTimer(e);
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            }
            else
            {
                // Lock the zone at click time (ported from FZoomRegion := IsCursorInZoomRegion)
                _isEdgeZoom = IsCursorInZoomRegion(_mouseDownPos);

                if (_isEdgeZoom)
                {
                    _lastMouseY = (int)_mouseDownPos.Y;
                    Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                }
                else
                {
                    _startPanX = _panX;
                    _startPanY = _panY;
                    Cursor = new Cursor(StandardCursorType.Hand);
                }
            }

            e.Pointer.Capture(RootGrid);
            _capturedPointer = e.Pointer;
            AttachCapturedPointerHandlers();
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (HandleMeasurementPointerReleased(e))
        {
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Right && _isRightDragging)
        {
            _isRightDragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            DetachCapturedPointerHandlers();
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
        else if ((e.InitialPressMouseButton == MouseButton.Left || e.InitialPressMouseButton == MouseButton.Middle) && _isLeftDragging)
        {
            EndPlaneTiltDrag();
            HandleDvrPointerReleased();  // no-op if not orbiting
            StopStackDragTimer();
            _isLeftDragging = false;
            _isEdgeZoom = false;
            _isStackDragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            DetachCapturedPointerHandlers();
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
    }

    private void OnRootGridPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isLeftDragging && !_isRightDragging && !_isStackDragging)
        {
            return;
        }

        if (_isStackDragging)
        {
            return;
        }

        EndPlaneTiltDrag();
        HandleDvrPointerReleased();
        StopStackDragTimer();
        _isLeftDragging = false;
        _isRightDragging = false;
        _isEdgeZoom = false;
        _isStackDragging = false;
        _capturedPointer = null;
        DetachCapturedPointerHandlers();
        UpdateZoneCursor(_lastPointerPos);
    }

    // ==============================================================================================
    // Pointer Move — Dispatch to windowing, edge-zoom, or center-pan
    // ==============================================================================================

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point pos = e.GetPosition(RootGrid);
        _lastPointerPos = pos;

        if (HandleMeasurementPointerMoved(pos, e))
        {
            HoveredImagePointChanged?.Invoke(TryGetImagePoint(pos, out Point imagePoint)
                ? new DicomHoverInfo(imagePoint, e.KeyModifiers)
                : null);
            return;
        }

        if (_isLeftDragging && UpdatePlaneTiltDrag(pos, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (_isRightDragging)
        {
            if (IsDvrMode && UpdateDvrTransferDrag(pos))
            {
                e.Handled = true;
                return;
            }

            // Window/Level: horizontal = width, vertical = center
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            double sensitivity = Math.Max(1.0, _defaultWindowWidth / 500.0);

            _windowWidth = Math.Max(1, _startWindowWidth + dx * sensitivity);
            _windowCenter = _startWindowCenter + dy * sensitivity;

            RenderImageFastThenSharp();
            UpdateOverlay();
            WindowChanged?.Invoke();
            NotifyViewStateChanged();
        }
        else if (_isLeftDragging && _isStackDragging)
        {
            // Stack drag is driven by a DispatcherTimer polling screen cursor
            // position (the Delphi Mouse.CursorPos pattern), so it works even
            // when the pointer leaves the panel.  Nothing to do here.
            e.Handled = true;
        }
        else if (_isLeftDragging && _isEdgeZoom)
        {
            // Edge zone: drag up = zoom in, drag down = zoom out
            int currentY = (int)pos.Y;
            if (currentY != _lastMouseY)
            {
                double zoomPct = _zoomFactor * 100.0;
                double step = Math.Max(1.0, zoomPct / 100.0 + 1.0);
                double tempZoomPct;

                if (currentY < _lastMouseY)
                    tempZoomPct = 100.0 + step;  // drag up = zoom in
                else
                    tempZoomPct = 100.0 - step;  // drag down = zoom out

                tempZoomPct = Math.Clamp(tempZoomPct, 5.0, 1000.0);

                double factor = tempZoomPct / 100.0;
                double newZoom = Math.Clamp(_zoomFactor * factor, 0.01, 20.0);

                double cx = RootGrid.Bounds.Width / 2.0;
                double cy = RootGrid.Bounds.Height / 2.0;
                double ratio = newZoom / _zoomFactor;
                _panX = cx - ratio * (cx - _panX);
                _panY = cy - ratio * (cy - _panY);
                _panTransform.X = _panX;
                _panTransform.Y = _panY;

                _zoomFactor = newZoom;
                _fitToWindow = false;
                ApplyZoomTransform();
                RenderImageFastThenSharp();
                Update3DCursorOverlay();
                UpdateMeasurementPresentation();
                UpdateOverlay();
                ZoomChanged?.Invoke();
                NotifyViewStateChanged();

                _lastMouseY = currentY;
            }
        }
        else if (_isLeftDragging && !_isEdgeZoom)
        {
            // Center zone: pan
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            _panX = _startPanX + dx;
            _panY = _startPanY + dy;
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
            _fitToWindow = false;
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            NotifyViewStateChanged();
        }
        else if (!_isRightDragging && !_isLeftDragging && IsImageLoaded)
        {
            UpdateZoneCursor(pos);
            HoveredImagePointChanged?.Invoke(TryGetImagePoint(pos, out Point imagePoint)
                ? new DicomHoverInfo(imagePoint, e.KeyModifiers)
                : null);
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        HandleMeasurementPointerExited();
        _lastPointerPos = default;

        if (!_isRightDragging && !_isLeftDragging)
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            HoveredImagePointChanged?.Invoke(null);
        }
    }

    /// <summary>
    /// Updates the cursor to reflect whether the pointer is in the edge (zoom) or center (pan) zone.
    /// </summary>
    private void UpdateZoneCursor(Point pos)
    {
        UpdateInteractiveCursor(pos);
    }

    // ==============================================================================================
    // Pointer Wheel — Zoom at cursor position
    // ==============================================================================================

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_measurementTool == MeasurementTool.BallRoiCorrection && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (TryAdjustBallRoiRadius(e.Delta.Y > 0 ? 1 : -1, out _))
            {
                e.Handled = true;
            }

            return;
        }

        if (WheelMode == MouseWheelMode.StackScroll)
        {
            StackScrollRequested?.Invoke(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        Point mousePos = e.GetPosition(RootGrid);

        double oldZoom = _zoomFactor;
        double zoomDelta = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        double newZoom = Math.Clamp(_zoomFactor * zoomDelta, 0.01, 20.0);

        // Zoom centered on mouse position
        double ratio = newZoom / oldZoom;
        _panX = mousePos.X - ratio * (mousePos.X - _panX);
        _panY = mousePos.Y - ratio * (mousePos.Y - _panY);
        _panTransform.X = _panX;
        _panTransform.Y = _panY;

        _zoomFactor = newZoom;
        _fitToWindow = false;
        ApplyZoomTransform();
        RenderImageFastThenSharp();
        Update3DCursorOverlay();
        UpdateMeasurementPresentation();

        UpdateOverlay();
        ZoomChanged?.Invoke();
        NotifyViewStateChanged();
        e.Handled = true;
    }

    private int GetStackDragStepDivisor()
    {
        if (!StackSkipImages || StackItemCount <= 1)
        {
            return 1;
        }

        int maxIndex = Math.Max(1, StackItemCount - 1);
        return Math.Max(2, (int)Math.Ceiling(200.0 / maxIndex));
    }

    // ==============================================================================================
    // Stack-drag via screen-cursor polling (Delphi Mouse.CursorPos pattern)
    // ==============================================================================================
    //
    // Avalonia's pointer capture does not reliably deliver PointerMoved events
    // when the pointer crosses into a sibling panel in a multi-viewport grid.
    // The Delphi implementation sidesteps this by reading Mouse.CursorPos
    // (global screen coordinates) in every MouseMove.  We replicate that by
    // polling the cursor position on a short-interval DispatcherTimer.

    /// <summary>
    /// Starts the stack-drag polling timer.  Records the initial screen-space Y
    /// so that subsequent ticks can compute the delta.
    /// </summary>
    private void BeginStackDragTimer(PointerEventArgs e)
    {
        // Record initial Y in screen coordinates.
        PixelPoint? screenPos = GetScreenPosition(e);
        _lastStackScreenY = screenPos?.Y ?? 0;

        if (_stackDragTimer is null)
        {
            _stackDragTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(8),  // ~120 Hz polling
            };
            _stackDragTimer.Tick += OnStackDragTimerTick;
        }

        _stackDragTimer.Start();
    }

    /// <summary>Stops and resets the stack-drag polling timer.</summary>
    private void StopStackDragTimer()
    {
        _stackDragTimer?.Stop();
    }

    /// <summary>
    /// Polls the current screen cursor position and fires StackScrollRequested
    /// based on the Y delta since the last tick — works regardless of which
    /// Avalonia control the pointer is currently over.
    /// </summary>
    private void OnStackDragTimerTick(object? sender, EventArgs e)
    {
        if (!_isStackDragging)
        {
            StopStackDragTimer();
            return;
        }

        // Get current screen cursor position.
        PixelPoint? screenPos = GetCurrentScreenCursorPosition();
        if (screenPos is null)
        {
            return;
        }

        int currentY = screenPos.Value.Y;
        int deltaY = currentY - _lastStackScreenY;

        if (Math.Abs(deltaY) > 1)
        {
            int stepDivisor = GetStackDragStepDivisor();
            int stackDelta = Math.Max(1, Math.Abs(deltaY) / stepDivisor);
            if (deltaY < 0)
            {
                stackDelta = -stackDelta;
            }

            StackScrollRequested?.Invoke(stackDelta);
            _lastStackScreenY = currentY;
        }
    }

    /// <summary>
    /// Gets the screen position from a pointer event via the TopLevel.
    /// </summary>
    private PixelPoint? GetScreenPosition(PointerEventArgs e)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        Point posInTopLevel = e.GetPosition(topLevel);
        PixelPoint screenPos = topLevel.PointToScreen(posInTopLevel);
        return screenPos;
    }

    /// <summary>
    /// Gets the current screen cursor position by reading the last known pointer
    /// position relative to our TopLevel and converting to screen coordinates.
    /// Falls back to the platform cursor position via Win32 on Windows.
    /// </summary>
    private PixelPoint? GetCurrentScreenCursorPosition()
    {
        // Use Win32 GetCursorPos on Windows for reliable global cursor tracking,
        // exactly matching the Delphi Mouse.CursorPos pattern.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (GetCursorPos(out POINT pt))
            {
                return new PixelPoint(pt.X, pt.Y);
            }
        }

        // Fallback for non-Windows: convert last known pointer position via TopLevel.
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            PixelPoint screenPos = topLevel.PointToScreen(_lastPointerPos);
            return screenPos;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void UpdateDisplayGeometry(double pixelSpacingX, double pixelSpacingY)
    {
        pixelSpacingX = pixelSpacingX > 0 ? pixelSpacingX : 1.0;
        pixelSpacingY = pixelSpacingY > 0 ? pixelSpacingY : 1.0;

        double spacingBase = Math.Min(pixelSpacingX, pixelSpacingY);
        if (spacingBase <= 0)
        {
            spacingBase = 1.0;
        }

        _displayScaleX = pixelSpacingX / spacingBase;
        _displayScaleY = pixelSpacingY / spacingBase;
    }

    private void ApplyDisplayImageSize()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        DicomImage.Width = GetDisplayWidth() * _zoomFactor;
        DicomImage.Height = GetDisplayHeight() * _zoomFactor;
    }

    private int GetRenderPixelWidth()
    {
        return (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0
            ? GetSourceRenderPixelWidth()
            : GetSourceRenderPixelHeight();
    }

    private int GetSourceRenderPixelWidth()
    {
        if (_imageWidth <= 0)
        {
            return 0;
        }

        if (!_isSharpRender)
        {
            // Fast mode: render at native resolution, let Avalonia upscale the bitmap
            return _imageWidth;
        }

        // Sharp mode: render at display resolution for subpixel-accurate interpolation.
        // Cap at zoomFactor ≤ 2.0 — beyond that, nearest-neighbor is fine for pixel inspection.
        double scale = _displayScaleX * Math.Min(_zoomFactor, 2.0);
        return Math.Max(1, (int)Math.Ceiling(_imageWidth * scale));
    }

    private int GetRenderPixelHeight()
    {
        return (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0
            ? GetSourceRenderPixelHeight()
            : GetSourceRenderPixelWidth();
    }

    private int GetSourceRenderPixelHeight()
    {
        if (_imageHeight <= 0)
        {
            return 0;
        }

        if (!_isSharpRender)
        {
            return _imageHeight;
        }

        double scale = _displayScaleY * Math.Min(_zoomFactor, 2.0);
        return Math.Max(1, (int)Math.Ceiling(_imageHeight * scale));
    }

    private double GetDisplayWidth() => GetViewPixelWidth() * GetViewDisplayScaleX();

    private double GetDisplayHeight() => GetViewPixelHeight() * GetViewDisplayScaleY();

    private int GetViewPixelWidth() => (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0 ? _imageWidth : _imageHeight;

    private int GetViewPixelHeight() => (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0 ? _imageHeight : _imageWidth;

    private double GetViewDisplayScaleX() => (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0 ? _displayScaleX : _displayScaleY;

    private double GetViewDisplayScaleY() => (NormalizeQuarterTurns(_viewRotationQuarterTurns) & 1) == 0 ? _displayScaleY : _displayScaleX;

    private Point ImageToDisplayPoint(Point imagePoint)
    {
        Point viewPoint = TransformImagePointToView(imagePoint);
        return new Point(viewPoint.X * GetViewDisplayScaleX(), viewPoint.Y * GetViewDisplayScaleY());
    }

    private Point DisplayToImagePoint(Point displayPoint)
    {
        double viewX = GetViewDisplayScaleX() <= 0 ? displayPoint.X : displayPoint.X / GetViewDisplayScaleX();
        double viewY = GetViewDisplayScaleY() <= 0 ? displayPoint.Y : displayPoint.Y / GetViewDisplayScaleY();
        return TransformViewPointToImage(new Point(viewX, viewY));
    }

    private Point TransformImagePointToView(Point imagePoint)
    {
        double x = imagePoint.X;
        double y = imagePoint.Y;
        int width = _imageWidth;
        int height = _imageHeight;

        Point rotated = NormalizeQuarterTurns(_viewRotationQuarterTurns) switch
        {
            1 => new Point((height - 1) - y, x),
            2 => new Point((width - 1) - x, (height - 1) - y),
            3 => new Point(y, (width - 1) - x),
            _ => new Point(x, y),
        };

        double viewX = IsEffectivelyHorizontallyFlipped() ? (GetViewPixelWidth() - 1) - rotated.X : rotated.X;
        double viewY = IsEffectivelyVerticallyFlipped() ? (GetViewPixelHeight() - 1) - rotated.Y : rotated.Y;
        return new Point(viewX, viewY);
    }

    private Point TransformViewPointToImage(Point viewPoint)
    {
        double viewX = IsEffectivelyHorizontallyFlipped() ? (GetViewPixelWidth() - 1) - viewPoint.X : viewPoint.X;
        double viewY = IsEffectivelyVerticallyFlipped() ? (GetViewPixelHeight() - 1) - viewPoint.Y : viewPoint.Y;
        int width = _imageWidth;
        int height = _imageHeight;

        return NormalizeQuarterTurns(_viewRotationQuarterTurns) switch
        {
            1 => new Point(viewY, (height - 1) - viewX),
            2 => new Point((width - 1) - viewX, (height - 1) - viewY),
            3 => new Point((width - 1) - viewY, viewX),
            _ => new Point(viewX, viewY),
        };
    }

    private bool HasViewTransform() => NormalizeQuarterTurns(_viewRotationQuarterTurns) != 0 || IsEffectivelyHorizontallyFlipped() || IsEffectivelyVerticallyFlipped();

    private bool IsEffectivelyHorizontallyFlipped() => _viewFlipHorizontal ^ IsDvrMode;

    private bool IsEffectivelyVerticallyFlipped() => _viewFlipVertical;

    private void TransformBgraBuffer(byte[] sourceBgra, int sourceWidth, int sourceHeight, byte[] destinationBgra)
    {
        int rotation = NormalizeQuarterTurns(_viewRotationQuarterTurns);
        int destinationWidth = (rotation & 1) == 0 ? sourceWidth : sourceHeight;
        int destinationHeight = (rotation & 1) == 0 ? sourceHeight : sourceWidth;
        Span<uint> source = MemoryMarshal.Cast<byte, uint>(sourceBgra.AsSpan(0, sourceWidth * sourceHeight * 4));
        Span<uint> destination = MemoryMarshal.Cast<byte, uint>(destinationBgra.AsSpan(0, destinationWidth * destinationHeight * 4));

        for (int y = 0; y < sourceHeight; y++)
        {
            int sourceOffset = y * sourceWidth;
            for (int x = 0; x < sourceWidth; x++)
            {
                int rotatedX;
                int rotatedY;
                switch (rotation)
                {
                    case 1:
                        rotatedX = sourceHeight - 1 - y;
                        rotatedY = x;
                        break;
                    case 2:
                        rotatedX = sourceWidth - 1 - x;
                        rotatedY = sourceHeight - 1 - y;
                        break;
                    case 3:
                        rotatedX = y;
                        rotatedY = sourceWidth - 1 - x;
                        break;
                    default:
                        rotatedX = x;
                        rotatedY = y;
                        break;
                }

                if (IsEffectivelyHorizontallyFlipped())
                {
                    rotatedX = destinationWidth - 1 - rotatedX;
                }

                if (IsEffectivelyVerticallyFlipped())
                {
                    rotatedY = destinationHeight - 1 - rotatedY;
                }

                destination[(rotatedY * destinationWidth) + rotatedX] = source[sourceOffset + x];
            }
        }
    }

    private static void ResampleBgraBuffer(byte[] sourceBgra, int sourceWidth, int sourceHeight, byte[] destinationBgra, int destinationWidth, int destinationHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
        {
            Buffer.BlockCopy(sourceBgra, 0, destinationBgra, 0, Math.Min(sourceBgra.Length, destinationBgra.Length));
            return;
        }

        for (int y = 0; y < destinationHeight; y++)
        {
            double sourceY = destinationHeight == 1 ? 0.0 : y * (sourceHeight - 1.0) / (destinationHeight - 1.0);
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            double ty = sourceY - y0;

            for (int x = 0; x < destinationWidth; x++)
            {
                double sourceX = destinationWidth == 1 ? 0.0 : x * (sourceWidth - 1.0) / (destinationWidth - 1.0);
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                double tx = sourceX - x0;

                int destinationOffset = (y * destinationWidth + x) * 4;
                int topLeft = (y0 * sourceWidth + x0) * 4;
                int topRight = (y0 * sourceWidth + x1) * 4;
                int bottomLeft = (y1 * sourceWidth + x0) * 4;
                int bottomRight = (y1 * sourceWidth + x1) * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    double top = sourceBgra[topLeft + channel] * (1.0 - tx) + sourceBgra[topRight + channel] * tx;
                    double bottom = sourceBgra[bottomLeft + channel] * (1.0 - tx) + sourceBgra[bottomRight + channel] * tx;
                    destinationBgra[destinationOffset + channel] = (byte)Math.Clamp(Math.Round(top * (1.0 - ty) + bottom * ty), 0, 255);
                }
            }
        }
    }

    private static int NormalizeQuarterTurns(int quarterTurns)
    {
        int normalized = quarterTurns % 4;
        return normalized < 0 ? normalized + 4 : normalized;
    }

    private static double NormalizeAngle(double angleRadians)
    {
        double wrapped = angleRadians % (Math.PI * 2.0);
        return wrapped <= -Math.PI
            ? wrapped + (Math.PI * 2.0)
            : wrapped > Math.PI
                ? wrapped - (Math.PI * 2.0)
                : wrapped;
    }

    public bool ToggleHorizontalFlip()
    {
        if (!IsImageLoaded)
        {
            return false;
        }

        _ = TryCaptureNavigationState(out NavigationState navigationState);
        _viewFlipHorizontal = !_viewFlipHorizontal;
        OnViewportTransformChanged(navigationState);
        return true;
    }

    public bool ToggleVerticalFlip()
    {
        if (!IsImageLoaded)
        {
            return false;
        }

        _ = TryCaptureNavigationState(out NavigationState navigationState);
        _viewFlipVertical = !_viewFlipVertical;
        OnViewportTransformChanged(navigationState);
        return true;
    }

    public bool RotateClockwise90()
    {
        if (!IsImageLoaded)
        {
            return false;
        }

        _ = TryCaptureNavigationState(out NavigationState navigationState);
        _viewRotationQuarterTurns = NormalizeQuarterTurns(_viewRotationQuarterTurns + 1);
        OnViewportTransformChanged(navigationState);
        return true;
    }

    private void OnViewportTransformChanged(NavigationState navigationState)
    {
        ApplyDisplayImageSize();
        if (_fitToWindow)
        {
            ApplyFitToWindow();
            return;
        }

        ApplyAbsoluteNavigationState(navigationState with { FitToWindow = false, ZoomFactor = _zoomFactor });
        NotifyViewStateChanged();
    }

    // ==============================================================================================
    // Resize handler
    // ==============================================================================================

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool hasImage = _rawPixelData != null || _volumeSlicePixels != null;
        if (!hasImage)
        {
            Update3DCursorOverlay();
            UpdateMeasurementPresentation();
            UpdateOverlay();
            return;
        }

        if (_fitToWindow)
        {
            ApplyFitToWindow();
        }
        else if (_zoomFactor > 0 && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            Point previousCenter = new(e.PreviousSize.Width / 2.0, e.PreviousSize.Height / 2.0);
            Point centerImagePoint = DisplayToImagePoint(new Point(
                (previousCenter.X - _panX) / _zoomFactor,
                (previousCenter.Y - _panY) / _zoomFactor));

            double newCenterX = e.NewSize.Width / 2.0;
            double newCenterY = e.NewSize.Height / 2.0;
            Point displayPoint = ImageToDisplayPoint(centerImagePoint);
            _panX = newCenterX - (displayPoint.X * _zoomFactor);
            _panY = newCenterY - (displayPoint.Y * _zoomFactor);
            _panTransform.X = _panX;
            _panTransform.Y = _panY;
        }

        Update3DCursorOverlay();
        UpdateMeasurementPresentation();
        UpdateOverlay();
    }

    private enum PlaneTiltConstraintAxis
    {
        None,
        Horizontal,
        Vertical,
    }
}
