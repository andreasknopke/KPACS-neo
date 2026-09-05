using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KPACS.SDK.Models;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;
using System.Threading.Tasks;
using Vector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

/// <summary>
/// EVAR-Schritt 1 — Segmentierung (Phase B2). Bietet zwei Wege zur Lumen-Maske:
/// <b>Auto</b> (TotalSegmentator <c>total_fast</c> → Aorta + Iliakalarterien als
/// Vereinigungsmaske) und <b>Manuell</b> (2 Seeds im MPR + HU-Band-Region-Grow über
/// <see cref="LumenSegmentationService"/>). Kalk-/Thrombus-Submasken sind als
/// Checkbox-Overlays ein-/ausblendbar; eine Statuskarte zeigt Maskenvolumen,
/// abgedeckte Z-Slices und Qualitätshinweise.
/// </summary>
public partial class VascularWorkspaceWindow
{
    // Ergebnis-Masken des aktiven Segmentschritts.
    private SegmentationMask3D? _lumenMask;
    private SegmentationMask3D? _calciumMask;
    private SegmentationMask3D? _thrombusMask;
    private bool _showCalcium;
    private bool _showThrombus;

    // Manueller Weg: gesammelte Seeds (Patientenpunkte) und Erfassungsmodus.
    private readonly List<Vector3D> _manualSeeds = [];
    private bool _seedCaptureArmed;
    private bool _seedHandlerWired;

    // HU-Band für den manuellen Region-Grow (Kontrast-CTA-Default).
    private double _lumenHuLower = 150;
    private double _lumenHuUpper = 1500;

    // Laufzeitsteuerung.
    private CancellationTokenSource? _segmentationCts;
    private bool _segmentationRunning;

    // Sidebar-Elemente, die nach dem Bau referenziert werden.
    private TextBlock? _statusCardText;
    private ProgressBar? _progressBar;
    private TextBlock? _progressText;
    private Button? _autoButton;
    private Button? _manualRunButton;
    private Button? _armSeedButton;
    private Slider? _huLowerSlider;
    private Slider? _huUpperSlider;
    private TextBlock? _huLowerLabel;
    private TextBlock? _huUpperLabel;
    private CheckBox? _calciumCheck;
    private CheckBox? _thrombusCheck;

    private void ShowSegmentationStep()
    {
        SidebarHeader.Text = "Segmentierung";
        SidebarBody.Text = _volume is null
            ? "Keine Serie geladen."
            : "Lumen-Maske automatisch (TotalSegmentator) oder manuell (2 Seeds + HU-Band) erzeugen.";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        WireSeedCapture();
        StepPanelHost.Content = BuildSegmentationPanel();
        RefreshSegmentationStatus();
        ApplySegmentationOverlays();
    }

    private Control BuildSegmentationPanel()
    {
        StackPanel panel = new() { Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };

        // ── Auto ──────────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Automatisch (TotalSegmentator)",
            FontWeight = FontWeight.SemiBold,
        });
        _autoButton = new Button
        {
            Content = "Aorta + Iliakal segmentieren",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = AutoSegmentationRunner is not null,
        };
        _autoButton.Click += OnAutoSegmentationClick;
        panel.Children.Add(_autoButton);
        if (AutoSegmentationRunner is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Auto-Segmentierung ist nur aus dem Study Viewer verfügbar.",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ── Manuell ───────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Manuell (2 Seeds + HU-Band)",
            FontWeight = FontWeight.SemiBold,
        });

        _armSeedButton = new Button
        {
            Content = "Seed-Erfassung im MPR",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _armSeedButton.Click += OnArmSeedCaptureClick;
        panel.Children.Add(_armSeedButton);

        Slider lowerSlider = new() { Minimum = -200, Maximum = 600, Value = _lumenHuLower, TickFrequency = 10 };
        Slider upperSlider = new() { Minimum = -200, Maximum = 2500, Value = _lumenHuUpper, TickFrequency = 10 };
        lowerSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _lumenHuLower = Math.Min(lowerSlider.Value, upperSlider.Value - 10);
                UpdateHuLabels();
            }
        };
        upperSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _lumenHuUpper = Math.Max(upperSlider.Value, lowerSlider.Value + 10);
                UpdateHuLabels();
            }
        };
        _huLowerSlider = lowerSlider;
        _huUpperSlider = upperSlider;
        _huLowerLabel = new TextBlock { FontSize = 11 };
        _huUpperLabel = new TextBlock { FontSize = 11 };
        UpdateHuLabels();

        panel.Children.Add(_huLowerLabel);
        panel.Children.Add(_huLowerSlider);
        panel.Children.Add(_huUpperLabel);
        panel.Children.Add(_huUpperSlider);

        _manualRunButton = new Button
        {
            Content = "Lumen berechnen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _manualRunButton.Click += OnManualSegmentationClick;
        panel.Children.Add(_manualRunButton);

        Button clearSeedsButton = new()
        {
            Content = "Seeds löschen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        clearSeedsButton.Click += (_, _) =>
        {
            _manualSeeds.Clear();
            UpdateSeedButtonLabel();
        };
        panel.Children.Add(clearSeedsButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ── Submasken ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock { Text = "Submasken (Overlay)", FontWeight = FontWeight.SemiBold });
        _calciumCheck = new CheckBox { Content = "Kalk", IsChecked = _showCalcium, IsEnabled = _calciumMask is not null };
        _calciumCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                _showCalcium = _calciumCheck.IsChecked == true;
                ApplySegmentationOverlays();
            }
        };
        _thrombusCheck = new CheckBox { Content = "Thrombus", IsChecked = _showThrombus, IsEnabled = _thrombusMask is not null };
        _thrombusCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                _showThrombus = _thrombusCheck.IsChecked == true;
                ApplySegmentationOverlays();
            }
        };
        panel.Children.Add(_calciumCheck);
        panel.Children.Add(_thrombusCheck);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        // ── Fortschritt + Statuskarte ───────────────────────────────────────────
        _progressBar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = false, IsVisible = false };
        _progressText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_progressBar);
        panel.Children.Add(_progressText);

        _statusCardText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.Parse("#1E2430")),
            Padding = new Thickness(8),
        };
        panel.Children.Add(_statusCardText);

        return panel;
    }

    private void UpdateHuLabels()
    {
        if (_huLowerLabel is not null)
        {
            _huLowerLabel.Text = $"HU unten: {_lumenHuLower:F0}";
        }

        if (_huUpperLabel is not null)
        {
            _huUpperLabel.Text = $"HU oben: {_lumenHuUpper:F0}";
        }
    }

    // ── Auto-Pfad ──────────────────────────────────────────────────────────────
    private async void OnAutoSegmentationClick(object? sender, RoutedEventArgs e)
    {
        if (_volume is null || AutoSegmentationRunner is null || _segmentationRunning)
        {
            return;
        }

        _segmentationRunning = true;
        _segmentationCts = new CancellationTokenSource();
        SetRunningUi(true, "Starte Auto-Segmentierung…");

        try
        {
            IProgress<ProgressReport> progress = new Progress<ProgressReport>(p =>
                Dispatcher.UIThread.Post(() => SetProgress(p.PercentComplete, p.StatusMessage)));

            IReadOnlyList<SegmentationMask3D> vascular =
                await AutoSegmentationRunner(_volume, progress, _segmentationCts.Token);

            if (vascular.Count == 0)
            {
                SetRunningUi(false, string.Empty);
                UpdateStatusCard("Auto-Segmentierung lieferte keine Aorta-/Iliakal-Maske.");
                return;
            }

            SegmentationMask3D? union = VascularSegmentationHelper.Union(
                BuildGeometry(_volume), "Lumen (auto)",
                _volume.SeriesInstanceUid, _volume.FrameOfReferenceUid,
                _volume.SeriesInstanceUid, vascular);

            SetRunningUi(false, string.Empty);

            if (union is null)
            {
                UpdateStatusCard("Vereinigung der Gefäßmasken war leer.");
                return;
            }

            _lumenMask = union;
            _calciumMask = null;
            _thrombusMask = null;
            _showCalcium = false;
            _showThrombus = false;
            RebuildSubmaskChecks();
            ApplySegmentationOverlays();
            RefreshSegmentationStatus();
            PushWorkspaceSnapshot();
        }
        catch (OperationCanceledException)
        {
            SetRunningUi(false, string.Empty);
            UpdateStatusCard("Auto-Segmentierung abgebrochen.");
        }
        catch (Exception ex)
        {
            SetRunningUi(false, string.Empty);
            UpdateStatusCard($"Fehler bei der Auto-Segmentierung: {ex.Message}");
        }
        finally
        {
            _segmentationCts?.Dispose();
            _segmentationCts = null;
        }
    }

    // ── Manueller Pfad ─────────────────────────────────────────────────────────
    private void WireSeedCapture()
    {
        if (_seedHandlerWired || _mprPanel is null)
        {
            return;
        }

        _mprPanel.ImagePointPressed += OnMprImagePointPressed;
        _seedHandlerWired = true;
    }

    private void OnArmSeedCaptureClick(object? sender, RoutedEventArgs e)
    {
        _seedCaptureArmed = !_seedCaptureArmed;
        UpdateSeedButtonLabel();
    }

    private void OnMprImagePointPressed(DicomImagePointerInfo info)
    {
        if (!_seedCaptureArmed || _mprPanel?.SpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return;
        }

        Vector3D patientPoint = metadata.PatientPointFromPixel(info.ImagePoint);
        if (_manualSeeds.Count >= 2)
        {
            _manualSeeds.Clear();
        }

        _manualSeeds.Add(patientPoint);
        UpdateSeedButtonLabel();
    }

    private async void OnManualSegmentationClick(object? sender, RoutedEventArgs e)
    {
        if (_volume is null || _segmentationRunning)
        {
            return;
        }

        if (_manualSeeds.Count == 0)
        {
            UpdateStatusCard("Bitte zuerst mindestens einen Seed im MPR setzen.");
            return;
        }

        _segmentationRunning = true;
        _segmentationCts = new CancellationTokenSource();
        SetRunningUi(true, "Berechne Lumen…");

        SeriesVolume volume = _volume;
        VolumeGridGeometry geometry = BuildGeometry(volume);
        IReadOnlyList<Vector3D> seeds = [.. _manualSeeds];
        var options = new LumenSegmentationOptions
        {
            LumenHuLower = _lumenHuLower,
            LumenHuUpper = _lumenHuUpper,
        };
        CancellationToken ct = _segmentationCts.Token;

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            LumenSegmentationResult result = await Task.Run(
                () => new LumenSegmentationService().Segment(volume, geometry, seeds, options, ct), ct);
            stopwatch.Stop();
            RecordVascularPerformanceMetric("lumen-segmentation", stopwatch.Elapsed.TotalMilliseconds);

            SetRunningUi(false, string.Empty);

            if (!result.Succeeded)
            {
                UpdateStatusCard(result.Summary);
                return;
            }

            _lumenMask = result.LumenMask;
            _calciumMask = result.CalciumMask;
            _thrombusMask = result.ThrombusMask;
            _showCalcium = false;
            _showThrombus = false;
            RebuildSubmaskChecks();
            ApplySegmentationOverlays();
            RefreshSegmentationStatus();
            PushWorkspaceSnapshot();
        }
        catch (OperationCanceledException)
        {
            SetRunningUi(false, string.Empty);
            UpdateStatusCard("Segmentierung abgebrochen.");
        }
        catch (Exception ex)
        {
            SetRunningUi(false, string.Empty);
            UpdateStatusCard($"Fehler bei der Segmentierung: {ex.Message}");
        }
        finally
        {
            _segmentationCts?.Dispose();
            _segmentationCts = null;
        }
    }

    // ── Overlay + Status ───────────────────────────────────────────────────────
    private void ApplySegmentationOverlays()
    {
        if (_volume is null)
        {
            return;
        }

        List<DicomViewPanel.SegmentationMaskOverlay> overlays = [];
        AddOverlay(overlays, _lumenMask, (66, 189, 255));   // Sky blue
        if (_showCalcium)
        {
            AddOverlay(overlays, _calciumMask, (255, 193, 37)); // Gold
        }

        if (_showThrombus)
        {
            AddOverlay(overlays, _thrombusMask, (255, 107, 89)); // Coral red
        }

        foreach (DicomViewPanel? panel in new[] { _dvrPanel, _mprPanel })
        {
            panel?.SetSegmentationMaskOverlays(overlays);
        }
    }

    private void AddOverlay(
        List<DicomViewPanel.SegmentationMaskOverlay> overlays,
        SegmentationMask3D? mask,
        (byte R, byte G, byte B) color)
    {
        if (mask is null || _volume is null)
        {
            return;
        }

        if (mask.Geometry.SizeX != _volume.SizeX ||
            mask.Geometry.SizeY != _volume.SizeY ||
            mask.Geometry.SizeZ != _volume.SizeZ)
        {
            return;
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        overlays.Add(new DicomViewPanel.SegmentationMaskOverlay(mask, buffer, color.R, color.G, color.B, 128));
    }

    private void RebuildSubmaskChecks()
    {
        // Panel wurde ggf. neu aufgebaut; Haken aktualisieren, falls vorhanden.
        if (_calciumCheck is not null)
        {
            _calciumCheck.IsEnabled = _calciumMask is not null;
            _calciumCheck.IsChecked = _showCalcium;
        }

        if (_thrombusCheck is not null)
        {
            _thrombusCheck.IsEnabled = _thrombusMask is not null;
            _thrombusCheck.IsChecked = _showThrombus;
        }
    }

    private void RefreshSegmentationStatus()
    {
        if (_lumenMask is null)
        {
            UpdateStatusCard("Noch keine Lumen-Maske. Auto oder manuell erzeugen.");
            return;
        }

        double cm3 = VascularSegmentationHelper.GetVolumeCubicCentimeters(_lumenMask);
        int slices = VascularSegmentationHelper.CountCoveredZSlices(_lumenMask);
        int totalSlices = _volume?.SizeZ ?? slices;

        string quality = slices < totalSlices / 3
            ? "Geringe kraniokaudale Abdeckung — ggf. Guide-Seed an einer Stenose setzen."
            : "Abdeckung unauffällig.";

        UpdateStatusCard(
            $"Lumen: {cm3:F1} cm³ · {slices}/{totalSlices} Slices\n{quality}");
    }

    private void UpdateStatusCard(string text)
    {
        if (_statusCardText is not null)
        {
            _statusCardText.Text = text;
        }
    }

    private void UpdateSeedButtonLabel()
    {
        if (_armSeedButton is not null)
        {
            string state = _seedCaptureArmed ? " (aktiv)" : string.Empty;
            _armSeedButton.Content = $"Seed-Erfassung im MPR{state} · {_manualSeeds.Count}/2";
        }
    }

    private void SetRunningUi(bool running, string status)
    {
        if (_autoButton is not null)
        {
            _autoButton.IsEnabled = !running && AutoSegmentationRunner is not null;
        }

        if (_manualRunButton is not null)
        {
            _manualRunButton.IsEnabled = !running;
        }

        if (_progressBar is not null)
        {
            _progressBar.IsVisible = running;
            _progressBar.IsIndeterminate = running && status.Length > 0;
        }

        if (_progressText is not null)
        {
            _progressText.IsVisible = running;
            _progressText.Text = status;
        }
    }

    private void SetProgress(int percent, string? status)
    {
        if (_progressBar is not null)
        {
            _progressBar.IsIndeterminate = percent < 0;
            if (percent >= 0)
            {
                _progressBar.Value = percent;
            }
        }

        if (_progressText is not null && !string.IsNullOrWhiteSpace(status))
        {
            _progressText.Text = status;
        }
    }

    private static VolumeGridGeometry BuildGeometry(SeriesVolume volume) =>
        new(volume.SizeX, volume.SizeY, volume.SizeZ,
            volume.SpacingX, volume.SpacingY, volume.SpacingZ,
            volume.Origin, volume.RowDirection, volume.ColumnDirection, volume.Normal,
            volume.FrameOfReferenceUid);
}
