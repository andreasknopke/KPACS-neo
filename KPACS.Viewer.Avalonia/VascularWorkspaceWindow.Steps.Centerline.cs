using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

/// <summary>
/// EVAR-Schritt 2 — Centerline (Phase B3). Verwaltet benannte Gefäß-Segmente
/// (Aorta, Iliakal, Renal als Presets), erfasst Start-/End-/Guide-Seeds im MPR
/// (SHIFT/ALT/CTRL-Schema), berechnet die mediale Centerline über
/// <see cref="MedialCenterlineService"/> (benötigt die Lumen-Maske aus Schritt 1),
/// und zeigt sie im DVR/MPR (Overlay), im CPR-Viewport (curved MPR) und im
/// Orthogonalschnitt-Viewport (mit Stations-Scrubber). Ein <see cref="VesselTree"/>
/// verknüpft die Segmente an ihren Bifurkationen.
/// </summary>
public partial class VascularWorkspaceWindow
{
    // ── Segment-Verwaltung ────────────────────────────────────────────────────
    private sealed class CenterlineSegmentEntry
    {
        public required CenterlineSeedSet SeedSet { get; set; }
        public CenterlinePath? Path { get; set; }
        public required string PresetLabel { get; init; }
        public required string DisplayName { get; init; }
        public string? ParentLabel { get; init; }
    }

    private readonly List<CenterlineSegmentEntry> _centerlineSegments = [];
    private int _activeCenterlineSegmentIndex = -1;
    private VesselTree _vesselTree = new();
    private readonly ICenterlineExtractionService _centerlineService = new MedialCenterlineService();

    // Seed-Erfassung.
    private bool _clSeedCaptureArmed;
    private bool _clSeedHandlerWired;

    // Laufzeitsteuerung.
    private CancellationTokenSource? _centerlineCts;
    private bool _centerlineRunning;

    // CPR-Viewport.
    private const double CprFieldOfViewMm = 90.0;
    private const double CprSlabThicknessMm = 8.0;
    private const int CprImageHeight = 241;
    private readonly byte[] _cprLut = Enumerable.Range(0, 256).Select(static v => (byte)v).ToArray();
    private Image? _cprImage;
    private WriteableBitmap? _cprBitmap;
    private byte[]? _cprRenderBuffer;
    private CancellationTokenSource? _cprRenderCts;
    private int _cprRenderVersion;

    // Orthogonalschnitt-Viewport.
    private const double CrossSectionFieldOfViewMm = 70.0;
    private const int CrossSectionImageSize = 220;
    private readonly byte[] _csLut = Enumerable.Range(0, 256).Select(static v => (byte)v).ToArray();
    private Image? _csImage;
    private WriteableBitmap? _csBitmap;
    private byte[]? _csRenderBuffer;
    private CancellationTokenSource? _csRenderCts;
    private int _csRenderVersion;
    private int _csStationIndex;

    // Sidebar-Referenzen.
    private ComboBox? _presetCombo;
    private ListBox? _segmentList;
    private Button? _armSeedButtonCl;
    private Button? _computeButton;
    private Slider? _stationSlider;
    private TextBlock? _clStatusCardText;
    private TextBlock? _clProgressText;
    private ProgressBar? _clProgressBar;

    private void ShowCenterlineStep()
    {
        SidebarHeader.Text = "Centerline";
        SidebarBody.Text = _volume is null
            ? "Keine Serie geladen."
            : "Segmente hinzufügen, Seeds im MPR setzen und mediale Centerline berechnen.";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        WireCenterlineSeedCapture();
        EnsureCprViewport();
        EnsureCrossSectionViewport();
        StepPanelHost.Content = BuildCenterlinePanel();
        RefreshCenterlineStatus();
        ApplyCenterlineOverlays();
    }

    private Control BuildCenterlinePanel()
    {
        StackPanel panel = new() { Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };

        if (_lumenMask is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Hinweis: Für die Centerline wird die Lumen-Maske aus Schritt 1 benötigt.",
                FontSize = 11,
                Foreground = Brushes.Orange,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ── Segment hinzufügen ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock { Text = "Segment hinzufügen", FontWeight = FontWeight.SemiBold });
        _presetCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = VascularCenterlineHelper.Presets
                .Select(p => new ComboBoxItem { Content = p.DisplayName })
                .ToList(),
        };
        _presetCombo.SelectedIndex = 0;
        panel.Children.Add(_presetCombo);

        Button addButton = new()
        {
            Content = "+ Segment",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        addButton.Click += OnAddSegmentClick;
        panel.Children.Add(addButton);

        // ── Segmentliste ────────────────────────────────────────────────────────
        _segmentList = new ListBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _segmentList.SelectionChanged += OnSegmentSelectionChanged;
        panel.Children.Add(_segmentList);
        RebuildSegmentList();

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ── Seed-Erfassung ──────────────────────────────────────────────────────
        _armSeedButtonCl = new Button
        {
            Content = "Seed-Erfassung im MPR",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _armSeedButtonCl.Click += OnArmCenterlineSeedClick;
        panel.Children.Add(_armSeedButtonCl);
        panel.Children.Add(new TextBlock
        {
            Text = "SHIFT=Start · ALT=Ende · CTRL=Guide · ohne = nächster fehlender",
            FontSize = 10,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        });

        _computeButton = new Button
        {
            Content = "Centerline berechnen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _computeButton.Click += OnComputeCenterlineClick;
        panel.Children.Add(_computeButton);

        Button clearSeedsButton = new()
        {
            Content = "Seeds löschen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        clearSeedsButton.Click += OnClearSeedsClick;
        panel.Children.Add(clearSeedsButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ── Stations-Scrubber für den Orthogonalschnitt ─────────────────────────
        panel.Children.Add(new TextBlock { Text = "Station (Orthogonalschnitt)", FontWeight = FontWeight.SemiBold });
        _stationSlider = new Slider { Minimum = 0, Maximum = 0, IsEnabled = false };
        _stationSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && _stationSlider is not null)
            {
                int index = (int)Math.Round(_stationSlider.Value);
                if (index != _csStationIndex)
                {
                    _csStationIndex = index;
                    ScheduleCrossSectionRender();
                    SyncChartStationFromSlider(index);
                }
            }
        };
        panel.Children.Add(_stationSlider);

        // ── Fortschritt + Statuskarte ───────────────────────────────────────────
        _clProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false };
        _clProgressText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_clProgressBar);
        panel.Children.Add(_clProgressText);

        _clStatusCardText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.Parse("#1E2430")),
            Padding = new Thickness(8),
        };
        panel.Children.Add(_clStatusCardText);

        return panel;
    }

    // ── Segmentverwaltung ──────────────────────────────────────────────────────
    private void OnAddSegmentClick(object? sender, RoutedEventArgs e)
    {
        int presetIndex = _presetCombo?.SelectedIndex ?? 0;
        if (presetIndex < 0 || presetIndex >= VascularCenterlineHelper.Presets.Length)
        {
            presetIndex = 0;
        }

        VascularCenterlineHelper.VesselPreset preset = VascularCenterlineHelper.Presets[presetIndex];

        CenterlineSegmentEntry entry = new()
        {
            SeedSet = new CenterlineSeedSet { Label = preset.DisplayName },
            PresetLabel = preset.Label,
            DisplayName = preset.DisplayName,
            ParentLabel = preset.ParentLabel,
        };
        _centerlineSegments.Add(entry);
        _activeCenterlineSegmentIndex = _centerlineSegments.Count - 1;

        RebuildSegmentList();
        SelectSegmentInList(_activeCenterlineSegmentIndex);
        UpdateCenterlineSeedButtonLabel();
        ResetStationSlider();
        RefreshCenterlineStatus();
    }

    private void OnSegmentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_segmentList is null)
        {
            return;
        }

        int index = _segmentList.SelectedIndex;
        if (index < 0 || index >= _centerlineSegments.Count)
        {
            return;
        }

        _activeCenterlineSegmentIndex = index;
        _clSeedCaptureArmed = false;
        UpdateCenterlineSeedButtonLabel();
        ResetStationSlider();
        RefreshCenterlineStatus();
        ApplyCenterlineOverlays();
        ScheduleCprRender();
        ScheduleCrossSectionRender();
        RefreshDiameterChart();
    }

    private void RebuildSegmentList()
    {
        if (_segmentList is null)
        {
            return;
        }

        _segmentList.Items.Clear();
        foreach (CenterlineSegmentEntry entry in _centerlineSegments)
        {
            string state = entry.Path?.HasRenderablePath == true ? "✓" : "·";
            _segmentList.Items.Add(new ListBoxItem
            {
                Content = $"{state} {entry.DisplayName} ({entry.SeedSet.SeedCount} Seeds)",
            });
        }
    }

    private void SelectSegmentInList(int index)
    {
        if (_segmentList is not null && index >= 0 && index < _segmentList.Items.Count)
        {
            _segmentList.SelectedIndex = index;
        }
    }

    private CenterlineSegmentEntry? ActiveSegment =>
        _activeCenterlineSegmentIndex >= 0 && _activeCenterlineSegmentIndex < _centerlineSegments.Count
            ? _centerlineSegments[_activeCenterlineSegmentIndex]
            : null;

    // ── Seed-Erfassung ─────────────────────────────────────────────────────────
    private void WireCenterlineSeedCapture()
    {
        if (_clSeedHandlerWired || _mprPanel is null)
        {
            return;
        }

        _mprPanel.ImagePointPressed += OnCenterlineMprPointPressed;
        _clSeedHandlerWired = true;
    }

    private void OnArmCenterlineSeedClick(object? sender, RoutedEventArgs e)
    {
        _clSeedCaptureArmed = !_clSeedCaptureArmed;
        UpdateCenterlineSeedButtonLabel();
    }

    private void OnCenterlineMprPointPressed(DicomImagePointerInfo info)
    {
        if (!_clSeedCaptureArmed || _currentStep != 1)
        {
            return;
        }

        if (_mprPanel?.SpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return;
        }

        CenterlineSegmentEntry? entry = ActiveSegment;
        if (entry is null)
        {
            UpdateCenterlineStatus("Bitte zuerst ein Segment hinzufügen.");
            return;
        }

        Vector3D patientPoint = metadata.PatientPointFromPixel(info.ImagePoint);
        CenterlineSeedKind kind = VascularCenterlineHelper.ResolveSeedKind(
            entry.SeedSet,
            shift: info.Modifiers.HasFlag(KeyModifiers.Shift),
            alt: info.Modifiers.HasFlag(KeyModifiers.Alt),
            ctrl: info.Modifiers.HasFlag(KeyModifiers.Control));

        CenterlineSeed seed = new()
        {
            Kind = kind,
            PatientPoint = patientPoint,
            SeriesInstanceUid = _volume?.SeriesInstanceUid ?? string.Empty,
            SopInstanceUid = metadata.SopInstanceUid ?? string.Empty,
        };

        entry.SeedSet = entry.SeedSet.UpsertSeed(seed);
        RebuildSegmentList();
        SelectSegmentInList(_activeCenterlineSegmentIndex);
        UpdateCenterlineSeedButtonLabel();
        RefreshCenterlineStatus();
    }

    private void OnClearSeedsClick(object? sender, RoutedEventArgs e)
    {
        CenterlineSegmentEntry? entry = ActiveSegment;
        if (entry is null)
        {
            return;
        }

        entry.SeedSet = new CenterlineSeedSet { Label = entry.SeedSet.Label };
        entry.Path = null;
        RebuildSegmentList();
        SelectSegmentInList(_activeCenterlineSegmentIndex);
        UpdateCenterlineSeedButtonLabel();
        ResetStationSlider();
        RefreshCenterlineStatus();
        ApplyCenterlineOverlays();
    }

    // ── Berechnung ─────────────────────────────────────────────────────────────
    private async void OnComputeCenterlineClick(object? sender, RoutedEventArgs e)
    {
        if (_volume is null || _centerlineRunning)
        {
            return;
        }

        CenterlineSegmentEntry? entry = ActiveSegment;
        if (entry is null)
        {
            UpdateCenterlineStatus("Bitte zuerst ein Segment hinzufügen.");
            return;
        }

        if (_lumenMask is null)
        {
            UpdateCenterlineStatus("Bitte zuerst in Schritt 1 das Lumen segmentieren.");
            return;
        }

        if (!entry.SeedSet.HasRequiredEndpoints)
        {
            UpdateCenterlineStatus("Start- und End-Seed erforderlich (SHIFT bzw. ALT im MPR).");
            return;
        }

        _centerlineRunning = true;
        _centerlineCts = new CancellationTokenSource();
        SetCenterlineRunningUi(true, $"Berechne {entry.DisplayName}…");

        SegmentationMask3D mask = _lumenMask;
        CenterlineSeedSet seedSet = entry.SeedSet;
        CancellationToken ct = _centerlineCts.Token;

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            CenterlineExtractionResult result = await Task.Run(
                () => _centerlineService.Extract(mask, seedSet, ct), ct);
            stopwatch.Stop();
            RecordVascularPerformanceMetric("centerline-calculation", stopwatch.Elapsed.TotalMilliseconds);

            SetCenterlineRunningUi(false, string.Empty);

            if (!result.Succeeded || result.Path is not CenterlinePath computed)
            {
                UpdateCenterlineStatus(result.Summary);
                return;
            }

            entry.Path = computed with
            {
                SegmentationMaskId = mask.Id,
                Kind = CenterlinePathKind.Computed,
                Status = CenterlineComputationStatus.Success,
            };

            RebuildVesselTree();
            RebuildSegmentList();
            SelectSegmentInList(_activeCenterlineSegmentIndex);
            ResetStationSlider();
            ApplyCenterlineOverlays();
            ScheduleCprRender();
            ScheduleCrossSectionRender();
            RefreshDiameterChart();
            RefreshCenterlineStatus();
            PushWorkspaceSnapshot();
        }
        catch (OperationCanceledException)
        {
            SetCenterlineRunningUi(false, string.Empty);
            UpdateCenterlineStatus("Centerline-Berechnung abgebrochen.");
        }
        catch (Exception ex)
        {
            SetCenterlineRunningUi(false, string.Empty);
            UpdateCenterlineStatus($"Fehler bei der Centerline-Berechnung: {ex.Message}");
        }
        finally
        {
            _centerlineCts?.Dispose();
            _centerlineCts = null;
        }
    }

    private void RebuildVesselTree()
    {
        VesselTree tree = new();
        // Aorta (Wurzel) zuerst, dann die geparenten Äste.
        foreach (CenterlineSegmentEntry entry in _centerlineSegments
                     .Where(s => s.Path?.HasRenderablePath == true)
                     .OrderBy(s => s.ParentLabel is null ? 0 : 1))
        {
            VesselSegment segment = new()
            {
                Label = entry.PresetLabel,
                DisplayName = entry.DisplayName,
                Path = entry.Path!,
            };
            tree = VesselTreeBuilder.AttachBranch(tree, segment, entry.ParentLabel);
        }

        _vesselTree = tree;
    }

    // ── Overlay in DVR + MPR ───────────────────────────────────────────────────
    private void ApplyCenterlineOverlays()
    {
        List<DicomViewPanel.CenterlineOverlay> overlays = [];
        foreach (CenterlineSegmentEntry entry in _centerlineSegments)
        {
            if (entry.Path?.HasRenderablePath != true)
            {
                continue;
            }

            bool selected = ReferenceEquals(entry, ActiveSegment);
            overlays.Add(new DicomViewPanel.CenterlineOverlay(
                entry.SeedSet.Id, entry.Path, entry.SeedSet.GetOrderedSeeds(), selected));
        }

        foreach (DicomViewPanel? panel in new[] { _dvrPanel, _mprPanel })
        {
            panel?.SetCenterlineOverlays(overlays);
        }
    }

    // ── CPR-Viewport ───────────────────────────────────────────────────────────
    private void EnsureCprViewport()
    {
        if (_cprImage is not null || _volume is null)
        {
            return;
        }

        _cprImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CprHost.Content = _cprImage;
    }

    private void ScheduleCprRender()
    {
        if (_volume is null || _cprImage is null)
        {
            return;
        }

        CenterlinePath? path = ActiveSegment?.Path;
        if (path?.HasRenderablePath != true)
        {
            _cprImage.Source = null;
            return;
        }

        _cprRenderCts?.Cancel();
        _cprRenderCts?.Dispose();
        CancellationTokenSource cts = new();
        _cprRenderCts = cts;
        int version = ++_cprRenderVersion;
        SeriesVolume volume = _volume;

        _ = RenderCprAsync(path, volume, version, cts.Token);
    }

    private async Task RenderCprAsync(CenterlinePath path, SeriesVolume volume, int version, CancellationToken ct)
    {
        CurvedMprRenderResult result;
        try
        {
            result = await Task.Run(
                () => CenterlineCurvedMprRenderer.Render(
                    volume, path, CprFieldOfViewMm, CprImageHeight, CprSlabThicknessMm, 0.0, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _cprRenderVersion)
                {
                    UpdateCenterlineStatus($"CPR-rendering fehlgeschlagen: {ex.Message}");
                }
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _cprRenderVersion || _cprImage is null)
            {
                return;
            }

            RenderIntoBitmap(_cprImage, ref _cprBitmap, ref _cprRenderBuffer, _cprLut,
                result.Pixels, result.Width, result.Height);
        });
    }

    // ── Orthogonalschnitt-Viewport ─────────────────────────────────────────────
    private void EnsureCrossSectionViewport()
    {
        if (_csImage is not null || _volume is null)
        {
            return;
        }

        _csImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CrossSectionHost.Content = _csImage;
    }

    private void ScheduleCrossSectionRender()
    {
        if (_volume is null || _csImage is null)
        {
            return;
        }

        CenterlinePath? path = ActiveSegment?.Path;
        if (path?.HasRenderablePath != true)
        {
            _csImage.Source = null;
            return;
        }

        _csRenderCts?.Cancel();
        _csRenderCts?.Dispose();
        CancellationTokenSource cts = new();
        _csRenderCts = cts;
        int version = ++_csRenderVersion;
        SeriesVolume volume = _volume;
        int station = Math.Clamp(_csStationIndex, 0, path.Points.Count - 1);

        _ = RenderCrossSectionAsync(path, volume, station, version, cts.Token);
    }

    private async Task RenderCrossSectionAsync(CenterlinePath path, SeriesVolume volume, int station, int version, CancellationToken ct)
    {
        CenterlinePathPoint pathPoint = path.Points[station];
        ReslicedImage resliced;
        try
        {
            resliced = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                CenterlineSampleFrame frame = CenterlineFrameBuilder.GetFrame(volume, path, station, 0.0);
                Vector3D csRow = frame.Binormal.Length > 1e-6 ? frame.Binormal.Normalize() : new Vector3D(1, 0, 0);
                Vector3D csCol = frame.Normal.Length > 1e-6 ? frame.Normal.Normalize() : new Vector3D(0, 1, 0);
                double pixelSpacing = CrossSectionFieldOfViewMm / CrossSectionImageSize;

                if (VolumeComputeBackend.TryRenderCrossSection(
                        volume, pathPoint.PatientPoint, csRow, csCol,
                        CrossSectionFieldOfViewMm, CrossSectionImageSize, out short[] gpuPixels))
                {
                    return new ReslicedImage
                    {
                        Pixels = gpuPixels,
                        Width = CrossSectionImageSize,
                        Height = CrossSectionImageSize,
                        PixelSpacingX = pixelSpacing,
                        PixelSpacingY = pixelSpacing,
                        RenderBackendLabel = VolumeComputeBackend.CurrentStatus.DisplayName,
                    };
                }

                VolumeSlicePlane plane = new()
                {
                    VolumeCenter = pathPoint.PatientPoint,
                    RowDirection = csRow,
                    ColumnDirection = csCol,
                    Normal = frame.Tangent.Length > 1e-6 ? frame.Tangent.Normalize() : new Vector3D(0, 0, 1),
                    PixelSpacingX = pixelSpacing,
                    PixelSpacingY = pixelSpacing,
                    SliceSpacingMm = 0.5,
                    ScrollStepMm = 0.5,
                    MinOffsetMm = 0,
                    MaxOffsetMm = 0,
                    CurrentOffsetMm = 0,
                    SliceCount = 1,
                    Width = CrossSectionImageSize,
                    Height = CrossSectionImageSize,
                };
                return VolumeReslicer.ExtractSlice(volume, plane);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _csRenderVersion || _csImage is null)
            {
                return;
            }

            RenderIntoBitmap(_csImage, ref _csBitmap, ref _csRenderBuffer, _csLut,
                resliced.Pixels, resliced.Width, resliced.Height);
        });
    }

    // ── Gemeinsame Bitmap-Ausgabe ──────────────────────────────────────────────
    private static void RenderIntoBitmap(
        Image target,
        ref WriteableBitmap? bitmap,
        ref byte[]? buffer,
        byte[] lut,
        short[] pixels,
        int width,
        int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int requiredBytes = width * height * 4;
        buffer ??= new byte[requiredBytes];
        if (buffer.Length < requiredBytes)
        {
            buffer = new byte[requiredBytes];
        }

        (double center, double window) = ComputeAutoWindow(pixels);
        DicomPixelRenderer.RenderRescaled16BitScaled(
            pixels, width, height, center, window, lut, lut, lut,
            isMonochrome1: false, width, height, buffer);

        if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
        {
            bitmap = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using ILockedFramebuffer framebuffer = bitmap.Lock();
        int rowBytes = width * 4;
        if (framebuffer.RowBytes == rowBytes)
        {
            Marshal.Copy(buffer, 0, framebuffer.Address, requiredBytes);
        }
        else
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(buffer, row * rowBytes,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
        }

        target.Source = bitmap;
        target.InvalidateVisual();
    }

    private static (double Center, double Width) ComputeAutoWindow(short[] pixels)
    {
        if (pixels.Length == 0)
        {
            return (0, 1);
        }

        short min = short.MaxValue;
        short max = short.MinValue;
        foreach (short pixel in pixels)
        {
            if (pixel < min)
            {
                min = pixel;
            }

            if (pixel > max)
            {
                max = pixel;
            }
        }

        return ((min + max) * 0.5, Math.Max(1, max - min));
    }

    // ── Status / UI-Helfer ─────────────────────────────────────────────────────
    private void ResetStationSlider()
    {
        CenterlinePath? path = ActiveSegment?.Path;
        _csStationIndex = 0;
        if (_stationSlider is not null)
        {
            int max = path?.Points.Count > 1 ? path.Points.Count - 1 : 0;
            _stationSlider.Maximum = max;
            _stationSlider.Value = 0;
            _stationSlider.IsEnabled = max > 0;
        }
    }

    private void RefreshCenterlineStatus()
    {
        CenterlineSegmentEntry? entry = ActiveSegment;
        if (entry is null)
        {
            UpdateCenterlineStatus("Noch kein Segment. „+ Segment“ fügt ein Preset hinzu.");
            return;
        }

        if (entry.Path?.HasRenderablePath == true)
        {
            string treeInfo = _vesselTree.FindByLabel(entry.PresetLabel)?.ParentLabel is string parent
                ? $" · Parent: {parent}"
                : " · Wurzel";
            UpdateCenterlineStatus(
                VascularCenterlineHelper.Summarize(entry.Path, entry.DisplayName) + treeInfo);
            return;
        }

        string pending = entry.SeedSet.HasRequiredEndpoints
            ? "Bereit zur Berechnung."
            : $"Seed fehlt: {entry.SeedSet.PendingSeedLabel}.";
        UpdateCenterlineStatus($"{entry.DisplayName}: {entry.SeedSet.SeedCount} Seeds · {pending}");
    }

    private void UpdateCenterlineStatus(string text)
    {
        if (_clStatusCardText is not null)
        {
            _clStatusCardText.Text = text;
        }
    }

    private void UpdateCenterlineSeedButtonLabel()
    {
        if (_armSeedButtonCl is not null)
        {
            CenterlineSegmentEntry? entry = ActiveSegment;
            int count = entry?.SeedSet.SeedCount ?? 0;
            string state = _clSeedCaptureArmed ? " (aktiv)" : string.Empty;
            _armSeedButtonCl.Content = $"Seed-Erfassung im MPR{state} · {count} Seeds";
        }
    }

    private void SetCenterlineRunningUi(bool running, string status)
    {
        if (_computeButton is not null)
        {
            _computeButton.IsEnabled = !running;
        }

        if (_clProgressBar is not null)
        {
            _clProgressBar.IsVisible = running;
            _clProgressBar.IsIndeterminate = running;
        }

        if (_clProgressText is not null)
        {
            _clProgressText.IsVisible = running;
            _clProgressText.Text = status;
        }
    }
}
