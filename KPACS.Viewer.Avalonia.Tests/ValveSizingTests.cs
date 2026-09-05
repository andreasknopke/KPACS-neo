using KPACS.Viewer.Models;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure valve sizing rule engine (Phase G5): BEV/SEV oversizing
/// bands, and every risk-warning rule at its boundary. Pure model surface — no
/// Avalonia, no volume I/O.
/// </summary>
public class ValveSizingTests
{
    private static ValveSizingInput HealthyInput(ValveType type) => new()
    {
        ValveType = type,
        BasisDiameterMm = 24.0,
        LvotDiameterMm = 22.0,
        CoronaryOstiumHeightMm = 14.0,
        CalciumSeverity = CalciumSeverity.None,
        ContralateralAccessOk = true,
    };

    // ── Sizing bands ──────────────────────────────────────────────────────────

    [Fact]
    public void Size_bev_applies_zero_to_ten_percent_band()
    {
        ValveSizingResult r = ValveSizingService.Size(HealthyInput(ValveType.BalloonExpandable));

        Assert.Equal(24.0, r.BasisDiameterMm, 6);
        Assert.Equal(24.0, r.RecommendedMinDiameterMm, 6);
        Assert.Equal(26.4, r.RecommendedMaxDiameterMm, 6);
    }

    [Fact]
    public void Size_sev_applies_five_to_fifteen_percent_band()
    {
        ValveSizingResult r = ValveSizingService.Size(HealthyInput(ValveType.SelfExpanding));

        Assert.Equal(25.2, r.RecommendedMinDiameterMm, 6);
        Assert.Equal(27.6, r.RecommendedMaxDiameterMm, 6);
    }

    // ── Warning engine ────────────────────────────────────────────────────────

    [Fact]
    public void WarningEngine_healthy_input_has_no_warnings()
    {
        ValveSizingResult r = ValveSizingService.Size(HealthyInput(ValveType.BalloonExpandable));
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData(9.9)]
    public void WarningEngine_coronary_ostium_too_low(double heightMm)
    {
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { CoronaryOstiumHeightMm = heightMm };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.Contains(warnings, w => w.RuleKey == "coronary-ostium-too-low" && w.Severity == TaviWarningSeverity.Critical);
    }

    [Fact]
    public void WarningEngine_coronary_ostium_at_threshold_no_warning()
    {
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { CoronaryOstiumHeightMm = 10.0 };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.DoesNotContain(warnings, w => w.RuleKey == "coronary-ostium-too-low");
    }

    [Fact]
    public void WarningEngine_coronary_ostium_ok_no_warning()
    {
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { CoronaryOstiumHeightMm = 10.1 };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.DoesNotContain(warnings, w => w.RuleKey == "coronary-ostium-too-low");
    }

    [Fact]
    public void WarningEngine_lvot_too_small()
    {
        // LVOT ratio 0.79 < 0.8.
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { LvotDiameterMm = 18.9 };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.Contains(warnings, w => w.RuleKey == "lvot-too-small" && w.Severity == TaviWarningSeverity.Warning);
    }

    [Fact]
    public void WarningEngine_lvot_ok_no_warning()
    {
        // LVOT ratio 0.85 — clearly above the 0.8 threshold.
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { LvotDiameterMm = 20.4 };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.DoesNotContain(warnings, w => w.RuleKey == "lvot-too-small");
    }

    [Fact]
    public void WarningEngine_severe_calcium_emits_valvuloplasty_hint()
    {
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { CalciumSeverity = CalciumSeverity.Severe };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.Contains(warnings, w => w.RuleKey == "severe-calcium" && w.Severity == TaviWarningSeverity.Warning);
    }

    [Fact]
    public void WarningEngine_no_contralateral_access_emits_warning()
    {
        ValveSizingInput input = HealthyInput(ValveType.BalloonExpandable) with { ContralateralAccessOk = false };
        List<TaviWarning> warnings = ValveSizingService.RunWarningEngine(input);

        Assert.Contains(warnings, w => w.RuleKey == "contralateral-access" && w.Severity == TaviWarningSeverity.Warning);
    }
}
