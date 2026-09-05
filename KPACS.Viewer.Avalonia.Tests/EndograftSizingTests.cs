using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure endograft sizing (Phase D): oversizing arithmetic, component
/// staging, and every warning rule at its boundary (±ε). Pure model surface — no Avalonia,
/// no volume I/O.
/// </summary>
public class EndograftSizingTests
{
    private static EndograftSizingInput HealthyInput() => new()
    {
        NeckDiameterMm = 20.0,
        NeckLengthMm = 25.0,
        NeckConicityMmPer10Mm = 0.5,
        NeckAngulationDegrees = 30.0,
        DistalLandingDiameterMm = 12.0,
        ProximalNeckStartStationMm = 0.0,
        AorticEndStationMm = 60.0,
        DistalLandingStartStationMm = 60.0,
        DistalLandingEndStationMm = 90.0,
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
    };

    // ── Oversizing ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyOversizing_recommended_15_percent()
    {
        double? result = EndograftSizingService.ApplyOversizing(20.0, 0.15);
        Assert.NotNull(result);
        Assert.Equal(23.0, result!.Value, 6);
    }

    [Fact]
    public void ApplyOversizing_distal_12_percent()
    {
        double? result = EndograftSizingService.ApplyOversizing(12.0, 0.12);
        Assert.NotNull(result);
        Assert.Equal(13.44, result!.Value, 6);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.20)]
    public void ApplyOversizing_boundary_fractions(double oversizing)
    {
        double? result = EndograftSizingService.ApplyOversizing(20.0, oversizing);
        Assert.NotNull(result);
        Assert.Equal(20.0 * (1.0 + oversizing), result!.Value, 6);
    }

    [Fact]
    public void ApplyOversizing_null_or_nonpositive_returns_null()
    {
        Assert.Null(EndograftSizingService.ApplyOversizing(null, 0.15));
        Assert.Null(EndograftSizingService.ApplyOversizing(0.0, 0.15));
        Assert.Null(EndograftSizingService.ApplyOversizing(-5.0, 0.15));
    }

    // ── Component staging ─────────────────────────────────────────────────────

    [Fact]
    public void BuildComponents_aortic_body_and_iliac_limbs()
    {
        EndograftSizingInput input = HealthyInput();
        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        Assert.Equal(2, components.Count);
        Assert.Equal("Aorten-Body", components[0].Name);
        Assert.Equal(23.0, components[0].ProximalDiameterMm, 6);
        Assert.Equal(60.0, components[0].LengthMm, 6);
        Assert.Equal("Iliakal-Limb Links", components[1].Name);
        Assert.Equal(13.44, components[1].ProximalDiameterMm, 6);
        Assert.Equal(30.0, components[1].LengthMm, 6);
    }

    [Fact]
    public void BuildComponents_no_stations_yields_empty()
    {
        EndograftSizingInput input = HealthyInput() with
        {
            ProximalNeckStartStationMm = null,
            AorticEndStationMm = null,
            DistalLandingStartStationMm = null,
            DistalLandingEndStationMm = null,
        };

        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        Assert.Empty(components);
    }

    [Fact]
    public void BuildComponents_negative_length_clamped_to_zero()
    {
        EndograftSizingInput input = HealthyInput() with
        {
            ProximalNeckStartStationMm = 60.0,
            AorticEndStationMm = 10.0,
            DistalLandingStartStationMm = null,
            DistalLandingEndStationMm = null,
        };

        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        Assert.Single(components);
        Assert.Equal("Aorten-Body", components[0].Name);
        Assert.Equal(0.0, components[0].LengthMm, 6);
    }

    // ── Warning engine ────────────────────────────────────────────────────────

    [Fact]
    public void WarningEngine_healthy_input_no_warnings()
    {
        EndograftSizingInput input = HealthyInput();
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(10.0, EndograftWarningSeverity.Critical)]
    [InlineData(15.0, EndograftWarningSeverity.Warning)]
    [InlineData(15.1, EndograftWarningSeverity.Info)] // no warning emitted
    public void WarningEngine_neck_length_boundaries(double neckLength, EndograftWarningSeverity expected)
    {
        EndograftSizingInput input = HealthyInput() with { NeckLengthMm = neckLength };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        EndograftWarning? neck = warnings.FirstOrDefault(w => w.RuleKey == "neck-too-short");
        if (expected == EndograftWarningSeverity.Info)
        {
            Assert.Null(neck);
        }
        else
        {
            Assert.NotNull(neck);
            Assert.Equal(expected, neck!.Severity);
        }
    }

    [Theory]
    [InlineData(2.0, EndograftWarningSeverity.Critical)]
    [InlineData(1.0, EndograftWarningSeverity.Warning)]
    [InlineData(0.99, EndograftWarningSeverity.Info)]
    public void WarningEngine_conicity_boundaries(double conicity, EndograftWarningSeverity expected)
    {
        EndograftSizingInput input = HealthyInput() with { NeckConicityMmPer10Mm = conicity };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        EndograftWarning? w = warnings.FirstOrDefault(x => x.RuleKey == "neck-conicity");
        if (expected == EndograftWarningSeverity.Info)
        {
            Assert.Null(w);
        }
        else
        {
            Assert.NotNull(w);
            Assert.Equal(expected, w!.Severity);
        }
    }

    [Theory]
    [InlineData(90.0, EndograftWarningSeverity.Critical)]
    [InlineData(60.0, EndograftWarningSeverity.Warning)]
    [InlineData(59.9, EndograftWarningSeverity.Info)]
    public void WarningEngine_angulation_boundaries(double angulation, EndograftWarningSeverity expected)
    {
        EndograftSizingInput input = HealthyInput() with { NeckAngulationDegrees = angulation };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        EndograftWarning? w = warnings.FirstOrDefault(x => x.RuleKey == "neck-angulation");
        if (expected == EndograftWarningSeverity.Info)
        {
            Assert.Null(w);
        }
        else
        {
            Assert.NotNull(w);
            Assert.Equal(expected, w!.Severity);
        }
    }

    [Theory]
    [InlineData(6.0, EndograftWarningSeverity.Critical)]
    [InlineData(7.0, EndograftWarningSeverity.Warning)]
    [InlineData(7.1, EndograftWarningSeverity.Info)]
    public void WarningEngine_access_diameter_boundaries(double minDia, EndograftWarningSeverity expected)
    {
        EndograftSizingInput input = HealthyInput() with
        {
            AccessPaths =
            [
                new VascularAccessPathMetrics
                {
                    Side = "Links",
                    MinEquivalentDiameterMm = minDia,
                    LengthMm = 30.0,
                    Tortuosity = 1.1,
                    CalciumFraction = 0.1,
                    Status = VascularMetricStatus.Ok,
                },
            ],
        };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        EndograftWarning? w = warnings.FirstOrDefault(x => x.RuleKey == "access-too-small");
        if (expected == EndograftWarningSeverity.Info)
        {
            Assert.Null(w);
        }
        else
        {
            Assert.NotNull(w);
            Assert.Equal(expected, w!.Severity);
        }
    }

    [Theory]
    [InlineData(0.5, EndograftWarningSeverity.Critical)]
    [InlineData(0.25, EndograftWarningSeverity.Warning)]
    [InlineData(0.24, EndograftWarningSeverity.Info)]
    public void WarningEngine_access_calcium_boundaries(double calcium, EndograftWarningSeverity expected)
    {
        EndograftSizingInput input = HealthyInput() with
        {
            AccessPaths =
            [
                new VascularAccessPathMetrics
                {
                    Side = "Links",
                    MinEquivalentDiameterMm = 9.0,
                    LengthMm = 30.0,
                    Tortuosity = 1.1,
                    CalciumFraction = calcium,
                    Status = VascularMetricStatus.Ok,
                },
            ],
        };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        EndograftWarning? w = warnings.FirstOrDefault(x => x.RuleKey == "access-calcified");
        if (expected == EndograftWarningSeverity.Info)
        {
            Assert.Null(w);
        }
        else
        {
            Assert.NotNull(w);
            Assert.Equal(expected, w!.Severity);
        }
    }

    [Fact]
    public void WarningEngine_material_conflict_when_limb_exceeds_access()
    {
        // Limb Ø 13.44 mm but access min Ø only 8 mm → cannot be introduced.
        EndograftSizingInput input = HealthyInput() with
        {
            AccessPaths =
            [
                new VascularAccessPathMetrics
                {
                    Side = "Links",
                    MinEquivalentDiameterMm = 8.0,
                    LengthMm = 30.0,
                    Tortuosity = 1.1,
                    CalciumFraction = 0.1,
                    Status = VascularMetricStatus.Ok,
                },
            ],
        };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        Assert.Contains(warnings, w => w.RuleKey == "material-conflict");
    }

    [Fact]
    public void WarningEngine_limb_negative_length_is_critical()
    {
        EndograftSizingInput input = HealthyInput() with
        {
            DistalLandingStartStationMm = 90.0,
            DistalLandingEndStationMm = 60.0,
        };
        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(input, 23.0, 13.44, components);

        Assert.Contains(warnings, w => w.RuleKey == "limb-negative-length" && w.Severity == EndograftWarningSeverity.Critical);
    }

    [Fact]
    public void WarningEngine_proximal_landing_too_short()
    {
        // Aortic body length 10 mm < 15 mm required overlap.
        EndograftSizingInput input = HealthyInput() with
        {
            ProximalNeckStartStationMm = 0.0,
            AorticEndStationMm = 10.0,
        };
        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(input, 23.0, 13.44, components);

        Assert.Contains(warnings, w => w.RuleKey == "proximal-landing-short");
    }

    [Fact]
    public void WarningEngine_iliac_landing_too_short()
    {
        // Iliac limb length 10 mm < 20 mm required overlap.
        EndograftSizingInput input = HealthyInput() with
        {
            DistalLandingStartStationMm = 60.0,
            DistalLandingEndStationMm = 70.0,
        };
        List<GraftComponent> components = EndograftSizingService.BuildComponents(input, 23.0, 13.44);

        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(input, 23.0, 13.44, components);

        Assert.Contains(warnings, w => w.RuleKey == "iliac-landing-short");
    }

    [Theory]
    [InlineData(0.09)]
    [InlineData(0.21)]
    public void WarningEngine_proximal_oversizing_out_of_range(double oversizing)
    {
        EndograftSizingInput input = HealthyInput() with { ProximalOversizing = oversizing };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        Assert.Contains(warnings, w => w.RuleKey == "oversizing-out-of-range");
    }

    [Theory]
    [InlineData(0.09)]
    [InlineData(0.16)]
    public void WarningEngine_distal_oversizing_out_of_range(double oversizing)
    {
        EndograftSizingInput input = HealthyInput() with { DistalOversizing = oversizing };
        List<EndograftWarning> warnings = EndograftSizingService.RunWarningEngine(
            input, 23.0, 13.44, EndograftSizingService.BuildComponents(input, 23.0, 13.44));

        Assert.Contains(warnings, w => w.RuleKey == "oversizing-out-of-range");
    }

    // ── Full plan ─────────────────────────────────────────────────────────────

    [Fact]
    public void Size_populates_plan()
    {
        EndograftSizingInput input = HealthyInput();
        EndograftPlan plan = EndograftSizingService.Size(input);

        Assert.NotNull(plan.RecommendedProximalDiameterMm);
        Assert.Equal(23.0, plan.RecommendedProximalDiameterMm!.Value, 6);
        Assert.NotNull(plan.RecommendedDistalDiameterMm);
        Assert.Equal(13.44, plan.RecommendedDistalDiameterMm!.Value, 6);
        Assert.Equal(2, plan.Components.Count);
        Assert.Empty(plan.Warnings);
    }
}
