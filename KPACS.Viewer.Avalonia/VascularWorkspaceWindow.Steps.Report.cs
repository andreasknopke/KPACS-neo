using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer;

/// <summary>
/// EVAR-Schritt 5 — Bericht (Phase E). Baut einen strukturierter Bericht mit
/// Messwert-Tabelle, Warnungen und Sizing-Vorschlag aus dem Workspace-Snapshot und
/// exportiert ihn als HTML (eingebettete PNG-Key-Images, Print-to-PDF durch den
/// Anwender) oder JSON (maschinenlesbar). PHI-sicher: keine Patientendaten in
/// Dateinamen oder Inhalten.
/// </summary>
public partial class VascularWorkspaceWindow
{
    private TextBlock? _reportStatusCard;
    private Button? _exportHtmlButton;
    private Button? _exportJsonButton;

    private void ShowReportStep()
    {
        SidebarHeader.Text = "Bericht";
        SidebarBody.Text = "Strukturierter Bericht mit Messwert-Tabelle, Warnungen, " +
            "Sizing-Vorschlag und Key-Images (HTML/JSON-Export).";

        if (_volume is null)
        {
            StepPanelHost.Content = null;
            return;
        }

        StepPanelHost.Content = BuildReportPanel();
        RefreshReportPanel();
    }

    private Control BuildReportPanel()
    {
        StackPanel panel = new() { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Vascular Planning Bericht",
            FontWeight = FontWeight.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Export als HTML (eingebettete Key-Images, Print-to-PDF) oder JSON " +
                   "(maschinenlesbar). Keine Patientendaten in Dateinamen.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        });

        _exportHtmlButton = new Button
        {
            Content = "HTML exportieren",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _exportHtmlButton.Click += OnExportHtmlClick;
        panel.Children.Add(_exportHtmlButton);

        _exportJsonButton = new Button
        {
            Content = "JSON exportieren",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _exportJsonButton.Click += OnExportJsonClick;
        panel.Children.Add(_exportJsonButton);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        _reportStatusCard = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.Parse("#1E2430")),
            Padding = new Thickness(8),
        };
        panel.Children.Add(_reportStatusCard);

        return panel;
    }

    private void RefreshReportPanel()
    {
        if (_reportStatusCard is null)
        {
            return;
        }

        VascularWorkspaceSnapshot snapshot = BuildCurrentSnapshot();
        List<string> lines = [];

        VascularPlanningMetrics? metrics = snapshot.PlanningBundle?.Metrics;
        if (metrics is null)
        {
            lines.Add("Keine Messungen vorhanden — bitte zuerst Schritt 3 berechnen.");
        }
        else
        {
            if (metrics.ProximalNeck?.LengthMm is double neckLength)
            {
                string diameter = metrics.ProximalNeck.MeanEquivalentDiameterMm is double neckDia
                    ? $" · Øeq {neckDia:F1} mm"
                    : string.Empty;
                lines.Add($"Neck: {neckLength:F1} mm{diameter}");
            }

            if (metrics.NeckAngulationDegrees is double angulation)
            {
                lines.Add($"Angulation: {angulation:F1}°");
            }

            if (metrics.AccessPaths.Count > 0)
            {
                lines.Add($"Zugangswege: {metrics.AccessPaths.Count} Seite(n)");
            }
        }

        if (snapshot.EndograftPlan is EndograftPlan plan)
        {
            lines.Add(string.Empty);
            lines.Add($"Sizing: {plan.Components.Count} Komponenten, {plan.Warnings.Count} Warnungen");
            if (plan.RecommendedProximalDiameterMm is double prox)
            {
                lines.Add($"Empfohlener proximal Ø: {prox:F1} mm");
            }
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("Keine Sizing-Vorschlag — bitte zuerst Schritt 4 berechnen.");
        }

        _reportStatusCard.Text = string.Join("\n", lines);
    }

    private async void OnExportHtmlClick(object? sender, RoutedEventArgs e)
    {
        await ExportReportAsync(html: true);
    }

    private async void OnExportJsonClick(object? sender, RoutedEventArgs e)
    {
        await ExportReportAsync(html: false);
    }

    private async Task ExportReportAsync(bool html)
    {
        if (_volume is null)
        {
            return;
        }

        VascularWorkspaceSnapshot snapshot = BuildCurrentSnapshot();
        string content = html
            ? VascularReportExportService.BuildHtml(snapshot, BuildKeyImages())
            : VascularReportExportService.BuildJson(snapshot, BuildKeyImages());

        string extension = html ? ".html" : ".json";
        string defaultName = $"vascular-report-{SanitizeReportComponent(_volume.SeriesInstanceUid)}{extension}";

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = html ? "Vascular-Bericht als HTML exportieren" : "Vascular-Bericht als JSON exportieren",
            SuggestedFileName = defaultName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(html ? "HTML-Datei" : "JSON-Datei") { Patterns = [html ? "*.html" : "*.json"] }],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            await using StreamWriter writer = new(stream);
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            UpdateReportStatus($"Exportiert: {file.Name}");
        }
        catch (Exception ex)
        {
            UpdateReportStatus($"Export fehlgeschlagen: {ex.Message}");
        }
    }

    private void UpdateReportStatus(string message)
    {
        if (_reportStatusCard is not null)
        {
            _reportStatusCard.Text = message;
        }
    }

    /// <summary>
    /// Baut die Key-Images als base64-PNG-Daten. Der Diameter-Chart wird als PNG
    /// gerendert; weitere Viewports können später ergänzt werden.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildKeyImages()
    {
        Dictionary<string, string> images = [];
        string? chartPng = CaptureDiameterChartPng();
        if (!string.IsNullOrWhiteSpace(chartPng))
        {
            images["diameter-chart"] = chartPng;
        }

        return images;
    }

    private string? CaptureDiameterChartPng()
    {
        if (_diameterChart is null)
        {
            return null;
        }

        try
        {
            // Der DiameterChartPanel wrappt einen AvaPlot; rendern als PNG über ScottPlot.
            // (ScottPlot 5.1.58: AvaPlot.SavePng oder Plot.SavePng.)
            return _diameterChart.ExportPngBase64();
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeReportComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "report";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Length > 40 ? sanitized[..40] : sanitized;
    }
}
