using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FellowOakDicom;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Windows;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow : Window
{
    private static readonly Lock s_openViewerSyncLock = new();
    private static readonly HashSet<StudyViewerWindow> s_openViewerWindows = [];

    private readonly ViewerStudyContext _context;
    private readonly RemoteStudyRetrievalSession? _remoteRetrievalSession;
    private readonly bool _startBlank;
    private readonly int _viewerNumber;
    private readonly List<ViewportSlot> _slots = [];
    private readonly Dictionary<string, DicomSpatialMetadata?> _spatialMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LegacyProjectionSeriesCache> _legacyProjectionSeriesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SeriesVolume?> _volumeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ToastNotificationItem> _toastNotifications = [];
    private readonly CancellationTokenSource _priorLookupCancellation = new();
    private readonly DispatcherTimer _actionToolbarHideTimer = new();
    private readonly DispatcherTimer _linkedViewSyncDebounceTimer = new();
    private readonly DispatcherTimer _volumeRoiDraftPanelRefreshTimer = new();
    private readonly DispatcherTimer _viewportLayoutResetTimer = new();
    private readonly DispatcherTimer _workspaceDockHideTimer = new();
    private readonly AnatomyKnowledgePackService _anatomyKnowledgePackService = new();
    private CancellationTokenSource? _adjacentPriorityDebounceCancellation;
    private readonly string? _viewerSettingsPath;
    private readonly string? _anatomyKnowledgePackDirectory;
    private readonly string? _defaultCraniumKnowledgePackPath;
    private string? _activeCraniumKnowledgePackPath;
    private IReadOnlyList<PriorStudySummary> _priorStudies = [];
    private readonly List<AnatomyKnowledgePack> _anatomyKnowledgePacks = [];
    private AnatomyKnowledgePack? _activeCraniumKnowledgePack;
    private PriorStudySummary? _selectedPriorStudy;
    private StudyDetails? _thumbnailStripStudy;
    private CancellationTokenSource? _priorPreviewCancellation;
    private ViewportSlot? _activeSlot;
    private SeriesDragGhostWindow? _dragGhostWindow;
    private ActionToolbarMode _actionToolbarMode = ActionToolbarMode.ScrollStack;
    private int _selectedColorScheme = (int)ColorScheme.Grayscale;
    private bool _overlayEnabled = true;
    private bool _linkedViewSyncEnabled = true;
    private TransferFunctionPreset _preferredDvrPreset = TransferFunctionPreset.SoftTissue;
    private VolumeShadingPreset _preferredDvrShadingPreset = VolumeShadingPreset.SoftTissue;
    private VolumeLightDirectionPreset _preferredDvrLightDirectionPreset = VolumeLightDirectionPreset.Headlight;
    private bool _preferredDvrAutoColorLutEnabled = true;
    private List<int> _currentLayoutSpec = [1];
    private List<string> _savedCustomLayouts = [];
    private ViewerLayoutState? _layoutBeforeFocusedView;
    private bool _isShowingCurrentStudy;
    private bool _isActionToolbarPointerOver;
    private bool _isWorkspaceDockPointerOver;
    private bool _isWorkspaceDockVisible = true;
    private bool _isPriorPreviewLoading;
    private bool _isSynchronizingLinkedViews;
    private bool _is3DCursorToolArmed;
    private bool _assignedPriorStudyLoadAttempted;
    private int _linkedSyncSuspendCount;
    private ViewportSlot? _pendingLinkedSyncSourceSlot;
    private bool _linkedSyncThrottleActive;
    private ViewportSlot? _linkedReferenceSourceSlot;
    private ViewportSlot? _openToolboxSlot;
    private SeriesVolume? _linkedReferenceSourceVolume;
    private DicomSpatialMetadata? _linkedReferenceSourceMetadata;
    private PendingLinkedSyncContext? _pendingLinkedSyncContext;
    private string _thumbnailStripMessage = string.Empty;
    private const int AdjacentPriorityDebounceMs = 180;
    private const int LinkedViewSyncThrottleMs = 16;
    private const int VolumeRoiDraftPanelRefreshDebounceMs = 50;
    private const int WorkspaceDockAutoHideMs = 1000;
    private const int ViewportLayoutResetDebounceMs = 140;
    private const int NoticeableStudyInstanceThreshold = 240;
    private const int NoticeableSeriesInstanceThreshold = 90;
    private const int MaxLayoutRows = 6;
    private const int MaxLayoutColumnsPerRow = 6;
    private const int MaxLayoutSlots = 12;
    private const double ParallelPlaneDotThreshold = 0.985;
    private const double CutlineEdgeTolerance = 1e-3;
    private bool IsRemoteOnlyDebugMode => App.RemoteOnlyDebugModeEnabled;

    public StudyViewerWindow()
        : this(CreateDefaultContext(), "StudyViewerWindow", 1)
    {
    }

    public StudyViewerWindow(ViewerStudyContext context, string placementKey, int viewerNumber)
    {
        InitializeComponent();
        InitializeToolboxIcons();
        InitializeMeasurementsUi();
        _context = context;
        _remoteRetrievalSession = context.RemoteRetrievalSession;
        _startBlank = context.StartBlank;
        _viewerNumber = viewerNumber;
        _isShowingCurrentStudy = !context.StartBlank;
        _viewportLayoutResetTimer.Interval = TimeSpan.FromMilliseconds(ViewportLayoutResetDebounceMs);
        _viewportLayoutResetTimer.Tick += OnViewportLayoutResetTimerTick;
        _linkedViewSyncDebounceTimer.Interval = TimeSpan.FromMilliseconds(LinkedViewSyncThrottleMs);
        _linkedViewSyncDebounceTimer.Tick += OnLinkedViewSyncDebounceTimerTick;
        _volumeRoiDraftPanelRefreshTimer.Interval = TimeSpan.FromMilliseconds(VolumeRoiDraftPanelRefreshDebounceMs);
        _volumeRoiDraftPanelRefreshTimer.Tick += OnVolumeRoiDraftPanelRefreshTimerTick;
        _centerlineSyncDebounceTimer.Interval = TimeSpan.FromMilliseconds(CenterlineSyncThrottleMs);
        _centerlineSyncDebounceTimer.Tick += OnCenterlineSyncDebounceTimerTick;
        InitializeVolumeRoiDraftPreviewControls();
        Title = $"K-PACS Viewer {viewerNumber}";
        if (Application.Current is App app)
        {
            _viewerSettingsPath = Path.Combine(app.Paths.ApplicationDirectory, "study-viewer-settings.json");
            _anatomyKnowledgePackDirectory = Path.Combine(app.Paths.ApplicationDirectory, "anatomy-packs");
            _defaultCraniumKnowledgePackPath = Path.Combine(_anatomyKnowledgePackDirectory, "cranium-base.sample.json");
            app.WindowPlacementService.Register(this, placementKey);
        }
        RegisterForLinkedViewSync();
        List<int> defaultLayout = BuildUniformLayoutSpec(context.LayoutRows, context.LayoutColumns);
        _currentLayoutSpec = [.. defaultLayout];
        LoadViewerSettings(defaultLayout);
        if (_remoteRetrievalSession is not null)
        {
            _remoteRetrievalSession.StudyChanged += OnRemoteStudyChanged;
        }
        InitializeWorkspaceDock();
        InitializeActionToolbar();
        ToastItemsControl.ItemsSource = _toastNotifications;
        ViewerIdentityText.Text = $"Viewer {viewerNumber}";
        StudyTitleText.Text = context.StudyDetails.Study.PatientName;
        StudySubtitleText.Text = BuildSubtitle();
        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        SizeChanged += OnWindowSizeChanged;
        Deactivated += (_, _) => Clear3DCursor();
        VolumeComputeBackend.GpuFallbackOccurred += OnGpuFallbackOccurred;
        Opened += OnViewerOpened;
        ApplyLayout(_currentLayoutSpec, persistLayout: false);
        InitializeSecondaryCaptureUi();
        Closed += OnViewerClosed;
    }

    private static ViewerStudyContext CreateDefaultContext()
    {
        return new ViewerStudyContext
        {
            StudyDetails = new StudyDetails
            {
                Study = new StudyListItem(),
            },
            StartBlank = true,
            LayoutRows = 1,
            LayoutColumns = 1,
        };
    }

    private void InitializeWorkspaceDock()
    {
        _workspaceDockHideTimer.Interval = TimeSpan.FromMilliseconds(WorkspaceDockAutoHideMs);
        _workspaceDockHideTimer.Tick += OnWorkspaceDockHideTimerTick;
        RefreshCenterlinePanelLayoutModeUi();
        ApplyCenterlinePanelLayoutMode();
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private static double GetFloatingPanelOverflowAllowance(double panelSize)
    {
        const double visiblePeek = 56;
        return Math.Max(0, panelSize - visiblePeek);
    }

    private void InitializeActionToolbar()
    {
        OverlayToggleButton.IsChecked = _overlayEnabled;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        _actionToolbarHideTimer.Interval = TimeSpan.FromSeconds(2.2);
        _actionToolbarHideTimer.Tick += OnActionToolbarHideTimerTick;
        SetActionToolbarMode(ActionToolbarMode.ZoomPan);
        ApplyOverlayState();
        RefreshSavedCustomLayoutsUi();
        HideActionToolbar();
    }

    private void ApplyLayout(int rows, int columns)
    {
        ApplyLayout(BuildUniformLayoutSpec(rows, columns));
    }

    private void ApplyLayout(IReadOnlyList<int> rowLayout, IReadOnlyList<ViewportSlotState>? slotStates = null, bool persistLayout = true, bool clearFocusedSingleView = true)
    {
        CloseViewportToolbox();
        List<int> normalizedLayout = NormalizeLayoutSpec(rowLayout);
        List<ViewportSlotState> states = slotStates?.ToList() ?? CaptureViewportSlotStates();
        int activeSlotIndex = states.FindIndex(state => state.IsActive);

        _currentLayoutSpec = [.. normalizedLayout];
        ViewportGrid.RowDefinitions.Clear();
        ViewportGrid.ColumnDefinitions.Clear();
        ViewportGrid.Children.Clear();
        _slots.Clear();

        ViewportGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int row = 0; row < normalizedLayout.Count; row++)
        {
            ViewportGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            var rowGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            for (int column = 0; column < normalizedLayout[row]; column++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            }

            Grid.SetRow(rowGrid, row);
            Grid.SetColumn(rowGrid, 0);
            ViewportGrid.Children.Add(rowGrid);

            for (int column = 0; column < normalizedLayout[row]; column++)
            {
                int index = _slots.Count;
                ViewportSlotState? state = index < states.Count ? states[index] : null;
                var slot = new ViewportSlot();

                var border = new Border
                {
                    Margin = new Thickness(2),
                    Background = Brushes.Black,
                    BorderBrush = new SolidColorBrush(Color.Parse("#FF383838")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    MinWidth = 80,
                    MinHeight = 80,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };

                var panel = new DicomViewPanel
                {
                    WheelMode = GetMouseWheelModeForAction(),
                    ActionMode = _actionToolbarMode,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    ShowOverlay = _overlayEnabled,
                };

                slot.Border = border;
                slot.Panel = panel;
                slot.Series = state?.Series ?? (!_startBlank && index < _context.StudyDetails.Series.Count ? _context.StudyDetails.Series[index] : null);
                slot.InstanceIndex = state?.InstanceIndex ?? 0;
                slot.ViewState = state?.ViewState;
                slot.CurrentSpatialMetadata = state?.CurrentSpatialMetadata;
                slot.Volume = state?.Volume;
                panel.StackItemCount = slot.Series?.Instances.Count ?? 0;

                ConfigureMeasurementPanel(slot, panel);
                ConfigureSecondaryCapturePanel(slot, panel);
                panel.StackScrollRequested += delta => OnStackScroll(border, delta);
                panel.PointerPressed += (_, e) => OnViewportPressed(border, e);
                panel.HoveredImagePointChanged += info => OnPanelHoveredImagePointChanged(slot, info);
                panel.ImagePointPressed += info => OnPanelImagePointPressed(slot, info);
                panel.ViewStateChanged += () => OnPanelViewStateChanged(slot, panel);
                panel.ImageDoubleClicked += () => OnPanelImageDoubleClicked(slot);
                panel.ToolboxRequested += () => OnPanelToolboxRequested(slot);
                border.Child = panel;
                border.PointerPressed += OnViewportPressed;
                _slots.Add(slot);

                Grid.SetColumn(border, column);
                rowGrid.Children.Add(border);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (ViewportSlot slot in _slots)
            {
                LoadSlot(slot);
            }

            SetActiveSlot(GetSlotAtIndex(activeSlotIndex));
            SynchronizeLinkedViews(_activeSlot);
            UpdateStatus();
        }, DispatcherPriority.Loaded);

        SetActiveSlot(GetSlotAtIndex(activeSlotIndex));
        if (CustomLayoutTextBox is not null)
        {
            CustomLayoutTextBox.Text = LayoutSpecToString(_currentLayoutSpec);
        }

        if (clearFocusedSingleView)
        {
            _layoutBeforeFocusedView = null;
        }

        if (persistLayout)
        {
            SaveViewerSettings();
        }

        UpdateStatus();
    }

    private static List<int> BuildUniformLayoutSpec(int rows, int columns)
    {
        rows = Math.Clamp(rows, 1, MaxLayoutRows);
        columns = Math.Clamp(columns, 1, MaxLayoutColumnsPerRow);
        return Enumerable.Repeat(columns, rows).ToList();
    }

    private static List<int> NormalizeLayoutSpec(IReadOnlyList<int> rowLayout)
    {
        List<int> normalized = rowLayout
            .Where(value => value > 0)
            .Take(MaxLayoutRows)
            .Select(value => Math.Clamp(value, 1, MaxLayoutColumnsPerRow))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(1);
        }

        int totalSlots = 0;
        for (int index = 0; index < normalized.Count; index++)
        {
            int remaining = MaxLayoutSlots - totalSlots;
            if (remaining <= 0)
            {
                normalized.RemoveRange(index, normalized.Count - index);
                break;
            }

            normalized[index] = Math.Min(normalized[index], remaining);
            totalSlots += normalized[index];
        }

        return normalized;
    }

    private List<ViewportSlotState> CaptureViewportSlotStates()
    {
        return _slots.Select(slot => new ViewportSlotState(
            slot.Series,
            slot.InstanceIndex,
            slot.ViewState,
            slot.CurrentSpatialMetadata,
            slot.Volume,
            ReferenceEquals(slot, _activeSlot))).ToList();
    }

    private ViewportSlot? GetSlotAtIndex(int index)
    {
        if (_slots.Count == 0)
        {
            return null;
        }

        if (index < 0 || index >= _slots.Count)
        {
            return _slots[0];
        }

        return _slots[index];
    }

    private static bool TryParseLayoutSpec(string? input, out List<int> rowLayout)
    {
        rowLayout = [];
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string normalized = input.Trim();
        char[] separators = [',', ';', '/', '\\', ' '];

        if (normalized.Contains(':') || normalized.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            string compact = normalized.Replace(" ", string.Empty);
            string[] gridParts = compact.Split([':', 'x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (gridParts.Length == 2 &&
                int.TryParse(gridParts[0], out int rows) &&
                int.TryParse(gridParts[1], out int columns) &&
                rows > 0 &&
                columns > 0)
            {
                rowLayout = BuildUniformLayoutSpec(rows, columns);
                return rowLayout.Sum() <= MaxLayoutSlots;
            }
        }

        string[] rowParts = normalized.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rowParts.Length == 0 || rowParts.Length > MaxLayoutRows)
        {
            return false;
        }

        foreach (string rowPart in rowParts)
        {
            if (!int.TryParse(rowPart, out int columns) || columns <= 0 || columns > MaxLayoutColumnsPerRow)
            {
                rowLayout.Clear();
                return false;
            }

            rowLayout.Add(columns);
        }

        if (rowLayout.Sum() > MaxLayoutSlots)
        {
            rowLayout.Clear();
            return false;
        }

        return true;
    }

    private static string LayoutSpecToString(IReadOnlyList<int> rowLayout)
    {
        if (rowLayout.Count == 0)
        {
            return "1:1";
        }

        bool uniform = rowLayout.All(value => value == rowLayout[0]);
        return uniform
            ? $"{rowLayout.Count}:{rowLayout[0]}"
            : string.Join(",", rowLayout);
    }

    private static string LayoutSpecToDisplayText(IReadOnlyList<int> rowLayout)
    {
        if (rowLayout.Count == 0)
        {
            return "1:1";
        }

        bool uniform = rowLayout.All(value => value == rowLayout[0]);
        return uniform
            ? $"{rowLayout.Count}:{rowLayout[0]}"
            : string.Join(" | ", rowLayout.Select((value, index) => $"R{index + 1}={value}"));
    }

    private void RefreshSavedCustomLayoutsUi()
    {
        if (SavedCustomLayoutsPanel is null)
        {
            return;
        }

        SavedCustomLayoutsPanel.Children.Clear();
        if (_savedCustomLayouts.Count == 0)
        {
            SavedCustomLayoutsPanel.Children.Add(new TextBlock
            {
                Text = "No saved layouts yet.",
                Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (string layoutSpec in _savedCustomLayouts)
        {
            if (!TryParseLayoutSpec(layoutSpec, out List<int> parsedLayout))
            {
                continue;
            }

            var button = new Button
            {
                Content = LayoutSpecToDisplayText(parsedLayout),
                Tag = layoutSpec,
                MinWidth = 120,
            };
            button.Classes.Add("popupAction");
            button.Click += OnSavedCustomLayoutClick;
            SavedCustomLayoutsPanel.Children.Add(button);
        }
    }

    private void ApplyCustomLayoutInput(bool savePreset)
    {
        string layoutSpec = CustomLayoutTextBox.Text?.Trim() ?? string.Empty;
        if (!TryParseLayoutSpec(layoutSpec, out List<int> rowLayout))
        {
            ShowToast("Invalid layout. Examples: 2:3 or 1,2", ToastSeverity.Warning);
            return;
        }

        string normalizedSpec = LayoutSpecToString(rowLayout);
        Clear3DCursor();
        ApplyLayout(rowLayout);
        QueueViewportLayoutReset();

        if (savePreset)
        {
            _savedCustomLayouts.RemoveAll(existing => string.Equals(existing, normalizedSpec, StringComparison.OrdinalIgnoreCase));
            _savedCustomLayouts.Insert(0, normalizedSpec);
            if (_savedCustomLayouts.Count > 10)
            {
                _savedCustomLayouts.RemoveRange(10, _savedCustomLayouts.Count - 10);
            }

            RefreshSavedCustomLayoutsUi();
            SaveViewerSettings();
            ShowToast($"Layout {normalizedSpec} saved.", ToastSeverity.Success);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnPanelImageDoubleClicked(ViewportSlot slot)
    {
        SetActiveSlot(slot);

        if (_layoutBeforeFocusedView is null)
        {
            if (_currentLayoutSpec.Count == 1 && _currentLayoutSpec[0] == 1)
            {
                return;
            }

            _layoutBeforeFocusedView = new ViewerLayoutState([.. _currentLayoutSpec], CaptureViewportSlotStates());
            ApplyLayout([1], [new ViewportSlotState(slot.Series, slot.InstanceIndex, slot.ViewState, slot.CurrentSpatialMetadata, slot.Volume, true)], persistLayout: false, clearFocusedSingleView: false);
            QueueViewportLayoutReset();
            return;
        }

        ViewerLayoutState previousLayout = _layoutBeforeFocusedView;
        _layoutBeforeFocusedView = null;
        ApplyLayout(previousLayout.RowLayout, previousLayout.SlotStates, persistLayout: false, clearFocusedSingleView: false);
        QueueViewportLayoutReset();
        SaveViewerSettings();
    }

    private void LoadSlot(ViewportSlot slot, bool refreshThumbnailStrip = true, bool preferExactRemoteFocus = false)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            slot.Panel.ClearImage();
            return;
        }

        // Volume path: if a volume is loaded for this series, use it
        if (slot.Volume is not null)
        {
            if (TryLoadRenderServerSlot(slot, refreshThumbnailStrip))
            {
                return;
            }

            if (IsRenderServerStudy && slot.Series is not null && TryGetRenderServerSeriesKey(slot.Series, out _))
            {
                LogRemoteViewerDiagnostic(slot, $"Remote volume cached for {DescribeSeriesForLog(slot.Series)} but no remote backend is attached; blocking local fallback and re-requesting remote load.");
                slot.Panel.ClearImage();
                _ = EnsureRenderServerBackendLoadedForSlotAsync(slot, slot.Series, showFailureToast: false);
                UpdateSecondaryCaptureIndicator(slot);
                return;
            }

            ApplySlotOverlayStudyInfo(slot);
            bool isCurrentBoundVolume = slot.Panel.BoundVolume == slot.Volume;
            SliceOrientation orientation = isCurrentBoundVolume
                ? slot.Panel.VolumeOrientation
                : SliceOrientation.Axial;
            int sliceCount = isCurrentBoundVolume
                ? Math.Max(1, slot.Panel.VolumeSliceCount)
                : VolumeReslicer.GetSliceCount(slot.Volume, orientation);
            slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, sliceCount - 1));
            slot.Panel.StackItemCount = sliceCount;

            DicomViewPanel.DisplayState? previousState = slot.ViewState;
            if (!isCurrentBoundVolume)
            {
                slot.Panel.BindVolume(slot.Volume, orientation, slot.InstanceIndex);
                ApplyStoredDvrPreferences(slot.Panel);
            }
            else
            {
                slot.Panel.ShowVolumeSlice(slot.InstanceIndex);
            }

            slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
            if (slot.CurrentSpatialMetadata?.FilePath is { Length: > 0 } fp)
                _spatialMetadataCache[fp] = slot.CurrentSpatialMetadata;
            ApplyMeasurementContext(slot);

            if (previousState is not null)
                slot.Panel.ApplyDisplayState(previousState);
            else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
                slot.Panel.SetColorScheme(_selectedColorScheme);
            else if (slot.Panel.IsImageLoaded)
                slot.ViewState = slot.Panel.CaptureDisplayState();

            if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
                RefreshThumbnailStrip(slot.Series);

            UpdateSecondaryCaptureIndicator(slot);
            return;
        }

        // Legacy file-based path
        int totalCount = GetSeriesTotalCount(slot.Series);
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, totalCount - 1));
        slot.Panel.StackItemCount = totalCount;
        InstanceRecord? instance = GetSafeSeriesInstance(slot.Series, slot.InstanceIndex);
        if (instance is null || !IsLocalInstance(instance))
        {
            slot.CurrentSpatialMetadata = null;
            if (IsRenderServerStudy && TryGetRenderServerSeriesKey(slot.Series, out _))
            {
                ApplySlotOverlayStudyInfo(slot);
                QueueRenderServerSlotImageLoad(slot, slot.Series, slot.InstanceIndex, refreshThumbnailStrip);
            }
            else
            {
                slot.Panel.ClearImage();
                RequestSlotPriority(slot, exactFocusOnly: preferExactRemoteFocus);
            }

            if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
            {
                RefreshThumbnailStrip(slot.Series);
            }

            UpdateSecondaryCaptureIndicator(slot);
            return;
        }

        string filePath = instance.FilePath;
        DicomViewPanel.DisplayState? legacyPreviousState = slot.ViewState;
        slot.Panel.LoadFile(filePath);
        slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
        _spatialMetadataCache[filePath] = slot.CurrentSpatialMetadata;
        ApplyMeasurementContext(slot);

        if (legacyPreviousState is not null)
        {
            slot.Panel.ApplyDisplayState(legacyPreviousState);
        }
        else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
        {
            slot.Panel.SetColorScheme(_selectedColorScheme);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }

        UpdateSecondaryCaptureIndicator(slot);
    }

    private void ApplySlotOverlayStudyInfo(ViewportSlot slot)
    {
        StudyDetails? displayedStudy = _isShowingCurrentStudy ? _context.StudyDetails : _thumbnailStripStudy;
        if (displayedStudy is null)
        {
            return;
        }

        string modality = !string.IsNullOrWhiteSpace(slot.Series?.Modality)
            ? slot.Series!.Modality
            : displayedStudy.Study.Modalities;

        slot.Panel.SetOverlayStudyInfo(
            displayedStudy.Study.PatientName,
            displayedStudy.Study.PatientId,
            displayedStudy.Study.StudyDate,
            displayedStudy.Study.StudyDescription,
            displayedStudy.LegacyStudy?.InstitutionName ?? string.Empty,
            modality);
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            ViewportSlot? slot = _slots.FirstOrDefault(candidate => ReferenceEquals(candidate.Border, border));
            SetActiveSlot(slot);
            RequestSlotPriority(slot);
        }
    }

    private void OnViewportToolboxPopupClosed(object? sender, EventArgs e)
    {
        _openToolboxSlot = null;
        RestartActionToolbarHideTimer();
    }

    private void OnNavigateToolClick(object? sender, RoutedEventArgs e)
    {
        SetNavigationTool(NavigationTool.Navigate);
        CloseViewportToolbox();
        UpdateStatus();
        e.Handled = true;
    }

    private void OnTiltPlaneToolClick(object? sender, RoutedEventArgs e)
    {
        SetNavigationTool(NavigationTool.TiltPlane);
        CloseViewportToolbox();
        UpdateStatus();
        e.Handled = true;
    }

    private void SetNavigationTool(NavigationTool tool)
    {
        _navigationTool = tool;
        _measurementTool = MeasurementTool.None;
        RefreshMeasurementPanels();
        UpdateMeasurementToolButtons();
    }

    private void OnToolboxOverlayToggleClick(object? sender, RoutedEventArgs e)
    {
        _overlayEnabled = ToolboxOverlayToggleButton.IsChecked != false;
        ApplyOverlayState();
        SyncViewportToolboxState();
        e.Handled = true;
    }

    private void OnToolboxFlipHorizontalClick(object? sender, RoutedEventArgs e)
    {
        GetToolboxTargetPanel()?.ToggleHorizontalFlip();
        SyncViewportToolboxState();
        e.Handled = true;
    }

    private void OnToolboxFlipVerticalClick(object? sender, RoutedEventArgs e)
    {
        GetToolboxTargetPanel()?.ToggleVerticalFlip();
        SyncViewportToolboxState();
        e.Handled = true;
    }

    private void OnToolboxRotateClockwiseClick(object? sender, RoutedEventArgs e)
    {
        GetToolboxTargetPanel()?.RotateClockwise90();
        SyncViewportToolboxState();
        e.Handled = true;
    }

    private void OnToolboxLinkedSyncToggleClick(object? sender, RoutedEventArgs e)
    {
        _linkedViewSyncEnabled = ToolboxLinkedSyncToggleButton.IsChecked != false;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        UpdateActionModeButtons();
        SaveViewerSettings();
        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(_activeSlot, _activeSlot?.Volume, _activeSlot?.CurrentSpatialMetadata);
            SynchronizeLinkedViews(_activeSlot);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        SyncViewportToolboxState();
        UpdateStatus();
        e.Handled = true;
    }

    private void OnPanelToolboxRequested(ViewportSlot slot)
    {
        SetActiveSlot(slot);

        bool shouldClose = ViewportToolboxPopup.IsOpen && ReferenceEquals(_openToolboxSlot, slot);
        if (shouldClose)
        {
            CloseViewportToolbox();
            return;
        }

        CloseAllActionPopups(ViewportToolboxPopup);
        _openToolboxSlot = slot;
        ViewportToolboxPopup.PlacementTarget = slot.Panel.ToolboxPlacementTarget;
        SyncViewportToolboxState();
        ViewportToolboxPopup.IsOpen = true;
        _actionToolbarHideTimer.Stop();
    }

    private void SyncViewportToolboxState()
    {
        DicomViewPanel? panel = GetToolboxTargetPanel();
        UpdateMeasurementToolButtons();
        UpdateActionModeButtons();
        Update3DCursorToolButton();
        ToolboxFlipHorizontalButton.IsChecked = panel?.IsHorizontallyFlipped == true;
        ToolboxFlipVerticalButton.IsChecked = panel?.IsVerticallyFlipped == true;
        ToolboxRotateClockwiseButton.IsChecked = false;
    }

    private DicomViewPanel? GetToolboxTargetPanel() => _openToolboxSlot?.Panel ?? _activeSlot?.Panel;

    private void CloseViewportToolbox()
    {
        _openToolboxSlot = null;
        ViewportToolboxPopup.IsOpen = false;
    }

    private void SetActiveSlot(ViewportSlot? slot, bool requestPriority = true)
    {
        if (ReferenceEquals(_activeSlot, slot))
        {
            return;
        }

        _activeSlot = slot;
        UpdateSlotVisualStates();
        RefreshThumbnailStrip(slot?.Series);
        if (requestPriority)
        {
            RequestSlotPriority(slot);
        }

        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(slot, slot?.Volume, slot?.CurrentSpatialMetadata);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        RefreshMeasurementInsightPanel();
        RefreshVolumeRoiDraftPanel();
        RefreshCenterlinePanels();
        RefreshReportPanel();
        RefreshRenderingWorkspacePanel();
        RefreshVolumeRoiDeveloperWorkspacePanel();
        UpdateStatus();
        ScheduleMeasurementSessionSave();
    }

    private void OnPanelViewStateChanged(ViewportSlot slot, DicomViewPanel panel)
    {
        if (!panel.IsImageLoaded)
        {
            return;
        }

        if (panel.IsVolumeBound)
        {
            slot.InstanceIndex = panel.VolumeSliceIndex;
            panel.StackItemCount = panel.VolumeSliceCount;
        }

        // Fast path: when the sync loop is driving target panels, it manages
        // display-state capture, reference lines, and side-panel refreshes
        // in a single batch pass after all slots are updated.  Doing that
        // work here for every intermediate panel notification would create
        // O(N²) overhead that blocks the UI thread in large grids (e.g. 4×4).
        if (_isSynchronizingLinkedViews)
        {
            return;
        }

        slot.ViewState = panel.CaptureDisplayState();
        string syncSignature = BuildLinkedSyncSignature(slot, panel);
        bool syncAnchorChanged = !string.Equals(slot.LastLinkedSyncSignature, syncSignature, StringComparison.Ordinal);
        slot.LastLinkedSyncSignature = syncSignature;

        if (ReferenceEquals(slot, _linkedReferenceSourceSlot))
        {
            _linkedReferenceSourceMetadata = slot.CurrentSpatialMetadata;
        }

        if (ReferenceEquals(slot, _activeSlot) && _selectedMeasurementId is not null)
        {
            RefreshMeasurementInsightPanel();
        }

        if (ReferenceEquals(slot, _activeSlot))
        {
            ScheduleVolumeRoiDraftPanelRefresh();
            RefreshRenderingWorkspacePanel(forceVisible: _renderingPanelVisible || _renderingPanelPinned);
        }

        RefreshLinkedReferenceLines();

        if (IsLinkedSyncSuspended())
        {
            return;
        }

        if (!syncAnchorChanged)
        {
            return;
        }

        ScheduleLinkedViewSync(slot);
    }

    private void ScheduleLinkedViewSync(ViewportSlot? sourceSlot)
    {
        if (!_linkedViewSyncEnabled || sourceSlot?.Series is null)
        {
            return;
        }

        _pendingLinkedSyncSourceSlot = sourceSlot;
        if (_isSynchronizingLinkedViews)
        {
            return;
        }

        if (!_linkedSyncThrottleActive)
        {
            // First event: fire immediately, then start the cooldown timer
            // so subsequent events during fast scrolling are coalesced.
            _linkedSyncThrottleActive = true;
            _pendingLinkedSyncSourceSlot = null;
            SynchronizeLinkedViews(sourceSlot);
            _linkedViewSyncDebounceTimer.Start();
        }
        // Else: cooldown timer is already running — the latest source slot
        // is stored and will be applied on the next timer tick.
    }

    private void OnLinkedViewSyncDebounceTimerTick(object? sender, EventArgs e)
    {
        _linkedViewSyncDebounceTimer.Stop();

        if (_isSynchronizingLinkedViews || IsLinkedSyncSuspended())
        {
            if (_pendingLinkedSyncSourceSlot is not null)
            {
                _linkedViewSyncDebounceTimer.Start();
            }

            return;
        }

        ViewportSlot? sourceSlot = _pendingLinkedSyncSourceSlot;
        _pendingLinkedSyncSourceSlot = null;
        if (sourceSlot is null)
        {
            // No pending work — end the throttle cooldown so that
            // the next scroll event fires immediately again.
            _linkedSyncThrottleActive = false;
            return;
        }

        SynchronizeLinkedViews(sourceSlot);
        // Keep the timer running so that further rapid events
        // are coalesced at ~60 fps until scrolling stops.
        _linkedViewSyncDebounceTimer.Start();
    }

    private void UpdateSlotVisualStates()
    {
        foreach (ViewportSlot current in _slots)
        {
            current.Border.BorderBrush = current.IsDropTarget
                ? new SolidColorBrush(Color.Parse("#FF35C7FF"))
                : current == _activeSlot
                    ? new SolidColorBrush(Color.Parse("#FFD9A03C"))
                    : new SolidColorBrush(Color.Parse("#FF383838"));

            current.Border.BorderThickness = current.IsDropTarget
                ? new Thickness(2)
                : new Thickness(1);
        }
    }

    private void OnStackScroll(Border sourceBorder, int delta)
    {
        ViewportSlot? slot = _slots.FirstOrDefault(candidate => ReferenceEquals(candidate.Border, sourceBorder));
        if (slot is null || slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        if (!ReferenceEquals(_activeSlot, slot))
        {
            SetActiveSlot(slot, requestPriority: false);
        }

        int maxIndex = slot.Volume is not null
            ? Math.Max(0, slot.Panel.VolumeSliceCount - 1)
            : GetSeriesTotalCount(slot.Series) - 1;
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex + delta, 0, maxIndex);
        if (slot.Volume is null)
            RequestSlotPriority(slot, Math.Sign(delta));
        LoadSlot(slot, refreshThumbnailStrip: false);
        UpdateStatus();
    }

    private void OnLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        Clear3DCursor();
    }

    private void ApplyColorScheme(int scheme)
    {
        _selectedColorScheme = scheme;

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Panel.IsImageLoaded)
            {
                slot.Panel.SetColorScheme(scheme);
            }
        }

        RefreshRenderingWorkspacePanel(forceVisible: _renderingPanelVisible || _renderingPanelPinned);
        UpdateStatus();
    }

    private void OnWheelModeChanged(object? sender, RoutedEventArgs e)
    {
        ApplyActionModeToPanels();
        UpdateStatus();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ApplyFitToWindow());
    private void OnOriginalClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ZoomToOriginal());
    private void OnResetWindowClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ResetWindowLevel());
    private void OnPrevImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(-1);
    private void OnNextImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(1);

    private void StepActiveImage(int delta)
    {
        if (_activeSlot is null || _activeSlot.Series is null)
        {
            return;
        }

        int maxIndex = _activeSlot.Volume is not null
            ? Math.Max(0, _activeSlot.Panel.VolumeSliceCount - 1)
            : GetSeriesTotalCount(_activeSlot.Series) - 1;
        _activeSlot.InstanceIndex = Math.Clamp(_activeSlot.InstanceIndex + delta, 0, maxIndex);
        if (_activeSlot.Volume is null)
            RequestSlotPriority(_activeSlot, Math.Sign(delta));
        LoadSlot(_activeSlot, refreshThumbnailStrip: false);
        UpdateStatus();
    }

    private void ApplyToActiveOrAll(Action<DicomViewPanel> action)
    {
        if (_activeSlot is not null)
        {
            action(_activeSlot.Panel);
            UpdateStatus();
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            action(slot.Panel);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_activeSlot?.Series is null)
        {
            ViewerStatusText.Text = BuildStatusText(null);
            return;
        }

        ViewerStatusText.Text = BuildStatusText(_activeSlot);
    }

    private async void OnViewerOpened(object? sender, EventArgs e)
    {
        Opened -= OnViewerOpened;
        try
        {
            EnsureMeasurementSessionPersistenceInitialized();
            await LoadAnatomyKnowledgePacksAsync();
            await LoadPriorStudiesAsync();
            await LoadVolumeRoiAnatomyPriorsAsync();
            await SwitchMeasurementSessionAsync(_context.StudyDetails);
        }
        finally
        {
            _remoteRetrievalSession?.StartBackgroundRetrieval();
        }

        // Load volumes in the background for all visible slots
        if (_isShowingCurrentStudy)
        {
            _ = LoadVolumesForSlotsAsync();
        }

        ShowGpuInitStatusToast();
    }

    private void ShowGpuInitStatusToast()
    {
        if (IsRenderServerStudy)
        {
            string remoteGpu = _context.RenderServerConnection?.Capabilities?.GpuDeviceName ?? "Remote render server";
            ShowToast($"Remote rendering active: {remoteGpu}", ToastSeverity.Success, TimeSpan.FromSeconds(8));
            return;
        }

        VolumeComputeBackendStatus status = VolumeComputeBackend.CurrentStatus;
        string fallbackNote = VolumeComputeBackend.CpuFallbackDisabled ? " ⛔ CPU fallback is DISABLED" : "";
        if (status.IsAccelerated)
        {
            ShowToast($"GPU rendering active: {status.DeviceName}{fallbackNote}", ToastSeverity.Success, TimeSpan.FromSeconds(8));
        }
        else
        {
            ShowToast($"GPU rendering unavailable — CPU fallback: {status.Detail}{fallbackNote}", ToastSeverity.Error, TimeSpan.FromSeconds(15));
        }
    }

    private async Task LoadAnatomyKnowledgePacksAsync()
    {
        _anatomyKnowledgePacks.Clear();
        _activeCraniumKnowledgePack = null;
        _activeCraniumKnowledgePackPath = null;

        if (string.IsNullOrWhiteSpace(_anatomyKnowledgePackDirectory) || string.IsNullOrWhiteSpace(_defaultCraniumKnowledgePackPath))
        {
            return;
        }

        Directory.CreateDirectory(_anatomyKnowledgePackDirectory);

        if (!File.Exists(_defaultCraniumKnowledgePackPath))
        {
            AnatomyKnowledgePack defaultPack = _anatomyKnowledgePackService.CreateDefaultCraniumBasePack();
            await _anatomyKnowledgePackService.SaveToFileAsync(defaultPack, _defaultCraniumKnowledgePackPath);
        }

        foreach (string filePath in Directory.EnumerateFiles(_anatomyKnowledgePackDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (_anatomyKnowledgePackService.TryLoadFromFile(filePath, out AnatomyKnowledgePack? pack, out _)
                && pack is not null)
            {
                _anatomyKnowledgePacks.Add(pack);
                if (_activeCraniumKnowledgePackPath is null && string.Equals(pack.Module, "Cranium", StringComparison.OrdinalIgnoreCase))
                {
                    _activeCraniumKnowledgePackPath = filePath;
                }
            }
        }

        _activeCraniumKnowledgePack = _anatomyKnowledgePacks
            .FirstOrDefault(static pack => string.Equals(pack.Module, "Cranium", StringComparison.OrdinalIgnoreCase));

        if (_activeCraniumKnowledgePack is null)
        {
            _activeCraniumKnowledgePack = _anatomyKnowledgePackService.CreateDefaultCraniumBasePack();
            _anatomyKnowledgePacks.Add(_activeCraniumKnowledgePack);
            _activeCraniumKnowledgePackPath = _defaultCraniumKnowledgePackPath;
        }

        if (_activeCraniumKnowledgePackPath is null)
        {
            _activeCraniumKnowledgePackPath = _defaultCraniumKnowledgePackPath;
        }
    }

    private async Task LoadVolumesForSlotsAsync()
    {
        var volumeLoader = new VolumeLoaderService();
        var slotsToLoad = _slots
            .Where(s => s.Series is not null && s.Volume is null)
            .ToList();
        if (IsRenderServerStudy)
        {
            await LoadRenderServerVolumesForSlotsAsync(slotsToLoad);

            return;
        }

        foreach (var slot in slotsToLoad)
        {
            if (slot.Series is null)
                continue;

            string seriesUid = slot.Series.SeriesInstanceUid;

            // Check cache first
            if (_volumeCache.TryGetValue(seriesUid, out var cachedVolume))
            {
                if (cachedVolume is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, cachedVolume));
                }
                continue;
            }

            try
            {
                var volume = await volumeLoader.TryLoadVolumeAsync(slot.Series);
                _volumeCache[seriesUid] = volume;

                if (volume is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, volume));
                }
                    if (IsRenderServerStudy)
                    {
                        ShowToast("Remote render-server study active. Local GPU status does not affect remote rendering.", ToastSeverity.Info, TimeSpan.FromSeconds(8));
                        return;
                    }
            }
            catch
            {
                _volumeCache[seriesUid] = null;
            }
        }
    }

    private void ApplyVolumeToSlot(ViewportSlot slot, SeriesVolume volume)
    {
        string backendKind = IsRenderServerStudy && slot.Series is not null && TryGetCachedRemoteRenderBackend(slot.Series, out RemoteRenderBackend? backend) && backend is not null
            ? $"remote/{backend.Label}"
            : "local";
        LogRemoteViewerDiagnostic(slot, $"Applying volume via {backendKind}. Size={volume.SizeX}x{volume.SizeY}x{volume.SizeZ} for {DescribeSeriesForLog(slot.Series)}.");

        // Preserve current view state and approximate slice position
        DicomViewPanel.DisplayState? viewState = slot.Panel.IsImageLoaded
            ? BuildVolumeTransitionDisplayState(slot.Panel.CaptureDisplayState(), volume)
            : null;
        int previousIndex = slot.InstanceIndex;

        slot.Volume = volume;

        // Map the file-based instance index to the closest volume slice index
        int sliceCount = VolumeReslicer.GetSliceCount(volume, SliceOrientation.Axial);
        int totalInstances = slot.Series is null ? 1 : Math.Max(1, GetSeriesTotalCount(slot.Series));
        if (sliceCount <= 0)
        {
            slot.InstanceIndex = 0;
        }
        else
        {
            int mappedIndex = totalInstances > 1
                ? (int)((long)previousIndex * (sliceCount - 1) / (totalInstances - 1))
                : sliceCount / 2;
            slot.InstanceIndex = Math.Clamp(mappedIndex, 0, sliceCount - 1);
        }

        LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));

        if (viewState is not null)
            slot.Panel.ApplyDisplayState(viewState);

        if (_linkedViewSyncEnabled)
        {
            if (ReferenceEquals(slot, _linkedReferenceSourceSlot))
            {
                SetLinkedReferenceSource(slot, slot.Volume, slot.CurrentSpatialMetadata);
            }

            ReplayPendingLinkedSyncContext();
            RefreshLinkedReferenceLines();
        }

        if (_developerAnatomyModelProjectionEnabled)
        {
            RefreshAnatomyProjectionUi(forceVisible: _anatomyPanelPinned);
            return;
        }

        if (_reportPanelVisible || _reportPanelPinned)
        {
            RefreshReportPanel(forceVisible: _reportPanelPinned);
        }

        UpdateStatus();
    }

    private static DicomViewPanel.DisplayState BuildVolumeTransitionDisplayState(DicomViewPanel.DisplayState state, SeriesVolume volume)
    {
        double defaultThickness = Math.Max(0.1, VolumeReslicer.GetSliceSpacing(volume, SliceOrientation.Axial));
        return state with
        {
            Orientation = SliceOrientation.Axial,
            ProjectionMode = VolumeProjectionMode.Mpr,
            ProjectionThicknessMm = defaultThickness,
            PlaneTiltAroundColumnRadians = 0,
            PlaneTiltAroundRowRadians = 0,
        };
    }

    private async Task LoadPriorStudiesAsync()
    {
        if (_context.InitialPriorStudies is not null)
        {
            UpdatePriorStudies(_context.InitialPriorStudies);
            if (_startBlank)
            {
                _thumbnailStripMessage = _context.InitialPriorStudies.Count == 0
                    ? "No prior studies are available. Select the current study thumbnails to populate this viewer."
                    : "Select the current study or one of the prior-study chips to populate this viewer.";
                RefreshThumbnailStrip(null);
            }

            _ = TryAutoLoadAssignedPriorStudyAsync(_context.InitialPriorStudies);

            return;
        }

        if (_context.LoadPriorStudiesAsync is null)
        {
            UpdatePriorStudies(Array.Empty<PriorStudySummary>());
            return;
        }

        try
        {
            IReadOnlyList<PriorStudySummary> priors = await _context.LoadPriorStudiesAsync(_priorLookupCancellation.Token);
            if (_priorLookupCancellation.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdatePriorStudies(priors);
                if (priors.Count > 0)
                {
                    ShowToast(priors.Count == 1
                        ? "1 prior study is available for comparison."
                        : $"{priors.Count} prior studies are available for comparison.", ToastSeverity.Info);
                }
            });

            _ = TryAutoLoadAssignedPriorStudyAsync(priors);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdatePriorStudies(Array.Empty<PriorStudySummary>()));
        }
    }

    private void UpdatePriorStudies(IReadOnlyList<PriorStudySummary> priors)
    {
        _priorStudies = priors.ToList();
        if (_selectedPriorStudy is not null
            && !_priorStudies.Any(prior => string.Equals(prior.StudyInstanceUid, _selectedPriorStudy.StudyInstanceUid, StringComparison.Ordinal)))
        {
            ShowCurrentStudyThumbnails();
        }

        RenderPriorStudyChips();
    }

    private async Task TryAutoLoadAssignedPriorStudyAsync(IReadOnlyList<PriorStudySummary> availablePriors)
    {
        if (_assignedPriorStudyLoadAttempted)
        {
            return;
        }

        _assignedPriorStudyLoadAttempted = true;

        if (_context.InitialAssignedPriorStudy is not PriorStudySummary assignedPriorStudy)
        {
            return;
        }

        PriorStudySummary? matchedPrior = availablePriors.FirstOrDefault(prior =>
            string.Equals(prior.StudyInstanceUid, assignedPriorStudy.StudyInstanceUid, StringComparison.Ordinal));
        if (matchedPrior is null)
        {
            return;
        }

        await LoadPriorStudyAsync(matchedPrior, populateViewer: true);
    }

    private void RefreshThumbnailStrip(SeriesRecord? activeSeries)
    {
        ThumbnailStripPanel.Children.Clear();

        StudyDetails? thumbnailStudy = _isShowingCurrentStudy ? _context.StudyDetails : _thumbnailStripStudy;
        bool showingCurrentStudy = _isShowingCurrentStudy;

        if (thumbnailStudy is null)
        {
            ShowThumbnailStripMessage(_thumbnailStripMessage);
            return;
        }

        List<SeriesRecord> seriesList = thumbnailStudy.Series
            .Where(series => GetSeriesTotalCount(series) > 0)
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .ToList();

        if (seriesList.Count == 0)
        {
            ShowThumbnailStripMessage(_thumbnailStripMessage);
            return;
        }

        int maxThumbs = Math.Min(seriesList.Count, 40);
        for (int index = 0; index < maxThumbs; index++)
        {
            SeriesRecord series = seriesList[index];
            int representativeIndex = GetRepresentativeInstanceIndex(series);
            InstanceRecord? localRepresentative = IsRemoteOnlyDebugMode ? null : GetBestLocalRepresentativeInstance(series);
            bool isRemoteSeries = IsRenderServerStudy && TryGetRenderServerSeriesKey(series, out _);
            bool isActiveSeries = activeSeries is not null && string.Equals(activeSeries.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.Ordinal);
            bool isSecondaryCaptureSeries = HasManagedSecondaryCapture(series);
            WriteableBitmap? remoteThumbnail = null;
            bool hasRemoteThumbnail = isRemoteSeries && TryGetRenderServerThumbnail(series, out remoteThumbnail) && remoteThumbnail is not null;

            var border = new Border
            {
                Width = 108,
                Height = 86,
                Padding = new Thickness(4),
                Background = Brushes.Black,
                BorderBrush = isActiveSeries ? new SolidColorBrush(Color.Parse("#FFF1E000")) : new SolidColorBrush(Color.Parse("#FF4B4B4B")),
                BorderThickness = isActiveSeries ? new Thickness(2) : new Thickness(1),
            };

            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
            };

            Control thumbnailControl;
            if (localRepresentative is not null)
            {
                var thumbPanel = new DicomViewPanel
                {
                    Width = 98,
                    Height = 58,
                    ShowOverlay = false,
                    ShowToolboxButton = false,
                    IsHitTestVisible = false,
                };
                thumbPanel.LoadFile(localRepresentative.FilePath);
                thumbnailControl = thumbPanel;
            }
            else if (hasRemoteThumbnail)
            {
                thumbnailControl = new Image
                {
                    Width = 98,
                    Height = 58,
                    Source = remoteThumbnail,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false,
                };
            }
            else
            {
                var thumbPanel = new DicomViewPanel
                {
                    Width = 98,
                    Height = 58,
                    ShowOverlay = false,
                    ShowToolboxButton = false,
                    IsHitTestVisible = false,
                };

                if (isRemoteSeries)
                {
                    QueueRenderServerThumbnail(series);
                }
                else
                {
                    RequestSeriesThumbnail(series, representativeIndex);
                }

                thumbnailControl = thumbPanel;
            }

            var label = new TextBlock
            {
                Text = $"S{Math.Max(series.SeriesNumber, index + 1)}  {GetSeriesLoadedCount(series)}/{GetSeriesTotalCount(series)}",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            grid.Children.Add(thumbnailControl);

            if (isRemoteSeries && localRepresentative is null && !hasRemoteThumbnail)
            {
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#66000000")),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text = "☁ Remote",
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                    },
                });
            }
            else if (isRemoteSeries)
            {
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#B21C3558")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 4, 0),
                    Child = new TextBlock
                    {
                        Text = "☁",
                        Foreground = Brushes.White,
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                    },
                });
            }

            if (isSecondaryCaptureSeries)
            {
                var keyBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#E6F6D04D")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#FFFFF4B3")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, 4, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "🔑",
                        FontSize = 12,
                        Foreground = Brushes.Black,
                    },
                };

                ToolTip.SetTip(keyBadge, "K-PACS key image series");
                grid.Children.Add(keyBadge);
            }

            Grid.SetRow(label, 1);
            grid.Children.Add(label);
            border.Child = grid;

            SeriesRecord capturedSeries = series;
            border.PointerPressed += (_, e) => OnThumbnailPointerPressed(capturedSeries, border, e);
            border.PointerMoved += async (_, e) => await OnThumbnailPointerMovedAsync(capturedSeries, border, e);
            border.PointerReleased += (_, e) => OnThumbnailPointerReleased(capturedSeries, border, e);
            border.PointerCaptureLost += (_, _) => CancelThumbnailDrag(border);
            ThumbnailStripPanel.Children.Add(border);
        }

        if (seriesList.Count > maxThumbs)
        {
            ThumbnailStripPanel.Children.Add(new TextBlock
            {
                Text = $"+{seriesList.Count - maxThumbs} more series",
                Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
    }

    private void RenderPriorStudyChips()
    {
        PriorStudiesPanel.Children.Clear();
        PriorStudiesScrollViewer.IsVisible = _priorStudies.Count > 0;
        if (_priorStudies.Count == 0)
        {
            return;
        }

        PriorStudiesPanel.Children.Add(CreateChipButton(
            label: _isShowingCurrentStudy ? "Current study" : "Load current study",
            toolTip: "Show and load the current study in this viewer.",
            isSelected: _isShowingCurrentStudy,
            isRemote: false,
            isLoading: false,
            onClick: (_, _) => ShowCurrentStudyThumbnails()));

        foreach (PriorStudySummary prior in _priorStudies)
        {
            bool isSelected = _selectedPriorStudy is not null
                && string.Equals(_selectedPriorStudy.StudyInstanceUid, prior.StudyInstanceUid, StringComparison.Ordinal);
            string label = prior.IsRemote ? $"☁ {prior.DisplayLabel}" : prior.DisplayLabel;
            if (isSelected && _isPriorPreviewLoading)
            {
                label += " • loading";
            }

            PriorStudiesPanel.Children.Add(CreateChipButton(
                label,
                prior.ToolTipText,
                isSelected,
                prior.IsRemote,
                isSelected && _isPriorPreviewLoading,
                async (_, _) => await ShowPriorStudyThumbnailsAsync(prior)));
        }
    }

    private Button CreateChipButton(string label, string toolTip, bool isSelected, bool isRemote, bool isLoading, EventHandler<RoutedEventArgs> onClick)
    {
        Color background = isSelected
            ? Color.Parse(isRemote ? "#FF224C69" : "#FF5B4A20")
            : Color.Parse(isRemote ? "#FF202D36" : "#FF2B2B2B");
        Color border = isSelected
            ? Color.Parse(isRemote ? "#FF76C8FF" : "#FFFFD66C")
            : Color.Parse(isRemote ? "#FF3A647B" : "#FF565656");
        Color foreground = isLoading ? Color.Parse("#FFFFF1C1") : Colors.White;

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new Border
            {
                Background = new SolidColorBrush(background),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(foreground),
                    FontSize = 12,
                },
            },
        };

        ToolTip.SetTip(button, toolTip);
        button.Click += onClick;
        return button;
    }

    private async void ShowCurrentStudyThumbnails()
    {
        CancelPriorPreviewLoad();
        _isShowingCurrentStudy = true;
        _selectedPriorStudy = null;
        _thumbnailStripStudy = null;
        _thumbnailStripMessage = string.Empty;
        _isPriorPreviewLoading = false;
        _remoteRetrievalSession?.StartBackgroundRetrieval();
        ShowStudyLoadingToast(_context.StudyDetails, "Loading study into viewer");
        await LoadStudyIntoSlotsAsync(_context.StudyDetails);
        RenderPriorStudyChips();
        RefreshThumbnailStrip(_activeSlot?.Series);
        RequestSlotPriority(_activeSlot);
        StudySubtitleText.Text = BuildSubtitle();
    }

    private static int GetStudyInstanceCount(StudyDetails study) => study.Series.Sum(GetSeriesTotalCount);

    private bool ShouldShowStudyLoadingToast(StudyDetails study) =>
        !_isShowingCurrentStudy || GetStudyInstanceCount(study) >= NoticeableStudyInstanceThreshold;

    private bool ShouldShowSeriesLoadingToast(SeriesRecord series) =>
        !_isShowingCurrentStudy || GetSeriesTotalCount(series) >= NoticeableSeriesInstanceThreshold;

    private void ShowStudyLoadingToast(StudyDetails study, string context)
    {
        if (!ShouldShowStudyLoadingToast(study))
        {
            return;
        }

        ShowToast($"{context} ({study.Series.Count} series, {GetStudyInstanceCount(study)} images)…", ToastSeverity.Info, TimeSpan.FromSeconds(6));
    }

    private void ShowSeriesLoadingToast(SeriesRecord series, string context)
    {
        if (!ShouldShowSeriesLoadingToast(series))
        {
            return;
        }

        string label = string.IsNullOrWhiteSpace(series.SeriesDescription)
            ? $"S{series.SeriesNumber}"
            : $"S{series.SeriesNumber} {series.SeriesDescription.Trim()}";
        ShowToast($"{context} {label} ({GetSeriesTotalCount(series)} images)…", ToastSeverity.Info, TimeSpan.FromSeconds(5));
    }

    private async Task ShowPriorStudyThumbnailsAsync(PriorStudySummary priorStudy)
    {
        await LoadPriorStudyAsync(priorStudy, populateViewer: false);
    }

    private async Task LoadPriorStudyAsync(PriorStudySummary priorStudy, bool populateViewer)
    {
        if (_context.LoadPriorStudyPreviewAsync is null)
        {
            return;
        }

        CancelPriorPreviewLoad();
        _remoteRetrievalSession?.StartBackgroundRetrieval();
        _isShowingCurrentStudy = false;
        _selectedPriorStudy = priorStudy;
        _thumbnailStripStudy = null;
        _isPriorPreviewLoading = true;
        _thumbnailStripMessage = priorStudy.IsRemote
            ? "Loading remote prior previews…"
            : "Loading prior previews…";
        ShowToast(
            priorStudy.IsRemote
                ? $"Loading remote prior study {priorStudy.DisplayLabel}…"
                : $"Loading prior study {priorStudy.DisplayLabel}…",
            ToastSeverity.Info,
            TimeSpan.FromSeconds(6));
        RenderPriorStudyChips();
        RefreshThumbnailStrip(null);
        StudySubtitleText.Text = BuildSubtitle();

        _priorPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_priorLookupCancellation.Token);
        try
        {
            await _context.LoadPriorStudyPreviewAsync(priorStudy, details =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_selectedPriorStudy is null || !string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _thumbnailStripStudy = details;
                    _thumbnailStripMessage = string.Empty;
                    if (populateViewer)
                    {
                        _ = LoadStudyIntoSlotsAsync(details);
                    }
                    RefreshThumbnailStrip(null);
                    StudySubtitleText.Text = BuildSubtitle();
                });
            }, _priorPreviewCancellation.Token);

            if (_selectedPriorStudy is not null && string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
            {
                _isPriorPreviewLoading = false;
                if (_thumbnailStripStudy is null || _thumbnailStripStudy.Series.Count == 0)
                {
                    _thumbnailStripMessage = priorStudy.IsRemote
                        ? "No remote prior preview images arrived."
                        : "No prior preview images are available.";
                    RefreshThumbnailStrip(null);
                }

                RenderPriorStudyChips();
                StudySubtitleText.Text = BuildSubtitle();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_selectedPriorStudy is not null && string.Equals(_selectedPriorStudy.StudyInstanceUid, priorStudy.StudyInstanceUid, StringComparison.Ordinal))
            {
                _isPriorPreviewLoading = false;
                _thumbnailStripMessage = $"Prior preview failed: {ex.Message}";
                RefreshThumbnailStrip(null);
                RenderPriorStudyChips();
                StudySubtitleText.Text = BuildSubtitle();
                ShowToast(_thumbnailStripMessage, ToastSeverity.Warning, TimeSpan.FromSeconds(6));
            }
        }
    }

    private async Task LoadStudyIntoSlotsAsync(StudyDetails study)
    {
        await SwitchMeasurementSessionAsync(study);

        IReadOnlyList<SeriesRecord> orderedSeries = IsRenderServerStudy
            ? GetPreferredRemoteStudySeries(study)
            : study.Series;

        for (int index = 0; index < _slots.Count; index++)
        {
            ViewportSlot slot = _slots[index];
            slot.Series = index < orderedSeries.Count ? orderedSeries[index] : null;
            slot.Volume = slot.Series is not null && _volumeCache.TryGetValue(slot.Series.SeriesInstanceUid, out var vol) ? vol : null;
            slot.InstanceIndex = 0;
            slot.ViewState = null;
            LoadSlot(slot, refreshThumbnailStrip: false);

            if (slot.Series is not null && slot.Volume is null)
            {
                _ = EnsureVolumeLoadedForSlotAsync(slot, slot.Series);
            }
        }

        if (!ApplyPendingMeasurementSessionWorkspaceToSlots())
        {
            SetActiveSlot(_slots.FirstOrDefault());
        }

        SynchronizeLinkedViews(_activeSlot);
        UpdateStatus();
    }

    private IReadOnlyList<SeriesRecord> GetPreferredRemoteStudySeries(StudyDetails study)
    {
        return study.Series
            .OrderByDescending(IsPreferredRemoteRenderableSeries)
            .ThenByDescending(GetSeriesTotalCount)
            .ThenBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsPreferredRemoteRenderableSeries(SeriesRecord series)
    {
        return GetSeriesTotalCount(series) >= VolumeLoaderService.MinSlicesForVolume;
    }

    private string BuildSubtitle()
    {
        string baseSubtitle = $"{_context.StudyDetails.Study.StudyDescription}   {_context.StudyDetails.Study.StudyDate}   {_context.StudyDetails.Study.Modalities}".Trim();
        if (_isShowingCurrentStudy)
        {
            return $"Viewer {_viewerNumber}   {baseSubtitle}".Trim();
        }

        if (_selectedPriorStudy is not null)
        {
            return $"Viewer {_viewerNumber}   Comparison mode   {_selectedPriorStudy.DisplayLabel}";
        }

        return $"Viewer {_viewerNumber}   Ready for comparison";
    }

    private void CancelPriorPreviewLoad()
    {
        if (_priorPreviewCancellation is not null)
        {
            _priorPreviewCancellation.Cancel();
            _priorPreviewCancellation.Dispose();
            _priorPreviewCancellation = null;
        }
    }

    private void ShowThumbnailStripMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ThumbnailStripPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 4, 0, 4),
        });
    }

    private void JumpToSeries(SeriesRecord series)
    {
        if (_activeSlot is null || GetSeriesTotalCount(series) == 0)
        {
            return;
        }

        ShowSeriesLoadingToast(series, _isShowingCurrentStudy ? "Loading series" : "Loading comparison series");

        _activeSlot.Series = series;
        _activeSlot.Volume = _volumeCache.TryGetValue(series.SeriesInstanceUid, out var vol) ? vol : null;
        _activeSlot.InstanceIndex = _activeSlot.Volume is not null
            ? VolumeReslicer.GetSliceCount(_activeSlot.Volume, SliceOrientation.Axial) / 2
            : GetRepresentativeInstanceIndex(series);
        if (_activeSlot.Volume is null)
            RequestSeriesPriority(series, _activeSlot.InstanceIndex);
        LoadSlot(_activeSlot);
        if (_activeSlot.Volume is null)
            _ = EnsureVolumeLoadedForSlotAsync(_activeSlot, series);
        UpdateStatus();
    }

    private static int GetRepresentativeInstanceIndex(SeriesRecord series)
    {
        int total = GetSeriesTotalCount(series);
        if (total == 0)
        {
            return 0;
        }

        return total / 2;
    }

    private void OnThumbnailPointerPressed(SeriesRecord series, Border border, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        border.Tag = new PendingThumbnailDrag(series, e.GetPosition(border), GetBestThumbnailPath(series));
    }

    private void OnThumbnailPointerReleased(SeriesRecord series, Border border, PointerReleasedEventArgs e)
    {
        if (border.Tag is not PendingThumbnailDrag pending)
        {
            return;
        }

        if (!pending.IsStarted)
        {
            JumpToSeries(series);
            CancelThumbnailDrag(border);
            return;
        }

        Point screenPoint = GetPointerScreenPoint(e);
        ViewportDropTarget? dropTarget = FindDropTargetAtScreenPoint(screenPoint);
        if (dropTarget is not null)
        {
            dropTarget.Window.AssignSeriesToSlot(dropTarget.Slot, series);
            dropTarget.Window.SetActiveSlot(dropTarget.Slot);
            dropTarget.Window.Activate();
        }

        CancelThumbnailDrag(border);
    }

    private Task OnThumbnailPointerMovedAsync(SeriesRecord series, Border border, PointerEventArgs e)
    {
        if (border.Tag is not PendingThumbnailDrag pending)
        {
            return Task.CompletedTask;
        }

        PointerPoint point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ClearPendingThumbnailDrag(border);
            return Task.CompletedTask;
        }

        Point current = e.GetPosition(border);
        Vector delta = current - pending.StartPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return Task.CompletedTask;
        }

        if (!pending.IsStarted)
        {
            pending.IsStarted = true;
            e.Pointer.Capture(border);
            ShowDragGhost(series, pending.ThumbnailPath, GetPointerScreenPoint(e));
        }

        Point screenPoint = GetPointerScreenPoint(e);
        UpdateDragGhostPosition(screenPoint);
        SetDropTargetAtScreenPoint(screenPoint);
        return Task.CompletedTask;
    }

    private static void ClearPendingThumbnailDrag(Border border)
    {
        border.Tag = null;
    }

    private void CancelThumbnailDrag(Border border)
    {
        ClearPendingThumbnailDrag(border);
        HideDragGhost();
        ClearDropTargetsAcrossViewers();
    }

    private void AssignSeriesToSlot(ViewportSlot slot, SeriesRecord series)
    {
        ShowSeriesLoadingToast(series, _isShowingCurrentStudy ? "Loading series into viewport" : "Loading comparison series into viewport");
        LogRemoteViewerDiagnostic(slot, $"Assigning {DescribeSeriesForLog(series)}. RenderServerStudy={IsRenderServerStudy}. CachedVolume={_volumeCache.ContainsKey(series.SeriesInstanceUid)}.");

        slot.Series = series;
        slot.Volume = _volumeCache.TryGetValue(series.SeriesInstanceUid, out var vol) ? vol : null;
        slot.InstanceIndex = slot.Volume is not null
            ? VolumeReslicer.GetSliceCount(slot.Volume, SliceOrientation.Axial) / 2
            : GetRepresentativeInstanceIndex(series);
        slot.ViewState = null;
        if (slot.Volume is null)
            RequestSeriesPriority(series, slot.InstanceIndex);
        LoadSlot(slot);
        if (slot.Volume is null)
            _ = EnsureVolumeLoadedForSlotAsync(slot, series);
        SynchronizeLinkedViews(_activeSlot ?? slot);
        UpdateStatus();
    }

    private async Task EnsureVolumeLoadedForSlotAsync(ViewportSlot slot, SeriesRecord series)
    {
        if (IsRenderServerStudy && TryGetRenderServerSeriesKey(series, out _))
        {
            LogRemoteViewerDiagnostic(slot, $"Requesting remote backend for {DescribeSeriesForLog(series)}.");
            await EnsureRenderServerBackendLoadedForSlotAsync(slot, series);
            return;
        }

        string seriesUid = series.SeriesInstanceUid;

        if (_volumeCache.TryGetValue(seriesUid, out SeriesVolume? cachedVolume))
        {
            if (cachedVolume is not null && ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, cachedVolume));
            }

            return;
        }

        var volumeLoader = new VolumeLoaderService();

        try
        {
            SeriesVolume? volume = await volumeLoader.TryLoadVolumeAsync(series);
            _volumeCache[seriesUid] = volume;
            LogRemoteViewerDiagnostic(slot, volume is null
                ? $"Local volume load unavailable for {DescribeSeriesForLog(series)}."
                : $"Local volume load succeeded for {DescribeSeriesForLog(series)}.");

            if (volume is not null && ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyVolumeToSlot(slot, volume));
            }
        }
        catch (Exception ex)
        {
            _volumeCache[seriesUid] = null;
            LogRemoteViewerDiagnostic(slot, $"Local volume load failed for {DescribeSeriesForLog(series)}: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void SynchronizeLinkedViews(ViewportSlot? sourceSlot)
    {
        if (!_linkedViewSyncEnabled || _isSynchronizingLinkedViews || IsLinkedSyncSuspended() || sourceSlot?.Series is null || sourceSlot.CurrentSpatialMetadata is null)
        {
            return;
        }

        if (sourceSlot.Panel.IsDvrMode)
        {
            return;
        }

        if (!sourceSlot.Panel.TryCaptureNavigationState(out DicomViewPanel.NavigationState navigationState))
        {
            return;
        }

        DicomSpatialMetadata sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        SetPendingLinkedSyncContext(sourceSlot, sourceSlot.Volume, sourceMetadata, navigationState);
        SetLinkedReferenceSource(sourceSlot, sourceSlot.Volume, sourceMetadata);
        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(navigationState.CenterImagePoint);

        ApplyLinkedViewSync(sourceSlot, sourceSlot?.Volume, sourceMetadata, patientPoint, navigationState, broadcastToPeerViewers: true);
    }

    private void ApplyProjectionToSlot(ViewportSlot slot, SliceProjection projection, bool preferExactRemoteFocus)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        int maxIndex = slot.Volume is not null
            ? Math.Max(0, GetVolumeSliceCount(slot) - 1)
            : Math.Max(0, GetSeriesTotalCount(slot.Series) - 1);
        int targetIndex = Math.Clamp(projection.InstanceIndex, 0, maxIndex);
        InstanceRecord? targetInstance = GetSafeSeriesInstance(slot.Series, targetIndex);
        bool targetIsRemote = slot.Volume is null && (targetInstance is null || !IsLocalInstance(targetInstance));
        bool needsLoad = slot.InstanceIndex != targetIndex || !slot.Panel.IsImageLoaded || targetIsRemote;
        if (!needsLoad)
        {
            return;
        }

        slot.InstanceIndex = targetIndex;
        LoadSlot(slot, refreshThumbnailStrip: false, preferExactRemoteFocus: preferExactRemoteFocus);
    }

    private void ApplyLinkedViewSync(
        ViewportSlot? sourceSlot,
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState,
        bool broadcastToPeerViewers)
    {
        if (!_linkedViewSyncEnabled || _isSynchronizingLinkedViews)
        {
            return;
        }

        try
        {
            _isSynchronizingLinkedViews = true;

            foreach (ViewportSlot targetSlot in _slots)
            {
                if (ReferenceEquals(targetSlot, sourceSlot) || targetSlot.Series is null || targetSlot.Series.Instances.Count == 0)
                {
                    continue;
                }

                if (targetSlot.Panel.IsDvrMode)
                {
                    continue;
                }

                if (targetSlot.CurrentSpatialMetadata is not DicomSpatialMetadata targetMetadata ||
                    !ArePlanesParallel(sourceMetadata, targetMetadata))
                {
                    continue;
                }

                SliceProjection? projection = FindBestProjection(targetSlot, sourceMetadata, patientPoint, sourceVolume);
                if (projection is null)
                {
                    continue;
                }

                // Volume-bound fast path: bypass LoadSlot to avoid the
                // redundant ApplyDisplayState render.  ShowVolumeSlice
                // reslices and renders once; ApplyNavigationState applies
                // the final zoom/pan — 2 renders instead of 3.
                if (targetSlot.Volume is not null && targetSlot.Panel.IsVolumeBound)
                {
                    int maxSlice = Math.Max(0, targetSlot.Panel.VolumeSliceCount - 1);
                    int targetIndex = Math.Clamp(projection.InstanceIndex, 0, maxSlice);
                    if (targetSlot.InstanceIndex != targetIndex || !targetSlot.Panel.IsImageLoaded)
                    {
                        targetSlot.InstanceIndex = targetIndex;
                        targetSlot.Panel.ShowVolumeSlice(targetIndex);
                        targetSlot.CurrentSpatialMetadata = targetSlot.Panel.SpatialMetadata;
                    }
                }
                else
                {
                    ApplyProjectionToSlot(targetSlot, projection, preferExactRemoteFocus: true);
                }

                targetSlot.Panel.ApplyNavigationState(navigationState with { CenterImagePoint = projection.ImagePoint });
                if (targetSlot.Panel.IsImageLoaded)
                {
                    targetSlot.ViewState = targetSlot.Panel.CaptureDisplayState();
                }
            }

            if (broadcastToPeerViewers)
            {
                BroadcastLinkedViewSync(sourceVolume, sourceMetadata, patientPoint, navigationState);
            }
        }
        finally
        {
            _isSynchronizingLinkedViews = false;
        }

        RefreshLinkedReferenceLines();
    }

    private void BroadcastLinkedViewSync(
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState)
    {
        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ApplyExternalLinkedViewSync(sourceVolume, sourceMetadata, patientPoint, navigationState);
        }
    }

    private void ApplyExternalLinkedViewSync(
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        SpatialVector3D patientPoint,
        DicomViewPanel.NavigationState navigationState)
    {
        if (!_linkedViewSyncEnabled || IsLinkedSyncSuspended())
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            SetPendingLinkedSyncContext(null, sourceVolume, sourceMetadata, navigationState);
            SetLinkedReferenceSource(null, sourceVolume, sourceMetadata);
            ApplyLinkedViewSync(
                sourceSlot: null,
                sourceVolume,
                sourceMetadata,
                patientPoint,
                navigationState,
                broadcastToPeerViewers: false);
        }, DispatcherPriority.Input);
    }

    private void SetPendingLinkedSyncContext(
        ViewportSlot? sourceSlot,
        SeriesVolume? sourceVolume,
        DicomSpatialMetadata sourceMetadata,
        DicomViewPanel.NavigationState navigationState)
    {
        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(navigationState.CenterImagePoint);
        _pendingLinkedSyncContext = new PendingLinkedSyncContext(sourceSlot, sourceVolume, sourceMetadata, patientPoint, navigationState);
    }

    private void ReplayPendingLinkedSyncContext()
    {
        if (!_linkedViewSyncEnabled || _pendingLinkedSyncContext is null || _isSynchronizingLinkedViews)
        {
            return;
        }

        PendingLinkedSyncContext context = _pendingLinkedSyncContext;
        ApplyLinkedViewSync(
            context.SourceSlot,
            context.SourceVolume,
            context.SourceMetadata,
            context.PatientPoint,
            context.NavigationState,
            broadcastToPeerViewers: false);
    }

    private void SetLinkedReferenceSource(ViewportSlot? sourceSlot, SeriesVolume? sourceVolume, DicomSpatialMetadata? sourceMetadata)
    {
        _linkedReferenceSourceSlot = sourceMetadata is null ? null : sourceSlot;
        _linkedReferenceSourceVolume = sourceMetadata is null ? null : sourceVolume;
        _linkedReferenceSourceMetadata = sourceMetadata;
        RefreshLinkedReferenceLines();
    }

    private void ClearLinkedReferenceLines()
    {
        _linkedReferenceSourceSlot = null;
        _linkedReferenceSourceVolume = null;
        _linkedReferenceSourceMetadata = null;

        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.SetReferenceLineOverlay(null, null);
        }
    }

    private void RefreshLinkedReferenceLines()
    {
        if (!_linkedViewSyncEnabled || _linkedReferenceSourceMetadata is null)
        {
            foreach (ViewportSlot slot in _slots)
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
            }

            return;
        }

        DicomSpatialMetadata sourceMetadata = _linkedReferenceSourceMetadata;
        foreach (ViewportSlot slot in _slots)
        {
            if (ReferenceEquals(slot, _linkedReferenceSourceSlot) ||
                slot.Series is null ||
                slot.CurrentSpatialMetadata is not DicomSpatialMetadata targetMetadata)
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
                continue;
            }

            DicomSpatialMetadata? effectiveSourceMetadata = GetReferenceMetadataForTarget(
                sourceMetadata,
                _linkedReferenceSourceVolume,
                slot,
                targetMetadata);

            if (effectiveSourceMetadata is null ||
                ArePlanesParallel(effectiveSourceMetadata, targetMetadata))
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
                continue;
            }

            if (TryBuildCutlineSegment(effectiveSourceMetadata, targetMetadata, out Point start, out Point end))
            {
                slot.Panel.SetReferenceLineOverlay(start, end);
            }
            else
            {
                slot.Panel.SetReferenceLineOverlay(null, null);
            }
        }
    }

    private ViewportSlot? GetSlotFromSender(object? sender)
    {
        if (sender is not Border border)
        {
            return null;
        }

        return _slots.FirstOrDefault(slot => ReferenceEquals(slot.Border, border));
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        ApplyCenterlinePanelLayoutMode();
        RequestViewportLayoutReset();
    }

    private void OnViewportLayoutResetTimerTick(object? sender, EventArgs e)
    {
        _viewportLayoutResetTimer.Stop();
        ResetViewportsForLayoutChange();
    }

    private void QueueViewportLayoutReset()
    {
        Dispatcher.UIThread.Post(RequestViewportLayoutReset, DispatcherPriority.Loaded);
    }

    private void RequestViewportLayoutReset()
    {
        _viewportLayoutResetTimer.Stop();
        _viewportLayoutResetTimer.Start();
    }

    private void SetDropTarget(ViewportSlot slot)
    {
        foreach (ViewportSlot current in _slots)
        {
            current.IsDropTarget = ReferenceEquals(current, slot);
        }

        UpdateSlotVisualStates();
    }

    private void ClearDropTargets()
    {
        bool hadDropTarget = false;
        foreach (ViewportSlot current in _slots)
        {
            hadDropTarget |= current.IsDropTarget;
            current.IsDropTarget = false;
        }

        if (hadDropTarget)
        {
            UpdateSlotVisualStates();
        }
    }

    private void ShowDragGhost(SeriesRecord series, string thumbnailPath, Point screenPoint)
    {
        HideDragGhost();

        _dragGhostWindow = new SeriesDragGhostWindow(series, thumbnailPath)
        {
            ShowActivated = false,
        };
        _dragGhostWindow.Show();
        UpdateDragGhostPosition(screenPoint);
    }

    private void UpdateDragGhostPosition(Point screenPoint)
    {
        if (_dragGhostWindow is null)
        {
            return;
        }

        _dragGhostWindow.MoveTo(screenPoint, 14, 14);
    }

    private void HideDragGhost()
    {
        if (_dragGhostWindow is not null)
        {
            _dragGhostWindow.Close();
            _dragGhostWindow = null;
        }
    }

    private void SetDropTargetAtScreenPoint(Point screenPoint)
    {
        ViewportDropTarget? dropTarget = FindDropTargetAtScreenPoint(screenPoint);
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            if (dropTarget is not null && ReferenceEquals(window, dropTarget.Window))
            {
                window.SetDropTarget(dropTarget.Slot);
            }
            else
            {
                window.ClearDropTargets();
            }
        }
    }

    private void SetDropTargetAtPoint(Point overlayPoint)
    {
        ViewportSlot? slot = FindSlotAtOverlayPoint(overlayPoint);
        if (slot is null)
        {
            ClearDropTargets();
            return;
        }

        SetDropTarget(slot);
    }

    private ViewportSlot? FindSlotAtOverlayPoint(Point overlayPoint)
    {
        foreach (ViewportSlot slot in _slots)
        {
            Point? topLeft = slot.Border.TranslatePoint(new Point(0, 0), DragGhostOverlay);
            if (topLeft is null)
            {
                continue;
            }

            Rect rect = new(topLeft.Value, slot.Border.Bounds.Size);
            if (rect.Contains(overlayPoint))
            {
                return slot;
            }
        }

        return null;
    }

    private ViewportDropTarget? FindDropTargetAtScreenPoint(Point screenPoint)
    {
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            ViewportSlot? slot = window.FindSlotAtScreenPoint(screenPoint);
            if (slot is not null)
            {
                return new ViewportDropTarget(window, slot);
            }
        }

        return null;
    }

    private ViewportSlot? FindSlotAtScreenPoint(Point screenPoint)
    {
        foreach (ViewportSlot slot in _slots)
        {
            Point? topLeft = slot.Border.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null)
            {
                continue;
            }

            Point windowOrigin = new(Position.X, Position.Y);
            Rect rect = new(windowOrigin + topLeft.Value, slot.Border.Bounds.Size);
            if (rect.Contains(screenPoint))
            {
                return slot;
            }
        }

        return null;
    }

    private Point GetPointerScreenPoint(PointerEventArgs e)
    {
        Point localPoint = e.GetPosition(this);
        return new Point(Position.X + localPoint.X, Position.Y + localPoint.Y);
    }

    private void ClearDropTargetsAcrossViewers()
    {
        foreach (StudyViewerWindow window in GetOpenViewerWindowsSnapshot())
        {
            window.ClearDropTargets();
        }
    }

    private static List<StudyViewerWindow> GetOpenViewerWindowsSnapshot()
    {
        lock (s_openViewerSyncLock)
        {
            return [.. s_openViewerWindows];
        }
    }

    private void OnPanelImagePointPressed(ViewportSlot sourceSlot, DicomImagePointerInfo info)
    {
        if (sourceSlot.Series is null || sourceSlot.CurrentSpatialMetadata is null)
        {
            return;
        }

        if (TryHandleCenterlineSeedPlacement(sourceSlot, info))
        {
            return;
        }

        if (!Is3DCursorRequested(info.Modifiers))
        {
            return;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);
        Apply3DCursor(sourceSlot, info.ImagePoint);
        UpdateStatus();
    }

    private void OnPanelHoveredImagePointChanged(ViewportSlot sourceSlot, DicomHoverInfo? info)
    {
        if (sourceSlot.Series is null || sourceSlot.CurrentSpatialMetadata is null || info is null)
        {
            return;
        }

        if (_isCenterlineEditMode)
        {
            return;
        }

        if (!Is3DCursorRequested(info.Modifiers))
        {
            return;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);
        Apply3DCursor(sourceSlot, info.ImagePoint);
        UpdateStatus();
    }

    private void OnToolbox3DCursorClick(object? sender, RoutedEventArgs e)
    {
        _is3DCursorToolArmed = !_is3DCursorToolArmed;
        if (_is3DCursorToolArmed)
        {
            SetMeasurementTool(MeasurementTool.None);
        }
        else
        {
            Clear3DCursor();
        }

        Update3DCursorToolButton();
        UpdateStatus();
        e.Handled = true;
    }

    private static string BuildLinkedSyncSignature(ViewportSlot slot, DicomViewPanel panel)
    {
        string seriesUid = slot.Series?.SeriesInstanceUid ?? string.Empty;
        // Round zoom to 4 decimal places to avoid float-noise triggering
        // infinite sync loops while still detecting intentional zoom changes.
        string zoomTag = $"{panel.ZoomFactor:F4}";
        if (panel.IsVolumeBound)
        {
            return $"vol|{seriesUid}|{panel.VolumeOrientation}|{panel.VolumeSliceIndex}|{zoomTag}";
        }

        return $"img|{seriesUid}|{slot.InstanceIndex}|{zoomTag}";
    }

    private void Apply3DCursor(ViewportSlot sourceSlot, Point sourceImagePoint)
    {
        DicomSpatialMetadata? sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        if (sourceMetadata is null || !sourceMetadata.ContainsImagePoint(sourceImagePoint))
        {
            Clear3DCursor();
            return;
        }

        SpatialVector3D patientPoint = sourceMetadata.PatientPointFromPixel(sourceImagePoint);

        try
        {
            _isSynchronizingLinkedViews = true;

            foreach (ViewportSlot slot in _slots)
            {
                if (slot.Series is null || slot.Series.Instances.Count == 0)
                {
                    slot.Panel.Set3DCursorOverlay(null);
                    continue;
                }

                if (ReferenceEquals(slot, sourceSlot))
                {
                    slot.Panel.Set3DCursorOverlay(sourceImagePoint);
                    continue;
                }

                SliceProjection? projection = FindBestProjection(slot, sourceMetadata, patientPoint, sourceSlot.Volume);
                if (projection is null)
                {
                    slot.Panel.Set3DCursorOverlay(null);
                    continue;
                }

                ApplyProjectionToSlot(slot, projection, preferExactRemoteFocus: true);

                slot.CurrentSpatialMetadata = ResolveCurrentSpatialMetadata(slot);
                slot.Panel.Set3DCursorOverlay(projection.ImagePoint);
            }
        }
        finally
        {
            _isSynchronizingLinkedViews = false;
        }

        Broadcast3DCursor(sourceSlot.Volume, sourceMetadata, patientPoint);

        UpdateStatus();
    }

    private void Broadcast3DCursor(SeriesVolume? sourceVolume, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        if (!_linkedViewSyncEnabled)
        {
            return;
        }

        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ApplyExternal3DCursor(sourceVolume, sourceMetadata, patientPoint);
        }
    }

    private void ApplyExternal3DCursor(SeriesVolume? sourceVolume, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        if (!_linkedViewSyncEnabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ApplyCursorToSlots(sourceVolume, sourceMetadata, patientPoint);
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Synchronously updates all viewport slots to reflect the given 3D cursor position.
    /// Must be called on the UI thread. Use this from code paths that are already on the
    /// UI thread (e.g. centerline sync) to avoid an extra Dispatcher.Post round-trip
    /// that would defer painting by one frame.
    /// </summary>
    private void ApplyCursorToSlots(SeriesVolume? sourceVolume, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint)
    {
        try
        {
            _isSynchronizingLinkedViews = true;

            foreach (ViewportSlot slot in _slots)
            {
                if (slot.Series is null || slot.Series.Instances.Count == 0)
                {
                    slot.Panel.Set3DCursorOverlay(null);
                    continue;
                }

                SliceProjection? projection = FindBestProjection(slot, sourceMetadata, patientPoint, sourceVolume);
                if (projection is null)
                {
                    slot.Panel.Set3DCursorOverlay(null);
                    continue;
                }

                ApplyProjectionToSlot(slot, projection, preferExactRemoteFocus: true);

                slot.CurrentSpatialMetadata = ResolveCurrentSpatialMetadata(slot);
                slot.Panel.Set3DCursorOverlay(projection.ImagePoint);
            }
        }
        finally
        {
            _isSynchronizingLinkedViews = false;
        }

        UpdateStatus();
    }

    private SliceProjection? FindBestProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, SeriesVolume? sourceVolume)
    {
        if (slot.Series is null)
        {
            return null;
        }

        if (slot.Volume is not null)
        {
            SliceProjection? directVolumeProjection = FindBestVolumeProjection(slot, sourceMetadata, patientPoint);
            if (directVolumeProjection is not null)
            {
                return directVolumeProjection;
            }
        }

        if (sourceVolume is not null && slot.Volume is not null &&
            VolumeRegistrationService.TryTransformPatientPoint(sourceVolume, slot.Volume, patientPoint, out SpatialVector3D registeredPatientPoint, out _))
        {
            if (slot.Volume is not null)
            {
                SliceProjection? registeredVolumeProjection = FindBestVolumeProjection(slot, sourceMetadata, registeredPatientPoint, allowUnrelatedMetadata: true);
                if (registeredVolumeProjection is not null)
                {
                    return registeredVolumeProjection;
                }
            }

            SliceProjection? registeredLegacyProjection = FindBestLegacyProjection(slot, sourceMetadata, registeredPatientPoint, allowUnrelatedMetadata: true);
            if (registeredLegacyProjection is not null)
            {
                return registeredLegacyProjection;
            }
        }

        return FindBestLegacyProjection(slot, sourceMetadata, patientPoint, allowUnrelatedMetadata: false);
    }

    private SliceProjection? FindBestLegacyProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, bool allowUnrelatedMetadata)
    {
        if (slot.Series is null)
        {
            return null;
        }

        LegacyProjectionSeriesCache cache = GetOrCreateLegacyProjectionSeriesCache(slot.Series);
        DicomSpatialMetadata?[] metadataByIndex = cache.MetadataByIndex;
        bool[] scoutCandidates = cache.ScoutCandidates;
        List<(int Index, DicomSpatialMetadata Metadata)> candidates = [];

        for (int index = 0; index < metadataByIndex.Length; index++)
        {
            DicomSpatialMetadata? metadata = metadataByIndex[index];
            if (metadata is null || !IsProjectionCompatible(sourceMetadata, metadata, allowUnrelatedMetadata))
            {
                continue;
            }

            candidates.Add((index, metadata));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        IEnumerable<(int Index, DicomSpatialMetadata Metadata)> projectionCandidates = candidates.Any(candidate => !scoutCandidates[candidate.Index])
            ? candidates.Where(candidate => !scoutCandidates[candidate.Index])
            : candidates;

        SliceProjection? best = null;

        foreach ((int index, DicomSpatialMetadata metadata) in projectionCandidates)
        {
            Point imagePoint = metadata.PixelPointFromPatient(patientPoint);
            double distance = metadata.DistanceToPlane(patientPoint);
            bool containsPoint = metadata.ContainsImagePoint(imagePoint, tolerance: 1.0);

            if (best is null ||
                distance < best.DistanceToPlane - 1e-6 ||
                (Math.Abs(distance - best.DistanceToPlane) <= 1e-6 && containsPoint && !best.ContainsImagePoint))
            {
                best = new SliceProjection(index, imagePoint, distance, containsPoint);
            }
        }

        return best;
    }

    private static SliceProjection? FindBestVolumeProjection(ViewportSlot slot, DicomSpatialMetadata sourceMetadata, SpatialVector3D patientPoint, bool allowUnrelatedMetadata = false)
    {
        if (slot.Volume is null)
        {
            return null;
        }

        int sliceIndex = GetVolumeSliceIndexForPatientPoint(slot.Volume, slot.Panel.VolumeOrientation, patientPoint);
        DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(slot.Volume, slot.Panel.VolumeOrientation, sliceIndex);
        if (!IsProjectionCompatible(sourceMetadata, metadata, allowUnrelatedMetadata))
        {
            return null;
        }

        Point imagePoint = metadata.PixelPointFromPatient(patientPoint);
        double distance = metadata.DistanceToPlane(patientPoint);
        bool containsPoint = metadata.ContainsImagePoint(imagePoint, tolerance: 1.0);
        return new SliceProjection(sliceIndex, imagePoint, distance, containsPoint);
    }

    private static int GetVolumeSliceIndexForPatientPoint(SeriesVolume volume, SliceOrientation orientation, SpatialVector3D patientPoint)
    {
        SpatialVector3D relative = patientPoint - volume.Origin;

        double rawIndex = orientation switch
        {
            SliceOrientation.Axial => relative.Dot(volume.Normal) / volume.SpacingZ,
            SliceOrientation.Coronal => relative.Dot(volume.ColumnDirection) / volume.SpacingY,
            SliceOrientation.Sagittal => relative.Dot(volume.RowDirection) / volume.SpacingX,
            _ => relative.Dot(volume.Normal) / volume.SpacingZ,
        };

        int maxIndex = VolumeReslicer.GetSliceCount(volume, orientation) - 1;
        return Math.Clamp((int)Math.Round(rawIndex), 0, maxIndex);
    }

    private static DicomSpatialMetadata? GetReferenceMetadataForTarget(
        DicomSpatialMetadata sourceMetadata,
        SeriesVolume? sourceVolume,
        ViewportSlot targetSlot,
        DicomSpatialMetadata targetMetadata)
    {
        if (HasSharedFrameOfReference(sourceMetadata, targetMetadata))
        {
            return sourceMetadata;
        }

        if (sourceVolume is null || targetSlot.Volume is null ||
            !VolumeRegistrationService.TryGetRegistration(sourceVolume, targetSlot.Volume, out VolumeTranslationRegistration registration))
        {
            return null;
        }

        return sourceMetadata with
        {
            Origin = sourceMetadata.Origin + registration.Translation,
            FrameOfReferenceUid = targetMetadata.FrameOfReferenceUid,
            AcquisitionNumber = targetMetadata.AcquisitionNumber,
        };
    }

    private static bool IsProjectionCompatible(DicomSpatialMetadata sourceMetadata, DicomSpatialMetadata targetMetadata, bool allowUnrelatedMetadata)
    {
        if (allowUnrelatedMetadata)
        {
            return true;
        }

        return HasSharedFrameOfReference(sourceMetadata, targetMetadata);
    }

    private static bool HasSharedFrameOfReference(DicomSpatialMetadata first, DicomSpatialMetadata second)
    {
        if (string.IsNullOrWhiteSpace(first.FrameOfReferenceUid) || string.IsNullOrWhiteSpace(second.FrameOfReferenceUid))
        {
            return false;
        }

        return string.Equals(first.FrameOfReferenceUid, second.FrameOfReferenceUid, StringComparison.Ordinal);
    }

    private static bool[] IdentifyScoutCandidateImages(IReadOnlyList<DicomSpatialMetadata?> metadataByIndex)
    {
        bool[] scoutCandidates = new bool[metadataByIndex.Count];
        if (metadataByIndex.Count < 2)
        {
            return scoutCandidates;
        }

        MarkLeadingScoutCandidate(metadataByIndex, scoutCandidates, 0, 1);
        if (metadataByIndex.Count > 2)
        {
            MarkLeadingScoutCandidate(metadataByIndex, scoutCandidates, 1, 2);
            MarkTrailingScoutCandidate(metadataByIndex, scoutCandidates, metadataByIndex.Count - 2, metadataByIndex.Count - 3);
        }

        MarkTrailingScoutCandidate(metadataByIndex, scoutCandidates, metadataByIndex.Count - 1, metadataByIndex.Count - 2);
        return scoutCandidates;
    }

    private static void MarkLeadingScoutCandidate(IReadOnlyList<DicomSpatialMetadata?> metadataByIndex, bool[] scoutCandidates, int candidateIndex, int neighborIndex)
    {
        if ((uint)candidateIndex >= scoutCandidates.Length || (uint)neighborIndex >= metadataByIndex.Count)
        {
            return;
        }

        DicomSpatialMetadata? candidate = metadataByIndex[candidateIndex];
        DicomSpatialMetadata? neighbor = metadataByIndex[neighborIndex];
        if (candidate is null || neighbor is null)
        {
            return;
        }

        if (!ArePlanesParallel(candidate, neighbor))
        {
            scoutCandidates[candidateIndex] = true;
        }
    }

    private static void MarkTrailingScoutCandidate(IReadOnlyList<DicomSpatialMetadata?> metadataByIndex, bool[] scoutCandidates, int candidateIndex, int neighborIndex)
    {
        if ((uint)candidateIndex >= scoutCandidates.Length || (uint)neighborIndex >= metadataByIndex.Count)
        {
            return;
        }

        DicomSpatialMetadata? candidate = metadataByIndex[candidateIndex];
        DicomSpatialMetadata? neighbor = metadataByIndex[neighborIndex];
        if (candidate is null || neighbor is null)
        {
            return;
        }

        if (!ArePlanesParallel(candidate, neighbor))
        {
            scoutCandidates[candidateIndex] = true;
        }
    }

    private static bool ArePlanesParallel(DicomSpatialMetadata first, DicomSpatialMetadata second) =>
        Math.Abs(first.Normal.Dot(second.Normal)) >= ParallelPlaneDotThreshold;

    private static bool TryBuildCutlineSegment(DicomSpatialMetadata sourcePlane, DicomSpatialMetadata targetPlane, out Point start, out Point end)
    {
        start = default;
        end = default;

        double width = Math.Max(0, targetPlane.Width - 1);
        double height = Math.Max(0, targetPlane.Height - 1);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double a = sourcePlane.Normal.Dot(targetPlane.RowDirection) * targetPlane.ColumnSpacing;
        double b = sourcePlane.Normal.Dot(targetPlane.ColumnDirection) * targetPlane.RowSpacing;
        double c = sourcePlane.Normal.Dot(targetPlane.Origin - sourcePlane.Origin);

        if (Math.Abs(a) <= CutlineEdgeTolerance && Math.Abs(b) <= CutlineEdgeTolerance)
        {
            return false;
        }

        var candidates = new List<Point>(4);

        if (Math.Abs(b) > CutlineEdgeTolerance)
        {
            AddCutlineCandidate(candidates, 0, (-c) / b, width, height);
            AddCutlineCandidate(candidates, width, (-(a * width) - c) / b, width, height);
        }

        if (Math.Abs(a) > CutlineEdgeTolerance)
        {
            AddCutlineCandidate(candidates, (-c) / a, 0, width, height);
            AddCutlineCandidate(candidates, (-(b * height) - c) / a, height, width, height);
        }

        if (candidates.Count < 2)
        {
            return false;
        }

        double maxDistanceSquared = double.MinValue;
        for (int i = 0; i < candidates.Count - 1; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                Point candidateStart = candidates[i];
                Point candidateEnd = candidates[j];
                double dx = candidateEnd.X - candidateStart.X;
                double dy = candidateEnd.Y - candidateStart.Y;
                double distanceSquared = (dx * dx) + (dy * dy);
                if (distanceSquared > maxDistanceSquared)
                {
                    maxDistanceSquared = distanceSquared;
                    start = candidateStart;
                    end = candidateEnd;
                }
            }
        }

        return maxDistanceSquared > 1.0;
    }

    private static void AddCutlineCandidate(List<Point> candidates, double x, double y, double width, double height)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
        {
            return;
        }

        Point candidate = new(x, y);
        if (candidate.X < -CutlineEdgeTolerance || candidate.X > width + CutlineEdgeTolerance ||
            candidate.Y < -CutlineEdgeTolerance || candidate.Y > height + CutlineEdgeTolerance)
        {
            return;
        }

        candidate = new Point(
            Math.Clamp(candidate.X, 0, width),
            Math.Clamp(candidate.Y, 0, height));

        if (candidates.Any(existing => Math.Abs(existing.X - candidate.X) <= CutlineEdgeTolerance && Math.Abs(existing.Y - candidate.Y) <= CutlineEdgeTolerance))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private DicomSpatialMetadata? GetSpatialMetadata(InstanceRecord instance)
    {
        if (!IsLocalInstance(instance))
        {
            return null;
        }

        if (_spatialMetadataCache.TryGetValue(instance.FilePath, out DicomSpatialMetadata? cached))
        {
            return cached;
        }

        try
        {
            DicomDataset dataset = DicomFile.Open(instance.FilePath, FellowOakDicom.FileReadOption.ReadAll).Dataset;
            cached = DicomSpatialMetadata.FromDataset(dataset, instance.FilePath);
        }
        catch
        {
            cached = null;
        }

        _spatialMetadataCache[instance.FilePath] = cached;
        return cached;
    }

    private DicomSpatialMetadata? ResolveCurrentSpatialMetadata(ViewportSlot slot)
    {
        if (slot.Panel.SpatialMetadata is not null)
        {
            return slot.Panel.SpatialMetadata;
        }

        InstanceRecord? currentInstance = GetSafeSeriesInstance(slot.Series, slot.InstanceIndex);
        return currentInstance is null ? null : GetSpatialMetadata(currentInstance);
    }

    private LegacyProjectionSeriesCache GetOrCreateLegacyProjectionSeriesCache(SeriesRecord series)
    {
        string seriesUid = series.SeriesInstanceUid ?? string.Empty;
        if (!_legacyProjectionSeriesCache.TryGetValue(seriesUid, out LegacyProjectionSeriesCache? cache) ||
            cache.MetadataByIndex.Length != series.Instances.Count)
        {
            cache = new LegacyProjectionSeriesCache(new DicomSpatialMetadata?[series.Instances.Count], new bool[series.Instances.Count]);
            _legacyProjectionSeriesCache[seriesUid] = cache;
        }

        bool metadataChanged = false;
        for (int index = 0; index < series.Instances.Count; index++)
        {
            InstanceRecord instance = series.Instances[index];
            string currentFilePath = instance.FilePath ?? string.Empty;
            if (!string.Equals(cache.FilePathsByIndex[index], currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                cache.FilePathsByIndex[index] = currentFilePath;
                cache.MetadataByIndex[index] = null;
                metadataChanged = true;
            }

            DicomSpatialMetadata? metadata = GetSpatialMetadata(instance);
            if (!ReferenceEquals(cache.MetadataByIndex[index], metadata))
            {
                cache.MetadataByIndex[index] = metadata;
                metadataChanged = true;
            }
        }

        if (metadataChanged)
        {
            bool[] scoutCandidates = IdentifyScoutCandidateImages(cache.MetadataByIndex);
            if (cache.ScoutCandidates.Length != scoutCandidates.Length)
            {
                cache.ScoutCandidates = scoutCandidates;
            }
            else
            {
                Array.Copy(scoutCandidates, cache.ScoutCandidates, scoutCandidates.Length);
            }
        }

        return cache;
    }

    private void Clear3DCursor(bool broadcastToPeerViewers = true)
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.Set3DCursorOverlay(null);
        }

        if (!broadcastToPeerViewers || !_linkedViewSyncEnabled)
        {
            return;
        }

        List<StudyViewerWindow> peers;
        lock (s_openViewerSyncLock)
        {
            peers = s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .ToList();
        }

        foreach (StudyViewerWindow peer in peers)
        {
            peer.Clear3DCursor(broadcastToPeerViewers: false);
            peer.UpdateStatus();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_isCenterlineEditMode && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (e.Key == Key.Escape)
            {
                SetCenterlineEditMode(false, showToast: true);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Back or Key.Delete)
            {
                if (TryRemoveLastCenterlineSeed())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        if (_activeSlot?.Panel is { HasVolumeRoiDraft: true } drawingPanel)
        {
            if (e.Key == Key.Enter)
            {
                if (drawingPanel.TryCompleteVolumeRoiDraft())
                {
                    RefreshMeasurementPanels();
                    ShowToast("3D ROI created.", ToastSeverity.Success, TimeSpan.FromSeconds(4));
                }
                else
                {
                    ShowToast("Close at least one slice contour with a double-click before finishing the 3D ROI.", ToastSeverity.Warning, TimeSpan.FromSeconds(5));
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                drawingPanel.CancelVolumeRoiDraft();
                RefreshVolumeRoiDraftPanel();
                UpdateStatus();
                ShowToast("3D ROI draft cancelled.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
                e.Handled = true;
                return;
            }

            if (TryRotateVolumeRoiDraftPreview(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                UpdateStatus();
                e.Handled = true;
                return;
            }
        }

        if (_measurementTool == MeasurementTool.BallRoiCorrection)
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox &&
                _activeSlot?.Panel is { } brushPanel)
            {
                int brushDelta = e.Key switch
                {
                    Key.OemOpenBrackets => -1,
                    Key.OemCloseBrackets => 1,
                    Key.OemMinus => -1,
                    Key.OemPlus => 1,
                    Key.Subtract => -1,
                    Key.Add => 1,
                    _ => 0,
                };

                if (brushDelta != 0)
                {
                    int brushStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 3 : 1;
                    if (brushPanel.TryAdjustBallRoiRadius(brushDelta * brushStep, out _))
                    {
                        UpdateStatus();
                    }

                    e.Handled = true;
                    return;
                }
            }
        }

        if (_measurementTool != MeasurementTool.Modify)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
        Vector? delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => null,
        };

        if (delta is not Vector nudgeDelta)
        {
            return;
        }

        if (TryNudgeSelectedMeasurement(nudgeDelta))
        {
            UpdateStatus();
        }

        e.Handled = true;
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            Clear3DCursor();
            UpdateStatus();
        }
    }

    private bool Is3DCursorRequested(KeyModifiers modifiers) =>
        _is3DCursorToolArmed || modifiers.HasFlag(KeyModifiers.Shift);

    private string BuildStatusText(ViewportSlot? slot)
    {
        string toolText = $"Action: {GetActionToolbarModeLabel()}";
        string cursorText = _is3DCursorToolArmed
            ? "3D cursor: click a viewport"
            : "Hold SHIFT for 3D cursor";
        string measurementText = $"Measure: {GetMeasurementToolLabel()}";
        string centerlineText = BuildCenterlineStatusText();
        string nudgeText = _measurementTool == MeasurementTool.Modify
            ? "   Nudge: arrows move selected measurement (SHIFT = 5 px)"
            : string.Empty;
        string ballCorrectionText = _measurementTool == MeasurementTool.BallRoiCorrection
            ? $"   ROI ball: drag the circle across ROI edges to dent or bulge; CTRL+wheel or [ ] changes radius ({(_activeSlot?.Panel?.BallRoiRadiusPixels ?? 0)} px, SHIFT = faster)"
            : string.Empty;
        string volumeRoiText = _measurementTool == MeasurementTool.VolumeRoi || _activeSlot?.Panel is { HasVolumeRoiDraft: true }
            ? "   3D ROI: double-click without a line auto-outlines a 3D lesion, double-click with a line closes contour, use Add to merge another region, preview buttons grow/shrink, arrows rotate mesh (SHIFT = faster), scroll slices, Enter finishes, Esc cancels"
            : string.Empty;
        string polygonAutoOutlineText = _measurementTool == MeasurementTool.PolygonRoi
            ? "   Polygon ROI: double-click without a line auto-outlines the pointed structure"
            : string.Empty;
        string linkedText = _linkedViewSyncEnabled && slot?.CurrentSpatialMetadata is not null
            ? $"Linked: {GetLinkedViewCount(slot)}"
            : "Linked: off";

        if (slot?.Series is null)
        {
            return $"{toolText}   {measurementText}{nudgeText}{ballCorrectionText}{polygonAutoOutlineText}{volumeRoiText}{centerlineText}   {linkedText}   {cursorText}";
        }

        int total = GetSeriesTotalCount(slot.Series);
        int loaded = GetSeriesLoadedCount(slot.Series);
        InstanceRecord? currentInstance = GetSafeSeriesInstance(slot.Series, slot.InstanceIndex);
        bool currentAvailable = currentInstance is not null && IsLocalInstance(currentInstance);
        int displayIndex = total > 0 ? Math.Clamp(slot.InstanceIndex + 1, 1, total) : 0;
        string retrievalText = _remoteRetrievalSession is null
            ? string.Empty
            : currentAvailable
                ? $"   Loaded {loaded}/{total}"
                : $"   Retrieving image {displayIndex}... ({loaded}/{total} local)";

        string projectionText = slot.Panel.IsVolumeBound
            ? $"   {slot.Panel.OrientationLabel}   {slot.Panel.ProjectionModeLabel} {slot.Panel.ProjectionThicknessMm:F1} mm"
            : string.Empty;

        return $"{slot.Series.Modality}   Series {slot.Series.SeriesNumber}   Image {displayIndex}/{total}{projectionText}   {toolText}   {measurementText}{nudgeText}{ballCorrectionText}{polygonAutoOutlineText}{volumeRoiText}{centerlineText}{retrievalText}   {linkedText}   {cursorText}";
    }

    private int GetLinkedViewCount(ViewportSlot sourceSlot)
    {
        DicomSpatialMetadata? sourceMetadata = sourceSlot.CurrentSpatialMetadata;
        if (sourceMetadata is null)
        {
            return 0;
        }

        int count = _slots.Count(slot =>
            slot.Series is not null
            && slot.CurrentSpatialMetadata is not null
            && (HasSharedFrameOfReference(slot.CurrentSpatialMetadata, sourceMetadata)
                || (sourceSlot.Volume is not null && slot.Volume is not null && VolumeRegistrationService.TryGetRegistration(sourceSlot.Volume, slot.Volume, out _))));

        lock (s_openViewerSyncLock)
        {
            count += s_openViewerWindows
                .Where(window => !ReferenceEquals(window, this)
                    && window._linkedViewSyncEnabled
                    && HasLinkedSyncAffinityTo(window))
                .Sum(window => window._slots.Count(slot =>
                    slot.Series is not null
                    && slot.CurrentSpatialMetadata is not null
                    && (HasSharedFrameOfReference(slot.CurrentSpatialMetadata, sourceMetadata)
                        || (sourceSlot.Volume is not null && slot.Volume is not null && VolumeRegistrationService.TryGetRegistration(sourceSlot.Volume, slot.Volume, out _)))));
        }

        return Math.Max(0, count - 1);
    }

    private bool HasLinkedSyncAffinityTo(StudyViewerWindow other)
    {
        if (string.Equals(_context.StudyDetails.Study.StudyInstanceUid, other._context.StudyDetails.Study.StudyInstanceUid, StringComparison.Ordinal))
        {
            return true;
        }

        string patientId = _context.StudyDetails.Study.PatientId?.Trim() ?? string.Empty;
        string otherPatientId = other._context.StudyDetails.Study.PatientId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(patientId) && !string.IsNullOrWhiteSpace(otherPatientId))
        {
            return string.Equals(patientId, otherPatientId, StringComparison.OrdinalIgnoreCase);
        }

        string patientName = _context.StudyDetails.Study.PatientName?.Trim() ?? string.Empty;
        string otherPatientName = other._context.StudyDetails.Study.PatientName?.Trim() ?? string.Empty;
        string patientBirthDate = _context.StudyDetails.Study.PatientBirthDate?.Trim() ?? string.Empty;
        string otherPatientBirthDate = other._context.StudyDetails.Study.PatientBirthDate?.Trim() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(patientName)
            && string.Equals(patientName, otherPatientName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(patientBirthDate)
                || string.IsNullOrWhiteSpace(otherPatientBirthDate)
                || string.Equals(patientBirthDate, otherPatientBirthDate, StringComparison.Ordinal));
    }

    private void RequestSlotPriority(ViewportSlot? slot, int direction = 0, bool exactFocusOnly = false)
    {
        if (slot?.Series is null || _remoteRetrievalSession is null)
        {
            return;
        }

        if (exactFocusOnly)
        {
            _ = _remoteRetrievalSession.RequestFocusedImageAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, direction);
            _ = _remoteRetrievalSession.PrioritizeSeriesAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, 0, direction);
            return;
        }

        if (!_remoteRetrievalSession.IsBackgroundRetrievalEnabled)
        {
            _ = _remoteRetrievalSession.RequestFocusedImageAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, direction);
            return;
        }

        _ = _remoteRetrievalSession.PrioritizeSeriesAsync(slot.Series.SeriesInstanceUid, slot.InstanceIndex, 6, direction);
        if (direction == 0)
        {
            _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(slot.Series.SeriesInstanceUid, 1);
            return;
        }

        QueueAdjacentSeriesPriority(slot.Series.SeriesInstanceUid);
    }

    private void QueueAdjacentSeriesPriority(string seriesInstanceUid)
    {
        if (_remoteRetrievalSession is null || string.IsNullOrWhiteSpace(seriesInstanceUid))
        {
            return;
        }

        _adjacentPriorityDebounceCancellation?.Cancel();
        _adjacentPriorityDebounceCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _adjacentPriorityDebounceCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AdjacentPriorityDebounceMs, cancellation.Token);
                if (cancellation.IsCancellationRequested || _remoteRetrievalSession is null)
                {
                    return;
                }

                _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(seriesInstanceUid, 1, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellation.Token);
    }

    private void RequestSeriesThumbnail(SeriesRecord series, int focusIndex)
    {
        if (_remoteRetrievalSession is null)
        {
            return;
        }

        _ = _remoteRetrievalSession.RequestFocusedImageAsync(series.SeriesInstanceUid, focusIndex);
    }

    private void RequestSeriesPriority(SeriesRecord series, int focusIndex, int direction = 0)
    {
        if (_remoteRetrievalSession is null)
        {
            return;
        }

        _ = _remoteRetrievalSession.PrioritizeSeriesAsync(series.SeriesInstanceUid, focusIndex, 6, direction);
        _ = _remoteRetrievalSession.PrioritizeAdjacentSeriesAsync(series.SeriesInstanceUid, 1);
    }

    private void OnRemoteStudyChanged()
    {
        Dispatcher.UIThread.Post(RefreshRemoteStudyState);
    }

    private void RefreshRemoteStudyState()
    {
        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Series is null)
            {
                continue;
            }

            slot.Panel.StackItemCount = GetSeriesTotalCount(slot.Series);
            if (slot.Series.Instances.Count == 0)
            {
                slot.Panel.ClearImage();
                continue;
            }

            slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, GetSeriesTotalCount(slot.Series) - 1);
            InstanceRecord? current = GetSafeSeriesInstance(slot.Series, slot.InstanceIndex);
            if (current is null || !IsLocalInstance(current))
            {
                if (IsRenderServerStudy && TryGetRenderServerSeriesKey(slot.Series, out _))
                {
                    LoadSlot(slot);
                }
                else
                {
                    slot.Panel.ClearImage();
                }

                continue;
            }

            if (!string.Equals(slot.Panel.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadSlot(slot);
            }
        }

        RefreshThumbnailStrip(_selectedPriorStudy is null ? _activeSlot?.Series : null);
        UpdateStatus();
    }

    private void OnViewerClosed(object? sender, EventArgs e)
    {
        Closed -= OnViewerClosed;
        _measurementSessionSaveDebounceTimer.Stop();
        try
        {
            FlushMeasurementSessionSaveAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        VolumeComputeBackend.GpuFallbackOccurred -= OnGpuFallbackOccurred;
        UnregisterForLinkedViewSync();
        _linkedViewSyncDebounceTimer.Stop();
        _centerlineSyncDebounceTimer.Stop();
        _volumeRoiDraftPanelRefreshTimer.Stop();
        _volumeRoiPreviewAutoRotateTimer.Stop();
        _measurementSessionSaveDebounceTimer.Stop();
        _pendingLinkedSyncSourceSlot = null;
        CancelPriorPreviewLoad();
        _priorLookupCancellation.Cancel();
        _priorLookupCancellation.Dispose();
        _adjacentPriorityDebounceCancellation?.Cancel();
        _adjacentPriorityDebounceCancellation?.Dispose();
        _adjacentPriorityDebounceCancellation = null;
        Opened -= OnViewerOpened;

        if (_remoteRetrievalSession is not null)
        {
            _remoteRetrievalSession.StudyChanged -= OnRemoteStudyChanged;
        }

        DisposeRenderServerBackends();
    }

    private void OnGpuFallbackOccurred(string errorDetail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int failures = VolumeComputeBackend.ConsecutiveGpuFailures;
            string message = failures > 1
                ? $"GPU kernel failed ({failures}× consecutive) — rendering on CPU: {errorDetail}"
                : $"GPU kernel failed — rendering on CPU: {errorDetail}";
            ShowToast(message, ToastSeverity.Warning, TimeSpan.FromSeconds(12));
        });
    }

    private void ShowToast(string message, ToastSeverity severity, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message) || ToastItemsControl is null)
        {
            return;
        }

        if (_toastNotifications.Any(toast => string.Equals(toast.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        ToastNotificationItem toast = CreateToast(message, severity);
        _toastNotifications.Add(toast);
        while (_toastNotifications.Count > 4)
        {
            _toastNotifications.RemoveAt(0);
        }

        _ = DismissToastAsync(toast.Id, duration ?? GetToastDuration(severity));
    }

    private async Task DismissToastAsync(Guid toastId, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, _priorLookupCancellation.Token);
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ToastNotificationItem? toast = _toastNotifications.FirstOrDefault(item => item.Id == toastId);
            if (toast is not null)
            {
                _toastNotifications.Remove(toast);
            }
        });
    }

    private void RegisterForLinkedViewSync()
    {
        lock (s_openViewerSyncLock)
        {
            s_openViewerWindows.Add(this);
        }
    }

    private void UnregisterForLinkedViewSync()
    {
        lock (s_openViewerSyncLock)
        {
            s_openViewerWindows.Remove(this);
        }
    }

    private static TimeSpan GetToastDuration(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => TimeSpan.FromSeconds(4),
        ToastSeverity.Warning => TimeSpan.FromSeconds(8),
        ToastSeverity.Error => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(4),
    };

    private async void OnToastCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string message } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(message);
        }
    }

    private static ToastNotificationItem CreateToast(string message, ToastSeverity severity)
    {
        return severity switch
        {
            ToastSeverity.Success => new ToastNotificationItem
            {
                Icon = "✓",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0174D28")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF2F8F3A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Warning => new ToastNotificationItem
            {
                Icon = "⚠",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07A4E00")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFFB14A")),
                Foreground = Brushes.White,
            },
            ToastSeverity.Error => new ToastNotificationItem
            {
                Icon = "✕",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F07D2222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FFFF7575")),
                Foreground = Brushes.White,
            },
            _ => new ToastNotificationItem
            {
                Icon = "ℹ",
                Message = message,
                Background = new SolidColorBrush(Color.Parse("#F0215D8B")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF65B7F7")),
                Foreground = Brushes.White,
            },
        };
    }

    private static int GetSeriesTotalCount(SeriesRecord series) => Math.Max(series.InstanceCount, series.Instances.Count);

    private static int GetVolumeSliceCount(ViewportSlot slot)
    {
        if (slot.Volume is null)
        {
            return 0;
        }

        return slot.Panel.BoundVolume == slot.Volume
            ? Math.Max(1, slot.Panel.VolumeSliceCount)
            : Math.Max(1, VolumeReslicer.GetSliceCount(slot.Volume, SliceOrientation.Axial));
    }

    private int GetSeriesLoadedCount(SeriesRecord series)
    {
        if (IsRenderServerStudy && TryGetRenderServerSeriesKey(series, out _))
        {
            return GetSeriesTotalCount(series);
        }

        return series.Instances.Count(IsLocalInstance);
    }

    private static bool IsLocalInstance(InstanceRecord instance) =>
        !App.RemoteOnlyDebugModeEnabled
        && !string.IsNullOrWhiteSpace(instance.FilePath)
        && File.Exists(instance.FilePath);

    private static InstanceRecord? GetSafeSeriesInstance(SeriesRecord? series, int instanceIndex)
    {
        if (series is null || series.Instances.Count == 0)
        {
            return null;
        }

        return series.Instances[Math.Clamp(instanceIndex, 0, series.Instances.Count - 1)];
    }

    private static InstanceRecord? GetBestLocalRepresentativeInstance(SeriesRecord series)
    {
        if (series.Instances.Count == 0)
        {
            return null;
        }

        int targetIndex = GetRepresentativeInstanceIndex(series);
        List<InstanceRecord> locals = series.Instances.Where(IsLocalInstance).ToList();
        if (locals.Count == 0)
        {
            return null;
        }

        return locals
            .OrderBy(instance => Math.Abs(instance.InstanceNumber - targetIndex))
            .ThenBy(instance => instance.InstanceNumber)
            .FirstOrDefault();
    }

    private static string GetBestThumbnailPath(SeriesRecord series) => GetBestLocalRepresentativeInstance(series)?.FilePath ?? string.Empty;

    private void LogRemoteViewerDiagnostic(ViewportSlot? slot, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        App.LogRuntimeDiagnostic("REMOTE-VIEWER", $"Viewer{_viewerNumber} {DescribeSlotForLog(slot)} {message}");
    }

    private string DescribeSlotForLog(ViewportSlot? slot)
    {
        if (slot is null)
        {
            return "slot=<none>";
        }

        int slotIndex = _slots.IndexOf(slot);
        return slotIndex >= 0 ? $"slot={slotIndex + 1}" : "slot=<detached>";
    }

    private static string DescribeSeriesForLog(SeriesRecord? series)
    {
        if (series is null)
        {
            return "series=<none>";
        }

        string uid = series.SeriesInstanceUid ?? string.Empty;
        string uidSuffix = uid.Length <= 12 ? uid : uid[^12..];
        return $"series={series.Modality.Trim()} S{Math.Max(1, series.SeriesNumber)} count={Math.Max(series.InstanceCount, series.Instances.Count)} uid=*{uidSuffix}";
    }

    private MouseWheelMode GetMouseWheelModeForAction() =>
        MouseWheelMode.StackScroll;

    private void SetActionToolbarMode(ActionToolbarMode mode)
    {
        bool leavingToolsMode = _actionToolbarMode == ActionToolbarMode.Tools && mode != ActionToolbarMode.Tools;
        _actionToolbarMode = mode;

        if (leavingToolsMode)
        {
            foreach (ViewportSlot slot in _slots)
            {
                slot.Panel.CancelMeasurementInteraction();
            }
        }

        UpdateActionModeButtons();
        ApplyActionModeToPanels();
        RefreshMeasurementPanels();
        UpdateStatus();
    }

    private void UpdateActionModeButtons()
    {
        ActionScrollButton.IsChecked = _actionToolbarMode == ActionToolbarMode.ScrollStack;
        ActionZoomPanButton.IsChecked = _actionToolbarMode == ActionToolbarMode.ZoomPan;
        ActionWindowButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Window;
        ActionToolsButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Tools;
        ActionLayoutButton.IsChecked = _actionToolbarMode == ActionToolbarMode.Layout;
        LinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        OverlayToggleButton.IsChecked = _overlayEnabled;
        if (ToolboxOverlayToggleButton is not null)
        {
            ToolboxOverlayToggleButton.IsChecked = _overlayEnabled;
        }

        if (ToolboxLinkedSyncToggleButton is not null)
        {
            ToolboxLinkedSyncToggleButton.IsChecked = _linkedViewSyncEnabled;
        }
    }

    private void ApplyActionModeToPanels()
    {
        MouseWheelMode mode = GetMouseWheelModeForAction();
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.WheelMode = mode;
            slot.Panel.ActionMode = _actionToolbarMode;
            slot.Panel.NavigationTool = _navigationTool;
            slot.Panel.ShowOverlay = _overlayEnabled;
        }
    }

    private void ApplyOverlayState()
    {
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.ShowOverlay = _overlayEnabled;
        }

        UpdateActionModeButtons();
        UpdateStatus();
    }

    private string GetActionToolbarModeLabel() => "Navigate";

    private void ShowActionToolbar()
    {
        ActionToolbarBorder.IsVisible = false;
        ActionToolbarBorder.Opacity = 0;
        ActionToolbarBorder.IsHitTestVisible = false;
    }

    private void HideActionToolbar()
    {
        ActionToolbarBorder.IsVisible = false;
        ActionToolbarBorder.Opacity = 0;
        ActionToolbarBorder.IsHitTestVisible = false;
    }

    private void RestartActionToolbarHideTimer()
    {
        _actionToolbarHideTimer.Stop();
    }

    private bool IsAnyActionPopupOpen() =>
        WindowPresetPopup.IsOpen || ToolsPopup.IsOpen || LayoutPopup.IsOpen || ViewportToolboxPopup.IsOpen;

    private void CloseAllActionPopups(Popup? except = null)
    {
        if (!ReferenceEquals(WindowPresetPopup, except))
        {
            WindowPresetPopup.IsOpen = false;
        }

        if (!ReferenceEquals(ToolsPopup, except))
        {
            ToolsPopup.IsOpen = false;
        }

        if (!ReferenceEquals(LayoutPopup, except))
        {
            LayoutPopup.IsOpen = false;
        }

        if (!ReferenceEquals(ViewportToolboxPopup, except))
        {
            ViewportToolboxPopup.IsOpen = false;
        }
    }

    private void TogglePopup(Popup popup, Control placementTarget)
    {
        bool shouldOpen = !popup.IsOpen;
        CloseAllActionPopups(popup);
        popup.PlacementTarget = placementTarget;
        popup.IsOpen = shouldOpen;
        ShowActionToolbar();

        if (shouldOpen)
        {
            _actionToolbarHideTimer.Stop();
        }
        else
        {
            RestartActionToolbarHideTimer();
        }
    }

    private void ApplyLayoutFromTag(string? tag)
    {
        if (!TryParseLayoutSpec(tag, out List<int> rowLayout))
        {
            return;
        }

        Clear3DCursor();
        ApplyLayout(rowLayout);
        QueueViewportLayoutReset();
    }

    private void ResetViewportsForLayoutChange(bool broadcastToPeerViewers = true)
    {
        _viewportLayoutResetTimer.Stop();

        List<StudyViewerWindow> peers = [];
        if (broadcastToPeerViewers && _linkedViewSyncEnabled)
        {
            lock (s_openViewerSyncLock)
            {
                peers = s_openViewerWindows
                    .Where(window => !ReferenceEquals(window, this)
                        && window._linkedViewSyncEnabled
                        && HasLinkedSyncAffinityTo(window))
                    .ToList();
            }
        }

        SuspendLinkedSync();
        try
        {
            _pendingLinkedSyncContext = null;
            SetLinkedReferenceSource(null, null, null);

            foreach (ViewportSlot slot in _slots)
            {
                if (!slot.Panel.IsImageLoaded)
                {
                    slot.LastLinkedSyncSignature = null;
                    continue;
                }

                slot.Panel.ApplyFitToWindow();
                slot.ViewState = slot.Panel.CaptureDisplayState();
                slot.LastLinkedSyncSignature = BuildLinkedSyncSignature(slot, slot.Panel);
            }
        }
        finally
        {
            ResumeLinkedSync();
        }

        Clear3DCursor(broadcastToPeerViewers: false);
        RefreshLinkedReferenceLines();
        UpdateStatus();

        foreach (StudyViewerWindow peer in peers)
        {
            peer.ResetViewportsForLayoutChange(broadcastToPeerViewers: false);
        }
    }

    private void SuspendLinkedSync()
    {
        _linkedSyncSuspendCount++;
    }

    private void ResumeLinkedSync()
    {
        if (_linkedSyncSuspendCount > 0)
        {
            _linkedSyncSuspendCount--;
        }
    }

    private bool IsLinkedSyncSuspended() => _linkedSyncSuspendCount > 0;

    private void ApplyWindowPreset(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (string.Equals(tag, "RESET", StringComparison.OrdinalIgnoreCase))
        {
            ApplyToActiveOrAll(panel => panel.ResetWindowLevel());
            return;
        }

        string[] parts = tag.Split('|');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], out double center) ||
            !double.TryParse(parts[1], out double width))
        {
            return;
        }

        ApplyToActiveOrAll(panel => panel.SetWindowLevel(center, width));
    }

    private void OnActionToolbarHideTimerTick(object? sender, EventArgs e)
    {
        _actionToolbarHideTimer.Stop();
        HideActionToolbar();
    }

    private void OnViewerContentPointerMoved(object? sender, PointerEventArgs e)
    {
        RestartActionToolbarHideTimer();

        Point position = e.GetPosition(ViewerContentHost);
        if (!_isWorkspaceDockVisible && IsPointerNearWorkspaceRevealArea(position))
        {
            ShowWorkspaceDock(restartHideTimer: true);
        }
    }

    private void OnViewerContentPointerEntered(object? sender, PointerEventArgs e)
    {
        RestartActionToolbarHideTimer();

        Point position = e.GetPosition(ViewerContentHost);
        if (!_isWorkspaceDockVisible && IsPointerNearWorkspaceRevealArea(position))
        {
            ShowWorkspaceDock(restartHideTimer: true);
        }
    }

    private void OnViewerContentPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isActionToolbarPointerOver && !IsAnyActionPopupOpen())
        {
            _actionToolbarHideTimer.Stop();
            _actionToolbarHideTimer.Start();
        }

        RestartWorkspaceDockHideTimer();
    }

    private void OnActionToolbarPointerEntered(object? sender, PointerEventArgs e)
    {
        _isActionToolbarPointerOver = true;
        ShowActionToolbar();
        _actionToolbarHideTimer.Stop();
    }

    private void OnActionToolbarPointerExited(object? sender, PointerEventArgs e)
    {
        _isActionToolbarPointerOver = false;
        RestartActionToolbarHideTimer();
    }

    private void OnActionScrollClick(object? sender, RoutedEventArgs e)
    {
        CloseAllActionPopups();
        SetActionToolbarMode(ActionToolbarMode.ScrollStack);
        RestartActionToolbarHideTimer();
    }

    private void OnActionZoomPanClick(object? sender, RoutedEventArgs e)
    {
        CloseAllActionPopups();
        SetActionToolbarMode(ActionToolbarMode.ZoomPan);
        RestartActionToolbarHideTimer();
    }

    private void OnActionWindowClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Window);
        TogglePopup(WindowPresetPopup, ActionWindowButton);
    }

    private void OnActionToolsClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Tools);
        TogglePopup(ToolsPopup, ActionToolsButton);
    }

    private void OnActionLayoutClick(object? sender, RoutedEventArgs e)
    {
        SetActionToolbarMode(ActionToolbarMode.Layout);
        TogglePopup(LayoutPopup, ActionLayoutButton);
    }

    private void OnOverlayToggleClick(object? sender, RoutedEventArgs e)
    {
        _overlayEnabled = OverlayToggleButton.IsChecked != false;
        ApplyOverlayState();
        RestartActionToolbarHideTimer();
    }

    private void OnLinkedSyncToggleClick(object? sender, RoutedEventArgs e)
    {
        _linkedViewSyncEnabled = LinkedSyncToggleButton.IsChecked != false;
        UpdateActionModeButtons();
        SaveViewerSettings();
        if (_linkedViewSyncEnabled)
        {
            SetLinkedReferenceSource(_activeSlot, _activeSlot?.Volume, _activeSlot?.CurrentSpatialMetadata);
            SynchronizeLinkedViews(_activeSlot);
        }
        else
        {
            ClearLinkedReferenceLines();
        }

        RestartActionToolbarHideTimer();
    }

    private void OnStudyBrowserClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: true);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window browserWindow)
        {
            if (browserWindow.WindowState == WindowState.Minimized)
            {
                browserWindow.WindowState = WindowState.Normal;
            }

            browserWindow.Show();
            browserWindow.Activate();
        }

        RestartActionToolbarHideTimer();
    }

    private void OnWorkspaceLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            CloseViewportToolbox();
            ShowWorkspaceDock(restartHideTimer: false);
            _workspaceDockHideTimer.Stop();
            SetActionToolbarMode(ActionToolbarMode.Layout);
            TogglePopup(LayoutPopup, control);
        }
    }

    private void OnWorkspaceAnatomyClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
        _anatomyPanelVisible = !_anatomyPanelVisible;
        if (_anatomyPanelVisible)
        {
            RefreshAnatomyPanel(forceVisible: true);
        }
        else
        {
            HideAnatomyPanel();
        }

        SaveViewerSettings();
        ScheduleMeasurementSessionSave();
    }

    private void OnWorkspaceReportClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
        _reportPanelVisible = !_reportPanelVisible;
        if (_reportPanelVisible)
        {
            RefreshReportPanel(forceVisible: true);
        }
        else
        {
            HideReportPanel();
        }

        SaveViewerSettings();
    }

    private void LoadViewerSettings(IReadOnlyList<int> defaultLayout)
    {
        _savedCustomLayouts = [];
        _currentLayoutSpec = [.. defaultLayout];

        try
        {
            if (string.IsNullOrWhiteSpace(_viewerSettingsPath) || !File.Exists(_viewerSettingsPath))
            {
                return;
            }

            StudyViewerSettings? settings = JsonSerializer.Deserialize<StudyViewerSettings>(File.ReadAllText(_viewerSettingsPath));
            if (settings is not null)
            {
                _linkedViewSyncEnabled = settings.LinkedViewSyncEnabled;
                _measurementInsightPinned = settings.MeasurementInsightPinned;
                _measurementInsightCollapsed = settings.MeasurementInsightCollapsed;
                _measurementInsightOffset = new Point(settings.MeasurementInsightOffsetX, settings.MeasurementInsightOffsetY);
                _volumeRoiPreviewPinned = settings.VolumeRoiPreviewPinned;
                _volumeRoiPreviewAutoRotateEnabled = settings.VolumeRoiPreviewAutoRotateEnabled;
                _volumeRoiPreviewOffset = new Point(settings.VolumeRoiPreviewOffsetX, settings.VolumeRoiPreviewOffsetY);
                _reportPanelPinned = false;
                _reportPanelVisible = false;
                _reportPanelOffset = new Point(settings.ReportPanelOffsetX, settings.ReportPanelOffsetY);
                _anatomyPanelPinned = false;
                _anatomyPanelVisible = false;
                _anatomyPanelOffset = new Point(settings.AnatomyPanelOffsetX, settings.AnatomyPanelOffsetY);
                _renderingPanelPinned = false;
                _renderingPanelVisible = false;
                _renderingPanelOffset = new Point(settings.RenderingPanelOffsetX, settings.RenderingPanelOffsetY);
                if (Enum.TryParse(settings.RenderingBackendPreference, ignoreCase: true, out VolumeComputePreference renderingBackendPreference))
                {
                    _renderingBackendPreference = renderingBackendPreference;
                }
                if (Enum.TryParse(settings.PreferredDvrPreset, ignoreCase: true, out TransferFunctionPreset preferredDvrPreset))
                {
                    _preferredDvrPreset = preferredDvrPreset;
                }
                if (Enum.TryParse(settings.PreferredDvrShadingPreset, ignoreCase: true, out VolumeShadingPreset preferredDvrShadingPreset))
                {
                    _preferredDvrShadingPreset = preferredDvrShadingPreset;
                }
                if (Enum.TryParse(settings.PreferredDvrLightDirectionPreset, ignoreCase: true, out VolumeLightDirectionPreset preferredDvrLightDirectionPreset))
                {
                    _preferredDvrLightDirectionPreset = preferredDvrLightDirectionPreset;
                }
                _preferredDvrAutoColorLutEnabled = settings.PreferredDvrAutoColorLutEnabled;
                _reportDebugEnabled = settings.ReportDebugEnabled;
                _customAnatomyRegions.Clear();
                if (settings.CustomAnatomyRegions is not null)
                {
                    foreach (string region in settings.CustomAnatomyRegions.Where(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        if (!_customAnatomyRegions.Contains(region.Trim(), StringComparer.OrdinalIgnoreCase))
                        {
                            _customAnatomyRegions.Add(region.Trim());
                        }
                    }
                }
                _customAnatomyStructuresByRegion.Clear();
                if (settings.CustomAnatomyStructuresByRegion is not null)
                {
                    foreach ((string region, List<string> structures) in settings.CustomAnatomyStructuresByRegion)
                    {
                        if (string.IsNullOrWhiteSpace(region))
                        {
                            continue;
                        }

                        List<string> cleaned = structures
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (cleaned.Count > 0)
                        {
                            _customAnatomyStructuresByRegion[region.Trim()] = cleaned;
                        }
                    }
                }
                _reportRegionOverrides.Clear();
                if (settings.ReportRegionOverrides is not null)
                {
                    foreach ((string measurementId, string label) in settings.ReportRegionOverrides)
                    {
                        if (Guid.TryParse(measurementId, out Guid parsedId) && !string.IsNullOrWhiteSpace(label))
                        {
                            _reportRegionOverrides[parsedId] = label.Trim();
                        }
                    }
                }
                _reportAnatomyOverrides.Clear();
                if (settings.ReportAnatomyOverrides is not null)
                {
                    foreach ((string measurementId, string label) in settings.ReportAnatomyOverrides)
                    {
                        if (Guid.TryParse(measurementId, out Guid parsedId) && !string.IsNullOrWhiteSpace(label))
                        {
                            _reportAnatomyOverrides[parsedId] = label.Trim();
                        }
                    }
                }
                _reportReviewStates.Clear();
                if (settings.ReportReviewStates is not null)
                {
                    foreach ((string measurementId, string state) in settings.ReportReviewStates)
                    {
                        if (Guid.TryParse(measurementId, out Guid parsedId) && !string.IsNullOrWhiteSpace(state))
                        {
                            _reportReviewStates[parsedId] = state.Trim();
                        }
                    }
                }
                _savedCustomLayouts = settings.SavedCustomLayouts?
                    .Where(layout => TryParseLayoutSpec(layout, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList() ?? [];

                if (TryParseLayoutSpec(settings.LastLayoutSpec, out List<int> persistedLayout))
                {
                    _currentLayoutSpec = persistedLayout;
                }

                ApplyRenderingBackendPreference(rerenderLoadedVolumes: false);
            }
        }
        catch
        {
            _linkedViewSyncEnabled = true;
            _savedCustomLayouts = [];
            _currentLayoutSpec = [.. defaultLayout];
            _renderingBackendPreference = VolumeComputePreference.Auto;
            ApplyRenderingBackendPreference(rerenderLoadedVolumes: false);
        }
    }

    private void SaveViewerSettings()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_viewerSettingsPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_viewerSettingsPath) ?? string.Empty);
            StudyViewerSettings settings = new()
            {
                LinkedViewSyncEnabled = _linkedViewSyncEnabled,
                MeasurementInsightPinned = _measurementInsightPinned,
                MeasurementInsightCollapsed = _measurementInsightCollapsed,
                MeasurementInsightOffsetX = _measurementInsightOffset.X,
                MeasurementInsightOffsetY = _measurementInsightOffset.Y,
                VolumeRoiPreviewPinned = _volumeRoiPreviewPinned,
                VolumeRoiPreviewAutoRotateEnabled = _volumeRoiPreviewAutoRotateEnabled,
                VolumeRoiPreviewOffsetX = _volumeRoiPreviewOffset.X,
                VolumeRoiPreviewOffsetY = _volumeRoiPreviewOffset.Y,
                ReportPanelPinned = _reportPanelPinned,
                ReportPanelVisible = _reportPanelVisible,
                ReportPanelOffsetX = _reportPanelOffset.X,
                ReportPanelOffsetY = _reportPanelOffset.Y,
                AnatomyPanelPinned = _anatomyPanelPinned,
                AnatomyPanelVisible = _anatomyPanelVisible,
                AnatomyPanelOffsetX = _anatomyPanelOffset.X,
                AnatomyPanelOffsetY = _anatomyPanelOffset.Y,
                RenderingPanelPinned = _renderingPanelPinned,
                RenderingPanelVisible = _renderingPanelVisible,
                RenderingPanelOffsetX = _renderingPanelOffset.X,
                RenderingPanelOffsetY = _renderingPanelOffset.Y,
                RenderingBackendPreference = _renderingBackendPreference.ToString(),
                PreferredDvrPreset = _preferredDvrPreset.ToString(),
                PreferredDvrShadingPreset = _preferredDvrShadingPreset.ToString(),
                PreferredDvrLightDirectionPreset = _preferredDvrLightDirectionPreset.ToString(),
                PreferredDvrAutoColorLutEnabled = _preferredDvrAutoColorLutEnabled,
                ReportDebugEnabled = _reportDebugEnabled,
                CustomAnatomyRegions = [.. _customAnatomyRegions],
                CustomAnatomyStructuresByRegion = _customAnatomyStructuresByRegion.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                ReportRegionOverrides = _reportRegionOverrides.ToDictionary(
                    pair => pair.Key.ToString("D"),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                ReportAnatomyOverrides = _reportAnatomyOverrides.ToDictionary(
                    pair => pair.Key.ToString("D"),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                ReportReviewStates = _reportReviewStates.ToDictionary(
                    pair => pair.Key.ToString("D"),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                SavedCustomLayouts = [.. _savedCustomLayouts],
                LastLayoutSpec = LayoutSpecToString(_currentLayoutSpec),
            };

            File.WriteAllText(_viewerSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void OnWindowPresetSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyWindowPreset(button.Tag as string);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnWindowLutSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag && int.TryParse(tag, out int scheme))
        {
            ApplyColorScheme(scheme);
        }

        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
    }

    private void OnLayoutPopupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyLayoutFromTag(button.Tag as string);
        }

        CloseAllActionPopups();
        CloseViewportToolbox();
        RestartActionToolbarHideTimer();
    }

    private void OnApplyCustomLayoutClick(object? sender, RoutedEventArgs e) => ApplyCustomLayoutInput(savePreset: false);

    private void OnSaveCustomLayoutClick(object? sender, RoutedEventArgs e) => ApplyCustomLayoutInput(savePreset: true);

    private void OnSavedCustomLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        ApplyLayoutFromTag(button.Tag as string);
        CloseAllActionPopups();
        CloseViewportToolbox();
        RestartActionToolbarHideTimer();
    }

    private void OnCustomLayoutTextBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCustomLayoutInput(savePreset: false);
            e.Handled = true;
        }
    }

    private void OnAnyActionPopupClosed(object? sender, EventArgs e)
    {
        if (!IsAnyActionPopupOpen())
        {
            RestartActionToolbarHideTimer();
            RestartWorkspaceDockHideTimer();
        }
    }

    private void OnWorkspaceDockHideTimerTick(object? sender, EventArgs e)
    {
        _workspaceDockHideTimer.Stop();
        HideWorkspaceDock();
    }

    private void OnWorkspaceDockPointerEntered(object? sender, PointerEventArgs e)
    {
        _isWorkspaceDockPointerOver = true;
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
    }

    private void OnWorkspaceDockPointerExited(object? sender, PointerEventArgs e)
    {
        _isWorkspaceDockPointerOver = false;
        RestartWorkspaceDockHideTimer();
    }

    private void OnWorkspaceDockRevealPointerEntered(object? sender, PointerEventArgs e)
    {
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private void OnWorkspaceDockRevealClick(object? sender, RoutedEventArgs e)
    {
        ShowWorkspaceDock(restartHideTimer: true);
    }

    private void ShowWorkspaceDock(bool restartHideTimer)
    {
        _isWorkspaceDockVisible = true;
        WorkspaceDock.IsVisible = true;
        WorkspaceDockRevealButton.IsVisible = false;

        if (restartHideTimer)
        {
            RestartWorkspaceDockHideTimer();
        }
    }

    private void HideWorkspaceDock()
    {
        if (_isWorkspaceDockPointerOver || IsAnyActionPopupOpen())
        {
            return;
        }

        _isWorkspaceDockVisible = false;
        WorkspaceDock.IsVisible = false;
        WorkspaceDockRevealButton.IsVisible = true;
    }

    private void RestartWorkspaceDockHideTimer()
    {
        _workspaceDockHideTimer.Stop();
        if (_isWorkspaceDockVisible && !_isWorkspaceDockPointerOver && !IsAnyActionPopupOpen())
        {
            _workspaceDockHideTimer.Start();
        }
    }

    private bool IsPointerNearWorkspaceRevealArea(Point position)
    {
        double centerX = ViewerContentHost.Bounds.Width / 2.0;
        return position.Y <= 32 && Math.Abs(position.X - centerX) <= 220;
    }

    private void Update3DCursorToolButton()
    {
        if (Toolbox3DCursorButton is not null)
        {
            Toolbox3DCursorButton.IsChecked = _is3DCursorToolArmed;
        }
    }

    private sealed class ViewportSlot
    {
        public Border Border { get; set; } = null!;
        public DicomViewPanel Panel { get; set; } = null!;
        public SeriesRecord? Series { get; set; }
        public int InstanceIndex { get; set; }
        public DicomViewPanel.DisplayState? ViewState { get; set; }
        public DicomSpatialMetadata? CurrentSpatialMetadata { get; set; }
        public bool IsDropTarget { get; set; }
        public SeriesVolume? Volume { get; set; }
        public string? LastLinkedSyncSignature { get; set; }
    }

    private sealed record ViewportSlotState(
        SeriesRecord? Series,
        int InstanceIndex,
        DicomViewPanel.DisplayState? ViewState,
        DicomSpatialMetadata? CurrentSpatialMetadata,
        SeriesVolume? Volume,
        bool IsActive);

    private sealed record ViewerLayoutState(List<int> RowLayout, List<ViewportSlotState> SlotStates);

    private sealed record SliceProjection(int InstanceIndex, Point ImagePoint, double DistanceToPlane, bool ContainsImagePoint);
    private sealed record PendingLinkedSyncContext(
        ViewportSlot? SourceSlot,
        SeriesVolume? SourceVolume,
        DicomSpatialMetadata SourceMetadata,
        SpatialVector3D PatientPoint,
        DicomViewPanel.NavigationState NavigationState);

    private sealed class LegacyProjectionSeriesCache(DicomSpatialMetadata?[] metadataByIndex, bool[] scoutCandidates)
    {
        public DicomSpatialMetadata?[] MetadataByIndex { get; } = metadataByIndex;
        public string[] FilePathsByIndex { get; } = new string[metadataByIndex.Length];
        public bool[] ScoutCandidates { get; set; } = scoutCandidates;
    }

    private sealed record ViewportDropTarget(StudyViewerWindow Window, ViewportSlot Slot);

    private sealed class PendingThumbnailDrag(SeriesRecord series, Point startPoint, string thumbnailPath)
    {
        public SeriesRecord Series { get; } = series;
        public Point StartPoint { get; } = startPoint;
        public string ThumbnailPath { get; } = thumbnailPath;
        public bool IsStarted { get; set; }
    }

    private sealed class StudyViewerSettings
    {
        public bool LinkedViewSyncEnabled { get; init; } = true;
        public bool MeasurementInsightPinned { get; init; }
        public bool MeasurementInsightCollapsed { get; init; }
        public double MeasurementInsightOffsetX { get; init; }
        public double MeasurementInsightOffsetY { get; init; }
        public bool VolumeRoiPreviewPinned { get; init; }
        public bool VolumeRoiPreviewAutoRotateEnabled { get; init; }
        public double VolumeRoiPreviewOffsetX { get; init; }
        public double VolumeRoiPreviewOffsetY { get; init; }
        public bool ReportPanelPinned { get; init; }
        public bool ReportPanelVisible { get; init; }
        public double ReportPanelOffsetX { get; init; }
        public double ReportPanelOffsetY { get; init; }
        public bool AnatomyPanelPinned { get; init; }
        public bool AnatomyPanelVisible { get; init; }
        public double AnatomyPanelOffsetX { get; init; }
        public double AnatomyPanelOffsetY { get; init; }
        public bool RenderingPanelPinned { get; init; }
        public bool RenderingPanelVisible { get; init; }
        public double RenderingPanelOffsetX { get; init; }
        public double RenderingPanelOffsetY { get; init; }
        public string? RenderingBackendPreference { get; init; }
        public string? PreferredDvrPreset { get; init; }
        public string? PreferredDvrShadingPreset { get; init; }
        public string? PreferredDvrLightDirectionPreset { get; init; }
        public bool PreferredDvrAutoColorLutEnabled { get; init; } = true;
        public bool ReportDebugEnabled { get; init; }
        public List<string>? CustomAnatomyRegions { get; init; }
        public Dictionary<string, List<string>>? CustomAnatomyStructuresByRegion { get; init; }
        public Dictionary<string, string>? ReportRegionOverrides { get; init; }
        public Dictionary<string, string>? ReportAnatomyOverrides { get; init; }
        public Dictionary<string, string>? ReportReviewStates { get; init; }
        public List<string>? SavedCustomLayouts { get; init; }
        public string? LastLayoutSpec { get; init; }
    }
}
