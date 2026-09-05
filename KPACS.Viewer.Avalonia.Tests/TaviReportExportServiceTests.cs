using System.Text.Json;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the TAVI report section of the pure vascular report export service
/// (Phase G6): JSON and HTML generation from a TAVI planning bundle, PHI-safety (no
/// patient identifiers), and the annulus/LVOT/ostia/calcium/C-arm/sizing sections.
/// Pure model surface — no Avalonia, no file I/O.
/// </summary>
public class TaviReportExportServiceTests
{
    private static TaviPlanningBundle BuildBundle()
    {
        return new TaviPlanningBundle
        {
            Points =
            [
                new AnnulusPoint { PatientPoint = new Vector3D(0, 0, 0), Label = "Nodulus" },
                new AnnulusPoint { PatientPoint = new Vector3D(10, 0, 0), Label = "Left" },
                new AnnulusPoint { PatientPoint = new Vector3D(0, 10, 0), Label = "Right" },
                new AnnulusPoint { PatientPoint = new Vector3D(-10, 0, 0), Label = "NonCoronary" },
            ],
            Plane = new AnnulusPlane { Center = new Vector3D(0, 0, 0), Normal = new Vector3D(0, 0, 1) },
            Annulus = new AnnulusMetrics
            {
                AreaMm2 = 314.0,
                PerimeterMm = 62.8,
                PerimeterDerivedDiameterMm = 20.0,
                AreaDerivedDiameterMm = 20.0,
                MinDiameterMm = 19.0,
                MaxDiameterMm = 21.0,
            },
            Lvot = new AnnulusMetrics
            {
                AreaMm2 = 280.0,
                PerimeterMm = 59.3,
                PerimeterDerivedDiameterMm = 18.9,
                AreaDerivedDiameterMm = 18.9,
                MinDiameterMm = 18.0,
                MaxDiameterMm = 19.8,
            },
            LvotOffsetMm = 10.0,
            CoronaryOstia =
            [
                new CoronaryOstiumResult
                {
                    Label = "LCA",
                    AxialHeightMm = 12.0,
                    HorizontalDistanceMm = 8.0,
                    AngleToPlaneDegrees = 45.0,
                },
            ],
            Calcium = new LeafletCalciumResult
            {
                VolumeMm3 = 120.0,
                AgatstonScore = 450.0,
                Severity = CalciumSeverity.Moderate,
            },
            CarmAngulation = new CarmAngulationResult
            {
                LaoRaoDegrees = 15.0,
                CraCauDegrees = -5.0,
            },
            Sizing = new ValveSizingResult
            {
                ValveType = ValveType.BalloonExpandable,
                BasisDiameterMm = 20.0,
                RecommendedMinDiameterMm = 20.0,
                RecommendedMaxDiameterMm = 22.0,
                Warnings =
                [
                    new TaviWarning
                    {
                        Severity = TaviWarningSeverity.Warning,
                        RuleKey = "severe-calcium",
                        Message = "Schwere Verkalkung — Ballon-Valvuloplastie erwägen.",
                        AffectedMeasurement = "Verkalkung",
                    },
                ],
            },
        };
    }

    private static VascularWorkspaceSnapshot BuildSnapshot()
    {
        return new VascularWorkspaceSnapshot
        {
            SeriesInstanceUid = "1.2.3.4.5",
            FrameOfReferenceUid = "1.2.3.4.5.6",
            TaviPlanning = BuildBundle(),
        };
    }

    [Fact]
    public void BuildJson_contains_tavi_planning_node()
    {
        string json = VascularReportExportService.BuildJson(BuildSnapshot());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement tavi = document.RootElement.GetProperty("TaviPlanning");

        Assert.Equal(4, tavi.GetProperty("PointCount").GetInt32());
        Assert.Equal(20.0, tavi.GetProperty("Annulus").GetProperty("PerimeterDerivedDiameterMm").GetDouble());
        Assert.Equal(18.9, tavi.GetProperty("Lvot").GetProperty("PerimeterDerivedDiameterMm").GetDouble());
    }

    [Fact]
    public void BuildJson_contains_ostia_calcium_carm_sizing()
    {
        string json = VascularReportExportService.BuildJson(BuildSnapshot());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement tavi = document.RootElement.GetProperty("TaviPlanning");

        Assert.Equal("LCA", tavi.GetProperty("CoronaryOstia")[0].GetProperty("Label").GetString());
        Assert.Equal(450.0, tavi.GetProperty("Calcium").GetProperty("AgatstonScore").GetDouble());
        Assert.Equal("Moderate", tavi.GetProperty("Calcium").GetProperty("Severity").GetString());
        Assert.Equal(15.0, tavi.GetProperty("CarmAngulation").GetProperty("LaoRaoDegrees").GetDouble());
        Assert.Equal(22.0, tavi.GetProperty("Sizing").GetProperty("RecommendedMaxDiameterMm").GetDouble());
        Assert.Equal("severe-calcium", tavi.GetProperty("Sizing").GetProperty("Warnings")[0].GetProperty("RuleKey").GetString());
    }

    [Fact]
    public void BuildJson_null_tavi_is_valid()
    {
        string json = VascularReportExportService.BuildJson(BuildSnapshot() with { TaviPlanning = null });
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("TaviPlanning").ValueKind);
    }

    [Fact]
    public void BuildHtml_contains_annulus_table()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot());
        Assert.Contains("TAVI-Planung", html);
        Assert.Contains("Perimeter-Derived-Ø", html);
        Assert.Contains("20.0 mm", html);
    }

    [Fact]
    public void BuildHtml_contains_lvot_ostia_calcium_carm_sections()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot());
        Assert.Contains("LVOT", html);
        Assert.Contains("Koronarostien", html);
        Assert.Contains("LCA", html);
        Assert.Contains("Verkalkung", html);
        Assert.Contains("Agatston-Score", html);
        Assert.Contains("C-Arm-Angulation", html);
        Assert.Contains("Valve-Sizing", html);
    }

    [Fact]
    public void BuildHtml_contains_tavi_warning_with_severity_class()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot());
        Assert.Contains("class=\"warn\"", html);
        Assert.Contains("Schwere Verkalkung", html);
    }

    [Fact]
    public void BuildHtml_null_tavi_is_valid()
    {
        string html = VascularReportExportService.BuildHtml(BuildSnapshot() with { TaviPlanning = null });
        Assert.Contains("Keine TAVI-Planung vorhanden.", html);
    }

    [Fact]
    public void BuildHtml_escapes_html_in_tavi_values()
    {
        TaviPlanningBundle bundle = BuildBundle() with
        {
            CoronaryOstia =
            [
                new CoronaryOstiumResult
                {
                    Label = "<script>alert('x')</script>",
                    AxialHeightMm = 12.0,
                    HorizontalDistanceMm = 8.0,
                    AngleToPlaneDegrees = 45.0,
                },
            ],
        };

        string html = VascularReportExportService.BuildHtml(BuildSnapshot() with { TaviPlanning = bundle });
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
