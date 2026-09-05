using System.Globalization;
using System.Text;
using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Phase E: builds a structured vascular planning report (HTML for print-to-PDF and
/// JSON for machine-readable downstream ordering interfaces) from the workspace's
/// planning state. The service is pure and PHI-safe: it never receives or emits
/// patient identifiers — only geometry, metrics, warnings, and sizing proposals.
/// Key images are embedded as base64 PNG data URIs supplied by the caller.
/// </summary>
internal static class VascularReportExportService
{
    /// <summary>Stable report format version.</summary>
    public const int ReportFormatVersion = 1;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Builds the machine-readable JSON report. <paramref name="keyImages"/> maps a
    /// stable key (e.g. "diameter-chart", "cross-section") to a base64 PNG payload.
    /// </summary>
    public static string BuildJson(
        VascularWorkspaceSnapshot? snapshot,
        IReadOnlyDictionary<string, string>? keyImages = null)
    {
        var document = new
        {
            FormatVersion = ReportFormatVersion,
            GeneratedUtc = DateTimeOffset.UtcNow,
            SeriesInstanceUid = snapshot?.SeriesInstanceUid ?? string.Empty,
            Planning = BuildPlanningJson(snapshot?.PlanningBundle),
            VesselTree = BuildVesselTreeJson(snapshot?.VesselTree),
            EndograftPlan = BuildEndograftPlanJson(snapshot?.EndograftPlan),
            TaviPlanning = BuildTaviPlanningJson(snapshot?.TaviPlanning),
            KeyImages = keyImages ?? new Dictionary<string, string>(),
        };

        return JsonSerializer.Serialize(document, s_jsonOptions);
    }

    /// <summary>
    /// Builds a self-contained HTML report with embedded base64 PNG key images.
    /// The HTML is print-to-PDF friendly (no external assets).
    /// </summary>
    public static string BuildHtml(
        VascularWorkspaceSnapshot? snapshot,
        IReadOnlyDictionary<string, string>? keyImages = null)
    {
        VascularPlanningMetrics? metrics = snapshot?.PlanningBundle?.Metrics;
        EndograftPlan? plan = snapshot?.EndograftPlan;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"de\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>Vascular Planning Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1a1a1a}");
        sb.AppendLine("h1{font-size:20px;border-bottom:2px solid #2b6cb0;padding-bottom:6px}");
        sb.AppendLine("h2{font-size:15px;color:#2b6cb0;margin-top:22px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:8px}");
        sb.AppendLine("th,td{border:1px solid #ccc;padding:6px 8px;font-size:12px;text-align:left}");
        sb.AppendLine("th{background:#eef4fb}");
        sb.AppendLine(".warn{color:#b7791f}.crit{color:#c0392b}.ok{color:#27ae60}");
        sb.AppendLine("img{max-width:100%;border:1px solid #ddd;margin-top:8px}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Vascular Planning Report (EVAR)</h1>");
        sb.AppendLine($"<p>Erzeugt: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · Format v{ReportFormatVersion}</p>");

        AppendPlanningHtml(sb, metrics);
        AppendVesselTreeHtml(sb, snapshot?.VesselTree);
        AppendEndograftPlanHtml(sb, plan);
        AppendTaviPlanningHtml(sb, snapshot?.TaviPlanning);
        AppendKeyImagesHtml(sb, keyImages);

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static object? BuildPlanningJson(VascularPlanningBundle? bundle)
    {
        VascularPlanningMetrics? metrics = bundle?.Metrics;
        if (metrics is null)
        {
            return null;
        }

        return new
        {
            NeckLengthMm = metrics.ProximalNeck?.LengthMm,
            NeckMeanDiameterMm = metrics.ProximalNeck?.MeanEquivalentDiameterMm,
            NeckAngulationDegrees = metrics.NeckAngulationDegrees,
            NeckConicityMmPer10Mm = metrics.NeckConicity?.ConicityMmPer10Mm,
            AneurysmMaxDiameterMm = metrics.AneurysmMaxDiameterMm,
            NeckThrombusVolumeCm3 = metrics.NeckThrombusVolumeCm3,
            NeckCalciumVolumeCm3 = metrics.NeckCalciumVolumeCm3,
            AccessPaths = metrics.AccessPaths.Select(path => new
            {
                Side = path.Side,
                MinEquivalentDiameterMm = path.MinEquivalentDiameterMm,
                LengthMm = path.LengthMm,
                Tortuosity = path.Tortuosity,
                CalciumFraction = path.CalciumFraction,
                Status = path.Status.ToString(),
            }),
        };
    }

    private static object? BuildVesselTreeJson(VesselTree? tree)
    {
        if (tree is null || tree.Segments.Count == 0)
        {
            return null;
        }

        return tree.Segments.Select(segment => new
        {
            Label = segment.Label,
            DisplayName = segment.DisplayName,
            ParentLabel = segment.ParentLabel,
            PointCount = segment.Path?.Points?.Count ?? 0,
            LengthMm = segment.Path?.TotalLengthMm,
        });
    }

    private static object? BuildEndograftPlanJson(EndograftPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        return new
        {
            NeckDiameterMm = plan.NeckDiameterMm,
            ProximalOversizing = plan.ProximalOversizing,
            DistalOversizing = plan.DistalOversizing,
            ProximalLandingOverlapMm = plan.ProximalLandingOverlapMm,
            IliacLandingOverlapMm = plan.IliacLandingOverlapMm,
            RecommendedProximalDiameterMm = plan.RecommendedProximalDiameterMm,
            RecommendedDistalDiameterMm = plan.RecommendedDistalDiameterMm,
            Components = plan.Components.Select(component => new
            {
                Name = component.Name,
                ProximalDiameterMm = component.ProximalDiameterMm,
                DistalDiameterMm = component.DistalDiameterMm,
                LengthMm = component.LengthMm,
                StartStationMm = component.StartStationMm,
                EndStationMm = component.EndStationMm,
            }),
            Warnings = plan.Warnings.Select(warning => new
            {
                Severity = warning.Severity.ToString(),
                RuleKey = warning.RuleKey,
                Message = warning.Message,
                AffectedMeasurement = warning.AffectedMeasurement,
            }),
        };
    }

    private static object? BuildTaviPlanningJson(TaviPlanningBundle? bundle)
    {
        if (bundle is null)
        {
            return null;
        }

        return new
        {
            PointCount = bundle.Points.Count,
            Plane = bundle.Plane is null ? null : new
            {
                CenterX = bundle.Plane.Center.X,
                CenterY = bundle.Plane.Center.Y,
                CenterZ = bundle.Plane.Center.Z,
                NormalX = bundle.Plane.Normal.X,
                NormalY = bundle.Plane.Normal.Y,
                NormalZ = bundle.Plane.Normal.Z,
            },
            Annulus = BuildAnnulusMetricsJson(bundle.Annulus),
            Lvot = BuildAnnulusMetricsJson(bundle.Lvot),
            LvotOffsetMm = bundle.LvotOffsetMm,
            CoronaryOstia = bundle.CoronaryOstia.Select(ostium => new
            {
                Label = ostium.Label,
                AxialHeightMm = ostium.AxialHeightMm,
                HorizontalDistanceMm = ostium.HorizontalDistanceMm,
                AngleToPlaneDegrees = ostium.AngleToPlaneDegrees,
            }),
            Calcium = bundle.Calcium is null ? null : new
            {
                VolumeMm3 = bundle.Calcium.VolumeMm3,
                AgatstonScore = bundle.Calcium.AgatstonScore,
                Severity = bundle.Calcium.Severity.ToString(),
            },
            CarmAngulation = bundle.CarmAngulation is null ? null : new
            {
                LaoRaoDegrees = bundle.CarmAngulation.LaoRaoDegrees,
                CraCauDegrees = bundle.CarmAngulation.CraCauDegrees,
            },
            Sizing = bundle.Sizing is null ? null : new
            {
                ValveType = bundle.Sizing.ValveType.ToString(),
                BasisDiameterMm = bundle.Sizing.BasisDiameterMm,
                RecommendedMinDiameterMm = bundle.Sizing.RecommendedMinDiameterMm,
                RecommendedMaxDiameterMm = bundle.Sizing.RecommendedMaxDiameterMm,
                Warnings = bundle.Sizing.Warnings.Select(warning => new
                {
                    Severity = warning.Severity.ToString(),
                    RuleKey = warning.RuleKey,
                    Message = warning.Message,
                    AffectedMeasurement = warning.AffectedMeasurement,
                }),
            },
        };
    }

    private static object? BuildAnnulusMetricsJson(AnnulusMetrics? metrics)
    {
        if (metrics is null)
        {
            return null;
        }

        return new
        {
            AreaMm2 = metrics.AreaMm2,
            PerimeterMm = metrics.PerimeterMm,
            PerimeterDerivedDiameterMm = metrics.PerimeterDerivedDiameterMm,
            AreaDerivedDiameterMm = metrics.AreaDerivedDiameterMm,
            MinDiameterMm = metrics.MinDiameterMm,
            MaxDiameterMm = metrics.MaxDiameterMm,
        };
    }

    private static void AppendPlanningHtml(StringBuilder sb, VascularPlanningMetrics? metrics)
    {
        sb.AppendLine("<h2>Messungen</h2>");
        if (metrics is null)
        {
            sb.AppendLine("<p>Keine Messungen vorhanden.</p>");
            return;
        }

        sb.AppendLine("<table><tr><th>Metrik</th><th>Wert</th><th>Status</th></tr>");
        AppendMetricRow(sb, "Neck-Länge", metrics.ProximalNeck?.LengthMm, "mm");
        AppendMetricRow(sb, "Neck-Øeq (Mittel)", metrics.ProximalNeck?.MeanEquivalentDiameterMm, "mm");
        AppendMetricRow(sb, "Neck-Angulation", metrics.NeckAngulationDegrees, "°");
        AppendMetricRow(sb, "Neck-Konizität", metrics.NeckConicity?.ConicityMmPer10Mm, "mm/10mm");
        AppendMetricRow(sb, "Aneurysma-Sack max Ø", metrics.AneurysmMaxDiameterMm, "mm");
        AppendMetricRow(sb, "Neck-Thrombus-Volumen", metrics.NeckThrombusVolumeCm3, "cm³");
        AppendMetricRow(sb, "Neck-Kalk-Volumen", metrics.NeckCalciumVolumeCm3, "cm³");
        sb.AppendLine("</table>");

        if (metrics.AccessPaths.Count > 0)
        {
            sb.AppendLine("<h2>Iliakal-Zugangswege</h2>");
            sb.AppendLine("<table><tr><th>Seite</th><th>min Øeq</th><th>Länge</th><th>Tortuosität</th><th>Kalk-Anteil</th><th>Status</th></tr>");
            foreach (VascularAccessPathMetrics path in metrics.AccessPaths)
            {
                sb.AppendLine($"<tr><td>{EscapeHtml(path.Side)}</td>"
                    + $"<td>{FormatMm(path.MinEquivalentDiameterMm)}</td>"
                    + $"<td>{FormatMm(path.LengthMm)}</td>"
                    + $"<td>{FormatValue(path.Tortuosity)}</td>"
                    + $"<td>{FormatFraction(path.CalciumFraction)}</td>"
                    + $"<td>{StatusClass(path.Status)}</td></tr>");
            }

            sb.AppendLine("</table>");
        }
    }

    private static void AppendVesselTreeHtml(StringBuilder sb, VesselTree? tree)
    {
        sb.AppendLine("<h2>Vessel-Tree</h2>");
        if (tree is null || tree.Segments.Count == 0)
        {
            sb.AppendLine("<p>Keine Segmente vorhanden.</p>");
            return;
        }

        sb.AppendLine("<table><tr><th>Segment</th><th>Parent</th><th>Punkte</th><th>Länge</th></tr>");
        foreach (VesselSegment segment in tree.Segments)
        {
            sb.AppendLine($"<tr><td>{EscapeHtml(segment.DisplayName)}</td>"
                + $"<td>{EscapeHtml(segment.ParentLabel ?? "—")}</td>"
                + $"<td>{segment.Path?.Points?.Count ?? 0}</td>"
                + $"<td>{FormatMm(segment.Path?.TotalLengthMm)}</td></tr>");
        }

        sb.AppendLine("</table>");
    }

    private static void AppendEndograftPlanHtml(StringBuilder sb, EndograftPlan? plan)
    {
        sb.AppendLine("<h2>Endograft-Sizing</h2>");
        if (plan is null)
        {
            sb.AppendLine("<p>Keine Sizing-Vorschlag vorhanden.</p>");
            return;
        }

        sb.AppendLine("<table><tr><th>Parameter</th><th>Wert</th></tr>");
        sb.AppendLine($"<tr><td>Neck-Øeq</td><td>{FormatMm(plan.NeckDiameterMm)}</td></tr>");
        sb.AppendLine($"<tr><td>Proximal-Oversizing</td><td>{plan.ProximalOversizing:P0}</td></tr>");
        sb.AppendLine($"<tr><td>Distal-Oversizing</td><td>{plan.DistalOversizing:P0}</td></tr>");
        sb.AppendLine($"<tr><td>Empfohlener proximal Ø</td><td>{FormatMm(plan.RecommendedProximalDiameterMm)}</td></tr>");
        sb.AppendLine($"<tr><td>Empfohlener distal Ø</td><td>{FormatMm(plan.RecommendedDistalDiameterMm)}</td></tr>");
        sb.AppendLine("</table>");

        if (plan.Components.Count > 0)
        {
            sb.AppendLine("<h2>Komponenten</h2>");
            sb.AppendLine("<table><tr><th>Name</th><th>prox Ø</th><th>dist Ø</th><th>Länge</th><th>Station</th></tr>");
            foreach (GraftComponent component in plan.Components)
            {
                sb.AppendLine($"<tr><td>{EscapeHtml(component.Name)}</td>"
                    + $"<td>{component.ProximalDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td>"
                    + $"<td>{component.DistalDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td>"
                    + $"<td>{component.LengthMm.ToString("F0", CultureInfo.InvariantCulture)} mm</td>"
                    + $"<td>{component.StartStationMm.ToString("F0", CultureInfo.InvariantCulture)}–{component.EndStationMm.ToString("F0", CultureInfo.InvariantCulture)} mm</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        if (plan.Warnings.Count > 0)
        {
            sb.AppendLine("<h2>Warnungen</h2>");
            sb.AppendLine("<ul>");
            foreach (EndograftWarning warning in plan.Warnings)
            {
                string cssClass = warning.Severity switch
                {
                    EndograftWarningSeverity.Critical => "crit",
                    EndograftWarningSeverity.Warning => "warn",
                    _ => "ok",
                };
                sb.AppendLine($"<li class=\"{cssClass}\">{EscapeHtml(warning.Message)}</li>");
            }

            sb.AppendLine("</ul>");
        }
        else
        {
            sb.AppendLine("<p class=\"ok\">● Keine Warnungen — Plan unauffällig.</p>");
        }
    }

    private static void AppendTaviPlanningHtml(StringBuilder sb, TaviPlanningBundle? bundle)
    {
        sb.AppendLine("<h2>TAVI-Planung</h2>");
        if (bundle is null)
        {
            sb.AppendLine("<p>Keine TAVI-Planung vorhanden.</p>");
            return;
        }

        if (bundle.Annulus is AnnulusMetrics annulus)
        {
            sb.AppendLine("<h2>Annulus</h2>");
            sb.AppendLine("<table><tr><th>Metrik</th><th>Wert</th></tr>");
            sb.AppendLine($"<tr><td>Fläche</td><td>{annulus.AreaMm2.ToString("F1", CultureInfo.InvariantCulture)} mm²</td></tr>");
            sb.AppendLine($"<tr><td>Perimeter</td><td>{annulus.PerimeterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine($"<tr><td>Perimeter-Derived-Ø</td><td>{annulus.PerimeterDerivedDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine($"<tr><td>Area-Derived-Ø</td><td>{annulus.AreaDerivedDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine($"<tr><td>min/max-Ø</td><td>{annulus.MinDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} / {annulus.MaxDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine("</table>");
        }

        if (bundle.Lvot is AnnulusMetrics lvot)
        {
            sb.AppendLine("<h2>LVOT</h2>");
            sb.AppendLine($"<p>Offset: {bundle.LvotOffsetMm.ToString("F1", CultureInfo.InvariantCulture)} mm distal.</p>");
            sb.AppendLine("<table><tr><th>Metrik</th><th>Wert</th></tr>");
            sb.AppendLine($"<tr><td>Fläche</td><td>{lvot.AreaMm2.ToString("F1", CultureInfo.InvariantCulture)} mm²</td></tr>");
            sb.AppendLine($"<tr><td>Perimeter</td><td>{lvot.PerimeterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine($"<tr><td>Perimeter-Derived-Ø</td><td>{lvot.PerimeterDerivedDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine("</table>");
        }

        if (bundle.CoronaryOstia.Count > 0)
        {
            sb.AppendLine("<h2>Koronarostien</h2>");
            sb.AppendLine("<table><tr><th>Ostium</th><th>Axiale Höhe</th><th>Horiz. Distanz</th><th>Winkel zur Ebene</th></tr>");
            foreach (CoronaryOstiumResult ostium in bundle.CoronaryOstia)
            {
                sb.AppendLine($"<tr><td>{EscapeHtml(ostium.Label)}</td>"
                    + $"<td>{ostium.AxialHeightMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td>"
                    + $"<td>{ostium.HorizontalDistanceMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td>"
                    + $"<td>{ostium.AngleToPlaneDegrees.ToString("F1", CultureInfo.InvariantCulture)}°</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        if (bundle.Calcium is LeafletCalciumResult calcium)
        {
            sb.AppendLine("<h2>Verkalkung</h2>");
            sb.AppendLine("<table><tr><th>Metrik</th><th>Wert</th></tr>");
            sb.AppendLine($"<tr><td>Kalk-Volumen</td><td>{calcium.VolumeMm3.ToString("F1", CultureInfo.InvariantCulture)} mm³</td></tr>");
            sb.AppendLine($"<tr><td>Agatston-Score</td><td>{calcium.AgatstonScore.ToString("F0", CultureInfo.InvariantCulture)}</td></tr>");
            sb.AppendLine($"<tr><td>Schweregrad</td><td>{EscapeHtml(calcium.Severity.ToString())}</td></tr>");
            sb.AppendLine("</table>");
        }

        if (bundle.CarmAngulation is CarmAngulationResult carm)
        {
            sb.AppendLine("<h2>C-Arm-Angulation</h2>");
            sb.AppendLine($"<p>LAO/RAO: {carm.LaoRaoDegrees.ToString("F1", CultureInfo.InvariantCulture)}° · CRA/CAU: {carm.CraCauDegrees.ToString("F1", CultureInfo.InvariantCulture)}°</p>");
        }

        if (bundle.Sizing is ValveSizingResult sizing)
        {
            sb.AppendLine("<h2>Valve-Sizing</h2>");
            sb.AppendLine("<table><tr><th>Parameter</th><th>Wert</th></tr>");
            sb.AppendLine($"<tr><td>Valve-Typ</td><td>{EscapeHtml(sizing.ValveType.ToString())}</td></tr>");
            sb.AppendLine($"<tr><td>Basis-Ø</td><td>{sizing.BasisDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine($"<tr><td>Empfohlener Ø-Band</td><td>{sizing.RecommendedMinDiameterMm.ToString("F1", CultureInfo.InvariantCulture)}–{sizing.RecommendedMaxDiameterMm.ToString("F1", CultureInfo.InvariantCulture)} mm</td></tr>");
            sb.AppendLine("</table>");

            if (sizing.Warnings.Count > 0)
            {
                sb.AppendLine("<h2>TAVI-Warnungen</h2>");
                sb.AppendLine("<ul>");
                foreach (TaviWarning warning in sizing.Warnings)
                {
                    string cssClass = warning.Severity switch
                    {
                        TaviWarningSeverity.Critical => "crit",
                        TaviWarningSeverity.Warning => "warn",
                        _ => "ok",
                    };
                    sb.AppendLine($"<li class=\"{cssClass}\">{EscapeHtml(warning.Message)}</li>");
                }

                sb.AppendLine("</ul>");
            }
            else
            {
                sb.AppendLine("<p class=\"ok\">● Keine TAVI-Warnungen — Plan unauffällig.</p>");
            }
        }
    }

    private static void AppendKeyImagesHtml(StringBuilder sb, IReadOnlyDictionary<string, string>? keyImages)
    {
        if (keyImages is null || keyImages.Count == 0)
        {
            return;
        }

        sb.AppendLine("<h2>Key-Images</h2>");
        foreach ((string key, string base64Png) in keyImages)
        {
            if (string.IsNullOrWhiteSpace(base64Png))
            {
                continue;
            }

            sb.AppendLine($"<h3>{EscapeHtml(key)}</h3>");
            sb.AppendLine($"<img alt=\"{EscapeHtml(key)}\" src=\"data:image/png;base64,{base64Png}\">");
        }
    }

    private static void AppendMetricRow(StringBuilder sb, string label, double? value, string unit)
    {
        sb.AppendLine($"<tr><td>{EscapeHtml(label)}</td><td>{FormatValue(value)} {unit}</td><td>—</td></tr>");
    }

    private static string FormatMm(double? value) =>
        value is double v ? $"{v.ToString("F1", CultureInfo.InvariantCulture)} mm" : "—";

    private static string FormatValue(double? value) =>
        value is double v ? v.ToString("F2", CultureInfo.InvariantCulture) : "—";

    private static string FormatFraction(double? value) =>
        value is double v ? v.ToString("P0", CultureInfo.InvariantCulture) : "—";

    private static string StatusClass(VascularMetricStatus status) => status switch
    {
        VascularMetricStatus.Critical => "<span class=\"crit\">Kritisch</span>",
        VascularMetricStatus.Warning => "<span class=\"warn\">Warnung</span>",
        VascularMetricStatus.Ok => "<span class=\"ok\">Ok</span>",
        _ => "<span>Unbekannt</span>",
    };

    private static string EscapeHtml(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
