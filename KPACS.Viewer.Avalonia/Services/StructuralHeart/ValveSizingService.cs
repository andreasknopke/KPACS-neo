using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G5: pure, unit-testable valve sizing rule engine. Hersteller-neutral: the
/// service applies transparent oversizing bands for balloon-expandable (BEV) and
/// self-expanding (SEV) valves to the basis diameter (perimeter-derived by default),
/// and runs a structured risk-warning engine. No Avalonia, no volume I/O.
/// </summary>
internal static class ValveSizingService
{
    /// <summary>BEV oversizing band (fraction of the basis diameter).</summary>
    public static readonly (double Min, double Max) BevBand = (0.0, 0.10);

    /// <summary>SEV oversizing band (fraction of the basis diameter).</summary>
    public static readonly (double Min, double Max) SevBand = (0.05, 0.15);

    /// <summary>Coronary ostium height below this (mm) is a critical risk.</summary>
    public const double CoronaryOstiumCriticalMm = 10.0;

    /// <summary>LVOT diameter below this (mm) relative to the basis is a risk.</summary>
    public const double LvotMinRatio = 0.8;

    /// <summary>
    /// Sizes a valve for the given input: computes the recommended diameter band for
    /// the valve type and runs the risk-warning engine.
    /// </summary>
    public static ValveSizingResult Size(ValveSizingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        (double min, double max) = input.ValveType == ValveType.BalloonExpandable ? BevBand : SevBand;
        double basis = input.BasisDiameterMm;
        double recMin = basis * (1.0 + min);
        double recMax = basis * (1.0 + max);

        List<TaviWarning> warnings = RunWarningEngine(input);

        return new ValveSizingResult
        {
            ValveType = input.ValveType,
            BasisDiameterMm = basis,
            RecommendedMinDiameterMm = recMin,
            RecommendedMaxDiameterMm = recMax,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Runs the structured risk-warning engine. Rules: coronary ostium too low,
    /// LVOT too small, severe calcification (consider valvuloplasty), contralateral
    /// access not available.
    /// </summary>
    public static List<TaviWarning> RunWarningEngine(ValveSizingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<TaviWarning> warnings = [];

        if (input.CoronaryOstiumHeightMm is double ostiumHeight &&
            ostiumHeight < CoronaryOstiumCriticalMm)
        {
            warnings.Add(Warning(TaviWarningSeverity.Critical, "coronary-ostium-too-low",
                $"Koronarostium-Höhe {ostiumHeight:F1} mm ist kritisch niedrig (< {CoronaryOstiumCriticalMm:F0} mm).",
                "Koronarostium"));
        }

        if (input.LvotDiameterMm is double lvot &&
            input.BasisDiameterMm > 0 &&
            lvot < input.BasisDiameterMm * LvotMinRatio)
        {
            warnings.Add(Warning(TaviWarningSeverity.Warning, "lvot-too-small",
                $"LVOT-Durchmesser {lvot:F1} mm ist klein relativ zum Annulus-Basis ({input.BasisDiameterMm:F1} mm).",
                "LVOT"));
        }

        if (input.CalciumSeverity == CalciumSeverity.Severe)
        {
            warnings.Add(Warning(TaviWarningSeverity.Warning, "severe-calcium",
                "Schwere Verkalkung — Ballon-Valvuloplastie erwägen.",
                "Verkalkung"));
        }

        if (!input.ContralateralAccessOk)
        {
            warnings.Add(Warning(TaviWarningSeverity.Warning, "contralateral-access",
                "Kontralaterale Zugangsweg nicht verfügbar.",
                "Zugangsweg"));
        }

        return warnings;
    }

    private static TaviWarning Warning(TaviWarningSeverity severity, string ruleKey, string message, string affected)
    {
        return new TaviWarning
        {
            Severity = severity,
            RuleKey = ruleKey,
            Message = message,
            AffectedMeasurement = affected,
        };
    }
}
