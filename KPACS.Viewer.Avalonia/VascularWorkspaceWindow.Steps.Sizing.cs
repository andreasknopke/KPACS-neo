using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer;

/// <summary>
/// EVAR-Schritt 4 — Sizing (Phase D). Baut den <see cref="EndograftSizingInput"/> aus dem
/// Planungs-Bundle (Neck-/Landing-Marker, Metriken) und dem Vessel-Tree (Iliakal-Zugangswege),
/// berechnet über <see cref="EndograftSizingService"/> die empfohlener Graft-Durchmesser,
/// die Komponenten (Aorten-Body + Iliakal-Limbs) und die strukturierte Warnungen, und zeigt
/// sie im Sidebar. Die Graft-Bänder werden zusätzlich als halbetransparente Flächen im
/// Diameter-Chart gezeichnet (Phase D-Visualisierung).
/// </summary>
public partial class VascularWorkspaceWindow
{
    private EndograftPlan? _endograftPlan;
    private TextBlock? _sizingStatusCard;
    private Button? _sizingButton;

    private void ShowSizingStep()
    {
        SidebarHeader.Text = "Sizing";
        SidebarBody.Text = _volume is null
            ? "Keine Serie geladen."
            : "Endograft-Sizing mit Oversizing-Empfehlung und Warnungs-Engine.";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        StepPanelHost.Content = BuildSizingPanel();
        RefreshSizingPanel();
    }

    private Control BuildSizingPanel()
    {
        StackPanel panel = new() { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Endograft-Sizing (herstellerneutral)",
            FontWeight = FontWeight.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Oversizing: proximal 15 % (10–20 %), distal 12 % (10–15 %). "
                 + "Aorten-Ende 2 mm proximal der niedrigsten Renale.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        });

        _sizingButton = new Button
        {
            Content = "Sizing berechnen",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = _planningBundle.Metrics is not null,
        };
        _sizingButton.Click += OnSizingClick;
        panel.Children.Add(_sizingButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        _sizingStatusCard = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.Parse("#1E2430")),
            Padding = new Thickness(8),
        };
        panel.Children.Add(_sizingStatusCard);

        return panel;
    }

    private void OnSizingClick(object? sender, RoutedEventArgs e)
    {
        RecomputeSizing();
        RefreshSizingPanel();
        RefreshDiameterChart();
    }

    /// <summary>
    /// Baut den Sizing-Input aus dem Planungs-Bundle + Vessel-Tree und berechnet den Plan.
    /// </summary>
    private void RecomputeSizing()
    {
        VascularPlanningMetrics? m = _planningBundle.Metrics;
        if (m is null)
        {
            _endograftPlan = null;
            return;
        }

        VascularPlanningMarker? neckStart = _planningBundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckStart);
        VascularPlanningMarker? neckEnd = _planningBundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckEnd);
        VascularPlanningMarker? landingStart = _planningBundle.GetMarker(VascularPlanningMarkerKind.DistalLandingStart);
        VascularPlanningMarker? landingEnd = _planningBundle.GetMarker(VascularPlanningMarkerKind.DistalLandingEnd);

        // Aorten-Ende: 2 mm proximal der niedrigsten Renale (hier: Neck-Ende als Referenz,
        // da Ostien noch nicht erfasst — bewusst konservativ).
        double? aorticEnd = neckEnd?.ArcLengthMm is double neckEndArc
            ? neckEndArc - EndograftSizingService.Defaults.AorticEndProximalToLowestRenalMm
            : null;

        EndograftSizingInput input = new()
        {
            NeckDiameterMm = m.ProximalNeck?.MeanEquivalentDiameterMm,
            NeckLengthMm = m.ProximalNeck?.LengthMm,
            NeckConicityMmPer10Mm = m.NeckConicity?.ConicityMmPer10Mm,
            NeckAngulationDegrees = m.NeckAngulationDegrees,
            DistalLandingDiameterMm = m.DistalLanding?.MeanEquivalentDiameterMm,
            ProximalNeckStartStationMm = neckStart?.ArcLengthMm,
            AorticEndStationMm = aorticEnd,
            DistalLandingStartStationMm = landingStart?.ArcLengthMm,
            DistalLandingEndStationMm = landingEnd?.ArcLengthMm,
            AccessPaths = m.AccessPaths,
        };

        _endograftPlan = EndograftSizingService.Size(input);
        PushWorkspaceSnapshot();
    }

    private void RefreshSizingPanel()
    {
        if (_sizingStatusCard is null)
        {
            return;
        }

        if (_sizingButton is not null)
        {
            _sizingButton.IsEnabled = _planningBundle.Metrics is not null;
        }

        EndograftPlan? plan = _endograftPlan;
        if (plan is null)
        {
            _sizingStatusCard.Text = _planningBundle.Metrics is null
                ? "Zuerst in Schritt 3 die Messungen berechnen."
                : "Bereit zum Sizing. „Sizing berechnen“ drücken.";
            return;
        }

        List<string> lines = [];

        if (plan.RecommendedProximalDiameterMm is double prox)
        {
            lines.Add($"Empfohlener proximal Ø: {prox:F1} mm (Neck {plan.NeckDiameterMm:F1} mm, +{plan.ProximalOversizing:P0})");
        }

        if (plan.RecommendedDistalDiameterMm is double dist)
        {
            lines.Add($"Empfohlener distal Ø: {dist:F1} mm (+{plan.DistalOversizing:P0})");
        }

        if (plan.Components.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Komponenten:");
            foreach (GraftComponent component in plan.Components)
            {
                lines.Add($"  • {component.Name}: {component.ProximalDiameterMm:F1}→{component.DistalDiameterMm:F1} mm, "
                        + $"{component.LengthMm:F0} mm ({component.StartStationMm:F0}–{component.EndStationMm:F0} mm)");
            }
        }

        if (plan.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnungen:");
            foreach (EndograftWarning warning in plan.Warnings)
            {
                string glyph = warning.Severity switch
                {
                    EndograftWarningSeverity.Critical => "■",
                    EndograftWarningSeverity.Warning => "▲",
                    _ => "○",
                };
                lines.Add($"  {glyph} {warning.Message}");
            }
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("● Keine Warnungen — Plan unauffällig.");
        }

        _sizingStatusCard.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// Zeichnet die Graft-Bänder als halbetransparente Flächen im Diameter-Chart.
    /// </summary>
    private void ApplyGraftBandsToChart()
    {
        if (_diameterChart is null)
        {
            return;
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _diameterChart.SetGraftBands(_endograftPlan?.Components ?? []);
        stopwatch.Stop();
        RecordVascularPerformanceMetric("graft-overlay-rebuild", stopwatch.Elapsed.TotalMilliseconds);
    }
}
