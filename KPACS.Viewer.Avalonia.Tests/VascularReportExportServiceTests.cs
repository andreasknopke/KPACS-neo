using System.Text.Json;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure vascular report export service (Phase E): HTML and JSON
/// generation from a workspace snapshot, PHI-safety (no patient identifiers), and
/// key-image embedding. Pure model surface — no Avalonia, no file I/O.
/// </summary>
public class VascularReportExportServiceTests
{
    private static VascularWorkspaceSnapshot BuildSnapshot()
    {
        return new VascularWorkspaceSnapshot
        {
            SeriesInstanceUid = "1.2.3.4.5",
            FrameOfReferenceUid = "1.2.3.4.5.6",
            PlanningBundle = new VascularPlanningBundle
            {
                Metrics = new VascularPlanningMetrics
                {
                    ProximalNeck = new VascularSpanMetrics
                    {
                        LengthMm = 25.0,
                        MeanEquivalentDiameterMm = 20.0,
                    },
                    NeckAngulationDegrees = 30.0,
                    NeckConicity = new VascularConicityMetrics { ConicityMmPer10Mm = 0.5 },
                    AccessPaths =
                    [
                        new VascularAccessPathMetrics
                        {
                            Side = "Links",
                            MinEquivalentDiameterMm = 15.0,
                            LengthMm = 30.0,
                            Tortuosity = 1.1,
                            CalciumFraction = 0.1,
                            Status = VascularMetricStatus.Ok,
                        },
                    ],
                },
            },
            EndograftPlan = new EndograftPlan
            {
                NeckDiameterMm = 20.0,
                RecommendedProximalDiameterMm = 23.0,
                RecommendedDistalDiameterMm = 16.8,
                Components =
                [
                    new GraftComponent
                    {
                        Name = "Aorten-Body",
                        ProximalDiameterMm = 23.0,
                        DistalDiameterMm = 16.8,
                        LengthMm = 60.0,
                        StartStationMm = 0.0,
                        EndStationMm = 60.0,
                    },
                ],
                Warnings =
                [
                    new EndograftWarning
                    {
                        Severity = EndograftWarningSeverity.Warning,
                        RuleKey = "neck-too-short",
                        Message = "Neck length below 15 mm.",
                        AffectedMeasurement = "Neck-Length",
                    },
                ],
            },
            VesselTree = new VesselTree
            {
                Segments =
                [
                    new VesselSegment
                    {
                        Label = "aorta",
                        DisplayName = "Aorta",
                        Path = new CenterlinePath { TotalLengthMm = 60.0 },
                    },
                ],
            },
        };
    }

    [Fact]
    public void BuildJson_contains_planning_metrics()
    {
        string json = VascularReportExportService.BuildJson(BuildSnapshot());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("1.2.3.4.5", root.GetProperty("SeriesInstanceUid").GetString());
        Assert.Equal(25.0, root.GetProperty("Planning").GetProperty("NeckLengthMm").GetDouble());
        Assert.Equal(20.0, root.GetProperty("Planning").GetProperty("NeckMeanDiameterMm").GetDouble());
    }

    [Fact]
    public void BuildJson_contains_endograft_plan_and_warnings()
    {
        string json = VascularReportExportService.BuildJson(BuildSnapshot());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement plan = document.RootElement.GetProperty("EndograftPlan");

        Assert.Equal(23.0, plan.GetProperty("RecommendedProximalDiameterMm").GetDouble());
        Assert.Equal(1, plan.GetProperty("Components").GetArrayLength());
        Assert.Equal(1, plan.GetProperty("Warnings").GetArrayLength());
        Assert.Equal("neck-too-short", plan.GetProperty("Warnings")[0].GetProperty("RuleKey").GetString());
    }

    [Fact]
    public void BuildJson_contains_key_images()
    {
        string json = VascularReportExportService.BuildJson(
            BuildSnapshot(),
            new Dictionary<string, string> { ["diameter-chart"] = "aGVsbG8=" });

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement images = document.RootElement.GetProperty("KeyImages");
        Assert.Equal("aGVsbG8=", images.GetProperty("diameter-chart").GetString());
    }

    [Fact]
    public void BuildJson_null_snapshot_is_valid()
    {
        string json = VascularReportExportService.BuildJson(null);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(string.Empty, document.RootElement.GetProperty("SeriesInstanceUid").GetString());
    }

    [Fact]
    public void BuildHtml_contains_metric_table()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot());
        Assert.Contains("Neck", html);
        Assert.Contains("25.00 mm", html);
        Assert.Contains("Endograft-Sizing", html);
    }

    [Fact]
    public void BuildHtml_contains_warning_with_severity_class()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot());
        Assert.Contains("class=\"warn\"", html);
        Assert.Contains("Neck length below 15 mm.", html);
    }

    [Fact]
    public void BuildHtml_embeds_key_image_as_base64_data_uri()
    {
        string html = VascularReportExportService.BuildHtml(
            BuildSnapshot(),
            new Dictionary<string, string> { ["diameter-chart"] = "aGVsbG8=" });

        Assert.Contains("data:image/png;base64,aGVsbG8=", html);
    }

    [Fact]
    public void BuildHtml_escapes_html_in_values()
    {
        VascularWorkspaceSnapshot snapshot = BuildSnapshot() with
        {
            VesselTree = new VesselTree
            {
                Segments =
                [
                    new VesselSegment
                    {
                        Label = "aorta",
                        DisplayName = "<script>alert('x')</script>",
                        Path = new CenterlinePath { TotalLengthMm = 60.0 },
                    },
                ],
            },
        };

        string html = VascularReportExportService.BuildHtml(snapshot);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildHtml_null_snapshot_is_valid()
    {
        string html = VascularReportExportService.BuildHtml(null);
        Assert.Contains("Vascular Planning Report", html);
        Assert.Contains("Keine Messungen vorhanden.", html);
    }
}
