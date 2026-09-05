using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer;

/// <summary>
/// EVAR-Schritt 3 — Messungen (Phase C3). Setzt die vier Planungs-Marker
/// (Proximal-Neck Anfang/Ende, distale Landing-Zone Anfang/Ende) an der aktuellen
/// Querschnitts-Station, berechnet über <see cref="VascularPlanningMetricsService"/> die
/// Basis-Metriken und ergänzt sie um die erweiterten Werte aus Phase C2/C3
/// (Konizität, Aneurysma-Maximaldurchmesser, Kalk-/Thrombus-Volumen im Neck, iliakaler
/// Zugangsweg je Seite aus <see cref="VascularAccessPathHelper"/>). Ein Live-Panel zeigt
/// alle Werte mit Ampel-Status aus <see cref="VascularExtendedMetricsHelper"/>.
/// </summary>
public partial class VascularWorkspaceWindow
{
    private VascularPlanningBundle _planningBundle = new();
    private readonly IVascularPlanningMetricsService _metricsService = new VascularPlanningMetricsService();

    private TextBlock? _measureStatusCard;
    private Button? _recomputeButton;
    private readonly Dictionary<VascularPlanningMarkerKind, Button> _markerButtons = [];

    private void ShowMeasurementStep()
    {
        SidebarHeader.Text = "Messungen";
        SidebarBody.Text = _volume is null
            ? "Keine Serie geladen."
            : "Marker an der Querschnitts-Station setzen und alle EVAR-Metriken berechnen.";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        StepPanelHost.Content = BuildMeasurementPanel();
        RefreshMeasurementPanel();
    }

    private Control BuildMeasurementPanel()
    {
        StackPanel panel = new() { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Marker (aktuelle Querschnitts-Station)",
            FontWeight = FontWeight.SemiBold,
        });

        _markerButtons.Clear();
        panel.Children.Add(BuildMarkerRow("Neck Anfang", VascularPlanningMarkerKind.ProximalNeckStart));
        panel.Children.Add(BuildMarkerRow("Neck Ende", VascularPlanningMarkerKind.ProximalNeckEnd));
        panel.Children.Add(BuildMarkerRow("Landing Anfang", VascularPlanningMarkerKind.DistalLandingStart));
        panel.Children.Add(BuildMarkerRow("Landing Ende", VascularPlanningMarkerKind.DistalLandingEnd));

        _recomputeButton = new Button
        {
            Content = "Alle Messungen berechnen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = ActiveSegment?.Path?.HasRenderablePath == true && _lumenMask is not null,
        };
        _recomputeButton.Click += OnRecomputeMeasurementsClick;
        panel.Children.Add(_recomputeButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        _measureStatusCard = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.Parse("#1E2430")),
            Padding = new Thickness(8),
        };
        panel.Children.Add(_measureStatusCard);

        return panel;
    }

    private Button BuildMarkerRow(string caption, VascularPlanningMarkerKind kind)
    {
        Button button = new()
        {
            Content = $"{caption}: —",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4),
        };
        button.Click += (_, _) => SetMarkerFromCurrentStation(kind);
        _markerButtons[kind] = button;
        return button;
    }

    /// <summary>
    /// Setzt einen Marker an der aktuellen Querschnitts-Station des aktiven Segments.
    /// </summary>
    private void SetMarkerFromCurrentStation(VascularPlanningMarkerKind kind)
    {
        CenterlinePath? path = ActiveSegment?.Path;
        if (path?.HasRenderablePath != true)
        {
            return;
        }

        int station = Math.Clamp(_csStationIndex, 0, path.Points.Count - 1);
        CenterlinePathPoint point = path.Points[station];
        _planningBundle = _planningBundle.UpsertMarker(new VascularPlanningMarker
        {
            Kind = kind,
            StationIndex = station,
            ArcLengthMm = point.ArcLengthMm,
            PatientPoint = point.PatientPoint,
        });
        RefreshMarkerButtons();
    }

    private void OnRecomputeMeasurementsClick(object? sender, RoutedEventArgs e)
    {
        RecomputeMeasurementMetrics();
        RefreshMeasurementPanel();
    }

    /// <summary>
    /// Berechnet die Basis-Metriken über den Service und ergänzt die erweiterten Werte,
    /// die nur auf Workspace-Ebene aus Vessel-Tree + Submasken ableitbar sind.
    /// </summary>
    private void RecomputeMeasurementMetrics()
    {
        CenterlinePath? path = ActiveSegment?.Path;
        if (path?.HasRenderablePath != true || _lumenMask is null || _volume is null)
        {
            _planningBundle = _planningBundle.WithMetrics(null, path?.Id, _lumenMask?.Id);
            return;
        }

        VascularPlanningMetrics baseMetrics = _metricsService.Compute(_volume, _lumenMask, path, _planningBundle);

        // Phase C3: iliakaler Zugangsweg je Seite aus dem Vessel-Tree + Submasken.
        List<VascularAccessPathMetrics> accessPaths = BuildAccessPaths();

        // Phase C3: Kalk-/Thrombus-Volumen im Neck-Span (nur wenn Submasken vorhanden).
        double? neckCalcium = null;
        double? neckThrombus = null;
        VascularPlanningMarker? neckStart = _planningBundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckStart);
        VascularPlanningMarker? neckEnd = _planningBundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckEnd);
        if (neckStart is not null && neckEnd is not null)
        {
            SegmentationMaskBuffer? calciumBuffer = _calciumMask is null
                ? null
                : SegmentationMaskBuffer.FromStorage(_calciumMask.Geometry, _calciumMask.Storage);
            SegmentationMaskBuffer? thrombusBuffer = _thrombusMask is null
                ? null
                : SegmentationMaskBuffer.FromStorage(_thrombusMask.Geometry, _thrombusMask.Storage);
            neckCalcium = VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(
                calciumBuffer, path, neckStart.ArcLengthMm, neckEnd.ArcLengthMm);
            neckThrombus = VascularAccessPathHelper.ComputeSubMaskVolumeCm3WithinSpan(
                thrombusBuffer, path, neckStart.ArcLengthMm, neckEnd.ArcLengthMm);
        }

        VascularPlanningMetrics extended = baseMetrics with
        {
            AccessPaths = accessPaths,
            NeckCalciumVolumeCm3 = neckCalcium,
            NeckThrombusVolumeCm3 = neckThrombus,
        };

        _planningBundle = _planningBundle.WithMetrics(extended, path.Id, _lumenMask.Id);
        PushWorkspaceSnapshot();
    }

    private List<VascularAccessPathMetrics> BuildAccessPaths()
    {
        List<VascularAccessPathMetrics> result = [];
        if (_lumenMask is null)
        {
            return result;
        }

        SegmentationMaskBuffer lumen = SegmentationMaskBuffer.FromStorage(_lumenMask.Geometry, _lumenMask.Storage);
        SegmentationMaskBuffer? calcium = _calciumMask is null
            ? null
            : SegmentationMaskBuffer.FromStorage(_calciumMask.Geometry, _calciumMask.Storage);

        foreach (VesselSegment segment in _vesselTree.Segments)
        {
            if (!IsIliacAccess(segment.Label))
            {
                continue;
            }

            result.Add(VascularAccessPathHelper.BuildAccessPath(
                SideLabel(segment.Label), segment.Path, lumen, calcium));
        }

        return result;
    }

    private static bool IsIliacAccess(string label) =>
        label.Contains("iliac", StringComparison.OrdinalIgnoreCase);

    private static string SideLabel(string label) =>
        label.Contains("left", StringComparison.OrdinalIgnoreCase) ? "Links"
        : label.Contains("right", StringComparison.OrdinalIgnoreCase) ? "Rechts"
        : label;

    private void RefreshMarkerButtons()
    {
        foreach ((VascularPlanningMarkerKind kind, Button button) in _markerButtons)
        {
            VascularPlanningMarker? marker = _planningBundle.GetMarker(kind);
            string caption = MarkerCaption(kind);
            button.Content = marker is null
                ? $"{caption}: —"
                : $"{caption}: {marker.ArcLengthMm:F0} mm (Station {marker.StationIndex})";
        }
    }

    private static string MarkerCaption(VascularPlanningMarkerKind kind) => kind switch
    {
        VascularPlanningMarkerKind.ProximalNeckStart => "Neck Anfang",
        VascularPlanningMarkerKind.ProximalNeckEnd => "Neck Ende",
        VascularPlanningMarkerKind.DistalLandingStart => "Landing Anfang",
        VascularPlanningMarkerKind.DistalLandingEnd => "Landing Ende",
        _ => kind.ToString(),
    };

    private void RefreshMeasurementPanel()
    {
        RefreshMarkerButtons();
        if (_measureStatusCard is null)
        {
            return;
        }

        VascularPlanningMetrics? m = _planningBundle.Metrics;
        if (m is null)
        {
            _measureStatusCard.Text = ActiveSegment?.Path?.HasRenderablePath == true && _lumenMask is not null
                ? "Bereit zum Berechnen. Marker setzen und „Alle Messungen berechnen“."
                : "Centerline (Schritt 2) und Lumen-Maske (Schritt 1) erforderlich.";
            return;
        }

        List<string> lines = [];

        if (m.ProximalNeck?.LengthMm is double neckLen)
        {
            lines.Add(StatusLine("Neck-Länge", $"{neckLen:F1} mm",
                VascularExtendedMetricsHelper.ClassifyNeckLength(neckLen)));
        }

        if (m.ProximalNeck?.MeanEquivalentDiameterMm is double neckDia)
        {
            lines.Add(StatusLine("Neck-Øeq", $"{neckDia:F1} mm",
                VascularExtendedMetricsHelper.ClassifyNeckDiameter(neckDia)));
        }

        if (m.NeckConicity?.ConicityMmPer10Mm is double conicity)
        {
            lines.Add(StatusLine("Konizität", $"{conicity:F2} mm/10mm", m.NeckConicity.Status));
        }

        if (m.NeckAngulationDegrees is double ang)
        {
            lines.Add(StatusLine("Angulation", $"{ang:F0}°",
                VascularExtendedMetricsHelper.ClassifyAngulation(ang)));
        }

        if (m.AneurysmMaxDiameterMm is double sacMax)
        {
            lines.Add($"Aneurysma max Øeq: {sacMax:F1} mm");
        }

        if (m.NeckCalciumVolumeCm3 is double neckCa)
        {
            lines.Add($"Neck Kalk: {neckCa:F2} cm³");
        }

        if (m.NeckThrombusVolumeCm3 is double neckTh)
        {
            lines.Add($"Neck Thrombus: {neckTh:F2} cm³");
        }

        if (m.DistalLanding?.MeanEquivalentDiameterMm is double distalDia)
        {
            lines.Add($"Distal-Øeq: {distalDia:F1} mm");
        }

        foreach (VascularAccessPathMetrics ap in m.AccessPaths)
        {
            string dia = ap.MinEquivalentDiameterMm is double d ? $"{d:F1} mm" : "—";
            string tort = ap.Tortuosity is double t ? $"{t:F2}" : "—";
            string calc = ap.CalciumFraction is double c ? $"{c * 100:F0} %" : "—";
            lines.Add(StatusLine($"Zugang {ap.Side}", $"min Ø {dia} · Tort {tort} · Kalk {calc}", ap.Status));
        }

        _measureStatusCard.Text = lines.Count == 0 ? "Keine Metriken." : string.Join("\n", lines);
    }

    private static string StatusLine(string label, string value, VascularMetricStatus status)
    {
        string glyph = status switch
        {
            VascularMetricStatus.Ok => "●",
            VascularMetricStatus.Warning => "▲",
            VascularMetricStatus.Critical => "■",
            _ => "○",
        };
        return $"{glyph} {label}: {value}";
    }
}
