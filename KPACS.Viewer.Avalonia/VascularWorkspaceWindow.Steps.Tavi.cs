using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services.StructuralHeart;
using System.Threading.Tasks;
using Vector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

/// <summary>
/// TAVI-Schritt 2 — Double-Oblique / Verkalkung (Phase G2). Zeigt den Annulus
/// en-face (Reformat-Ebene parallel zur Annulus-Best-Fit-Ebene, per Definition exakt
/// en-face) und einen orthogonalen Längsschnitt (3-Kammer-artiger) zur Kontrolle des
/// Niveaus. Ein Slider durchfährt die Ebenen entlang der Annulus-Achse; Klicks im
/// En-face setzen Annulus-Punkte (Snap an Lumen-Kante via lokalem HU-Gradienten ist
/// als Phase-G2-Scope bewusst ausgeschlossen — Punkte werden roh gesetzt).
/// </summary>
public partial class VascularWorkspaceWindow
{
    // Annulus-Punkte (Patientenraum), gesammelte per Klick im En-face.
    private readonly List<Vector3D> _taviAnnulusPoints = [];

    // En-face-Viewport.
    private const double EnFaceFieldOfViewMm = 60.0;
    private const int EnFaceImageSize = 256;
    private readonly byte[] _enFaceLut = Enumerable.Range(0, 256).Select(static v => (byte)v).ToArray();
    private Image? _enFaceImage;
    private WriteableBitmap? _enFaceBitmap;
    private byte[]? _enFaceRenderBuffer;
    private CancellationTokenSource? _enFaceRenderCts;
    private int _enFaceRenderVersion;
    private double _enFaceOffsetMm;

    // Längsschnitt-Viewport.
    private const int LongAxisImageSize = 256;
    private readonly byte[] _longAxisLut = Enumerable.Range(0, 256).Select(static v => (byte)v).ToArray();
    private Image? _longAxisImage;
    private WriteableBitmap? _longAxisBitmap;
    private byte[]? _longAxisRenderBuffer;
    private CancellationTokenSource? _longAxisRenderCts;
    private int _longAxisRenderVersion;

    // Sidebar-Referenzen.
    private Slider? _enFaceSlider;
    private TextBlock? _taviStatusCardText;
    private Button? _clearAnnulusButton;

    private void ShowTaviDoubleObliqueStep()
    {
        SidebarHeader.Text = "Double-Oblique / Verkalkung";
        SidebarBody.Text = _volume is null
            ? "Keine Serie geladen."
            : "Annulus en-face + Längsschnitt. Slider durchfährt die Ebenen; Klick im En-face setzt Annulus-Punkte.";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        EnsureEnFaceViewport();
        EnsureLongAxisViewport();
        StepPanelHost.Content = BuildTaviDoubleObliquePanel();
        ScheduleEnFaceRender();
        ScheduleLongAxisRender();
        RefreshTaviStatus();
    }

    private Control BuildTaviDoubleObliquePanel()
    {
        StackPanel panel = new() { Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Annulus-Punkte: " + _taviAnnulusPoints.Count + " gesetzt. Mindestens 3 für die Best-Fit-Ebene.",
            FontSize = 11,
            Foreground = Brushes.LightSteelBlue,
            TextWrapping = TextWrapping.Wrap,
        });

        _enFaceSlider = new Slider
        {
            Minimum = -DoubleObliqueHelper.DefaultOffsetRangeMm,
            Maximum = DoubleObliqueHelper.DefaultOffsetRangeMm,
            Value = 0,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
        };
        _enFaceSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && _enFaceSlider is not null)
            {
                _enFaceOffsetMm = _enFaceSlider.Value;
                ScheduleEnFaceRender();
            }
        };
        panel.Children.Add(new TextBlock { Text = "En-face Ebene (mm entlang Annulus-Achse)", FontSize = 11 });
        panel.Children.Add(_enFaceSlider);

        _clearAnnulusButton = new Button { Content = "Annulus-Punkte loeschen" };
        _clearAnnulusButton.Click += OnClearAnnulusClick;
        panel.Children.Add(_clearAnnulusButton);

        _taviStatusCardText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_taviStatusCardText);

        return panel;
    }

    private void EnsureEnFaceViewport()
    {
        if (_enFaceImage is not null || _volume is null)
        {
            return;
        }

        _enFaceImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _enFaceImage.PointerPressed += OnEnFacePointerPressed;
        CprHost.Content = _enFaceImage;
    }

    private void EnsureLongAxisViewport()
    {
        if (_longAxisImage is not null || _volume is null)
        {
            return;
        }

        _longAxisImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CrossSectionHost.Content = _longAxisImage;
    }

    private void OnClearAnnulusClick(object? sender, RoutedEventArgs e)
    {
        _taviAnnulusPoints.Clear();
        RefreshTaviStatus();
    }

    private void OnEnFacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_volume is null || _enFaceImage is null)
        {
            return;
        }

        Point pos = e.GetPosition(_enFaceImage);
        double pixelSpacing = EnFaceFieldOfViewMm / EnFaceImageSize;
        Vector3D pt = DoubleObliqueHelper.ClickToPatientPoint(
            CurrentAnnulusPlane(), _enFaceOffsetMm, pos.X, pos.Y, pixelSpacing, pixelSpacing);
        _taviAnnulusPoints.Add(pt);
        RefreshTaviStatus();
    }

    private AnnulusPlane CurrentAnnulusPlane()
    {
        // Phase G2: ohne G1-Ergebnis wird eine Default-Ebene (XY, Z-Achse) verwendet,
        // damit der En-face-Viewport sofort funktioniert. Sobald G1-Annulus-Punkte
        // vorhandene sind, wird hier die Best-Fit-Ebene eingesetzt.
        if (_taviAnnulusPoints.Count >= AnnulusAnalysisService.MinPointsForPlane)
        {
            var points = _taviAnnulusPoints
                .Select(p => new AnnulusPoint { PatientPoint = p })
                .ToList();
            return AnnulusAnalysisService.FitPlane(points);
        }

        return new AnnulusPlane { Center = default, Normal = new Vector3D(0, 0, 1) };
    }

    private void ScheduleEnFaceRender()
    {
        if (_volume is null || _enFaceImage is null)
        {
            return;
        }

        _enFaceRenderCts?.Cancel();
        _enFaceRenderCts?.Dispose();
        CancellationTokenSource cts = new();
        _enFaceRenderCts = cts;
        int version = ++_enFaceRenderVersion;
        SeriesVolume volume = _volume;
        double offset = _enFaceOffsetMm;

        _ = RenderEnFaceAsync(volume, offset, version, cts.Token);
    }

    private async Task RenderEnFaceAsync(SeriesVolume volume, double offset, int version, CancellationToken ct)
    {
        ReslicedImage resliced;
        try
        {
            resliced = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                double pixelSpacing = EnFaceFieldOfViewMm / EnFaceImageSize;
                VolumeSlicePlane plane = DoubleObliqueHelper.BuildEnFacePlane(
                    CurrentAnnulusPlane(), pixelSpacing, pixelSpacing,
                    -DoubleObliqueHelper.DefaultOffsetRangeMm, DoubleObliqueHelper.DefaultOffsetRangeMm,
                    offset, EnFaceImageSize, EnFaceImageSize);

                if (VolumeComputeBackend.TryRenderObliqueProjection(
                        volume, plane, 0, VolumeProjectionMode.Mpr, out ReslicedImage gpuImage))
                {
                    return gpuImage;
                }

                return VolumeReslicer.ExtractSlice(volume, plane);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _enFaceRenderVersion)
                {
                    UpdateTaviStatus("En-face-rendering fehlgeschlagen: " + ex.Message);
                }
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _enFaceRenderVersion || _enFaceImage is null)
            {
                return;
            }

            RenderIntoBitmap(_enFaceImage, ref _enFaceBitmap, ref _enFaceRenderBuffer, _enFaceLut,
                resliced.Pixels, resliced.Width, resliced.Height);
        });
    }

    private void ScheduleLongAxisRender()
    {
        if (_volume is null || _longAxisImage is null)
        {
            return;
        }

        _longAxisRenderCts?.Cancel();
        _longAxisRenderCts?.Dispose();
        CancellationTokenSource cts = new();
        _longAxisRenderCts = cts;
        int version = ++_longAxisRenderVersion;
        SeriesVolume volume = _volume;

        _ = RenderLongAxisAsync(volume, version, cts.Token);
    }

    private async Task RenderLongAxisAsync(SeriesVolume volume, int version, CancellationToken ct)
    {
        ReslicedImage resliced;
        try
        {
            resliced = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                double pixelSpacing = EnFaceFieldOfViewMm / EnFaceImageSize;
                VolumeSlicePlane plane = DoubleObliqueHelper.BuildLongAxisPlane(
                    CurrentAnnulusPlane(), pixelSpacing, pixelSpacing, LongAxisImageSize, LongAxisImageSize);

                if (VolumeComputeBackend.TryRenderObliqueProjection(
                        volume, plane, 0, VolumeProjectionMode.Mpr, out ReslicedImage gpuImage))
                {
                    return gpuImage;
                }

                return VolumeReslicer.ExtractSlice(volume, plane);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _longAxisRenderVersion)
                {
                    UpdateTaviStatus("Längsschnitt-rendering fehlgeschlagen: " + ex.Message);
                }
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _longAxisRenderVersion || _longAxisImage is null)
            {
                return;
            }

            RenderIntoBitmap(_longAxisImage, ref _longAxisBitmap, ref _longAxisRenderBuffer, _longAxisLut,
                resliced.Pixels, resliced.Width, resliced.Height);
        });
    }

    private void RefreshTaviStatus()
    {
        if (_taviStatusCardText is null)
        {
            return;
        }

        string planeInfo = _taviAnnulusPoints.Count >= AnnulusAnalysisService.MinPointsForPlane
            ? "Best-Fit-Ebene aktiv."
            : "Mindestens 3 Punkte für die Best-Fit-Ebene.";
        _taviStatusCardText.Text = "Annulus-Punkte: " + _taviAnnulusPoints.Count + " · " + planeInfo;
    }

    private void UpdateTaviStatus(string message)
    {
        if (_taviStatusCardText is not null)
        {
            _taviStatusCardText.Text = message;
        }
    }
}
