using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Phase D: pure, unit-testable endograft sizing. Hersteller-agnostisch — the service
/// applies neutral oversizing percentages to the measured neck/distal diameters, builds
/// the graft components (aortic body + iliac limbs) from station ranges, and runs a
/// structured warning engine. No Avalonia, no volume I/O: every method works on already
/// measured values so the wiring layer only supplies numbers.
/// </summary>
internal static class EndograftSizingService
{
    /// <summary>
    /// Default sizing parameters (industry-typical IFU cut-offs). Kept as a single table so
    /// the service and any UI legend read the same numbers.
    /// </summary>
    public static class Defaults
    {
        /// <summary>Recommended proximal oversizing (10–20 %, midpoint).</summary>
        public const double ProximalOversizing = 0.15;

        /// <summary>Recommended distal oversizing (10–15 %).</summary>
        public const double DistalOversizing = 0.12;

        /// <summary>Required proximal (aortic) landing overlap, mm.</summary>
        public const double ProximalLandingOverlapMm = 15.0;

        /// <summary>Required iliac landing overlap, mm.</summary>
        public const double IliacLandingOverlapMm = 20.0;

        /// <summary>Aortic body ends this far (mm) proximal of the lowest renal ostium.</summary>
        public const double AorticEndProximalToLowestRenalMm = 2.0;

        /// <summary>Proximal oversizing below this is too little (risk of endoleak).</summary>
        public const double MinProximalOversizing = 0.10;

        /// <summary>Proximal oversizing above this is too much (risk of neck dilatation).</summary>
        public const double MaxProximalOversizing = 0.20;

        /// <summary>Distal oversizing below this is too little.</summary>
        public const double MinDistalOversizing = 0.10;

        /// <summary>Distal oversizing above this is too much.</summary>
        public const double MaxDistalOversizing = 0.15;
    }

    /// <summary>
    /// Computes the recommended graft diameters, builds the components and runs the warning
    /// engine. Returns a fully-populated <see cref="EndograftPlan"/>.
    /// </summary>
    public static EndograftPlan Size(EndograftSizingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        double? proximalDiameter = ApplyOversizing(input.NeckDiameterMm, input.ProximalOversizing);
        double? distalDiameter = ApplyOversizing(input.DistalLandingDiameterMm, input.DistalOversizing);

        List<GraftComponent> components = BuildComponents(input, proximalDiameter, distalDiameter);
        List<EndograftWarning> warnings = RunWarningEngine(input, proximalDiameter, distalDiameter, components);

        return new EndograftPlan
        {
            NeckDiameterMm = input.NeckDiameterMm,
            ProximalOversizing = input.ProximalOversizing,
            DistalOversizing = input.DistalOversizing,
            ProximalLandingOverlapMm = input.ProximalLandingOverlapMm,
            IliacLandingOverlapMm = input.IliacLandingOverlapMm,
            Components = components,
            Warnings = warnings,
            RecommendedProximalDiameterMm = proximalDiameter,
            RecommendedDistalDiameterMm = distalDiameter,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Applies an oversizing fraction to a measured diameter. Returns null when the diameter
    /// is missing or non-positive.
    /// </summary>
    public static double? ApplyOversizing(double? diameterMm, double oversizing)
    {
        if (diameterMm is null || diameterMm <= 0)
        {
            return null;
        }

        return diameterMm.Value * (1.0 + oversizing);
    }

    /// <summary>
    /// Builds the graft components: one aortic body (when neck/aortic stations are present)
    /// plus one iliac limb per access path (when distal-landing stations are present).
    /// </summary>
    public static List<GraftComponent> BuildComponents(
        EndograftSizingInput input,
        double? proximalDiameterMm,
        double? distalDiameterMm)
    {
        List<GraftComponent> components = [];

        if (input.ProximalNeckStartStationMm is double neckStart &&
            input.AorticEndStationMm is double aorticEnd)
        {
            components.Add(new GraftComponent
            {
                Name = "Aorten-Body",
                ProximalDiameterMm = proximalDiameterMm ?? 0,
                DistalDiameterMm = distalDiameterMm ?? proximalDiameterMm ?? 0,
                LengthMm = Math.Max(0, aorticEnd - neckStart),
                StartStationMm = neckStart,
                EndStationMm = aorticEnd,
            });
        }

        if (input.DistalLandingStartStationMm is double limbStart &&
            input.DistalLandingEndStationMm is double limbEnd)
        {
            foreach (VascularAccessPathMetrics access in input.AccessPaths)
            {
                components.Add(new GraftComponent
                {
                    Name = $"Iliakal-Limb {access.Side}",
                    ProximalDiameterMm = distalDiameterMm ?? 0,
                    DistalDiameterMm = distalDiameterMm ?? 0,
                    LengthMm = Math.Max(0, limbEnd - limbStart),
                    StartStationMm = limbStart,
                    EndStationMm = limbEnd,
                });
            }
        }

        return components;
    }

    /// <summary>
    /// Runs the structured warning engine. Each rule emits a warning with a severity, a stable
    /// rule key and the affected measurement name. Rules: neck too short, neck conicity, neck
    /// angulation, access too small, access calcified, limb negative length, material conflict
    /// (limb too large for the access vessel), landing overlap too short, oversizing out of range.
    /// </summary>
    public static List<EndograftWarning> RunWarningEngine(
        EndograftSizingInput input,
        double? proximalDiameterMm,
        double? distalDiameterMm,
        IReadOnlyList<GraftComponent> components)
    {
        List<EndograftWarning> warnings = [];

        // Neck too short.
        if (input.NeckLengthMm is double neckLen)
        {
            if (neckLen <= VascularExtendedMetricsHelper.ReferenceRanges.NeckLengthCriticalMm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Critical, "neck-too-short",
                    $"Neck-Länge {neckLen:F1} mm ist kritisch kurz (≤ {VascularExtendedMetricsHelper.ReferenceRanges.NeckLengthCriticalMm:F0} mm).",
                    "Neck-Länge"));
            }
            else if (neckLen <= VascularExtendedMetricsHelper.ReferenceRanges.NeckLengthWarningMm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Warning, "neck-too-short",
                    $"Neck-Länge {neckLen:F1} mm ist kurz (≤ {VascularExtendedMetricsHelper.ReferenceRanges.NeckLengthWarningMm:F0} mm).",
                    "Neck-Länge"));
            }
        }

        // Neck conicity.
        if (input.NeckConicityMmPer10Mm is double conicity)
        {
            if (conicity >= VascularExtendedMetricsHelper.ReferenceRanges.ConicityCriticalMmPer10Mm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Critical, "neck-conicity",
                    $"Konizität {conicity:F2} mm/10mm ist kritisch (≥ {VascularExtendedMetricsHelper.ReferenceRanges.ConicityCriticalMmPer10Mm:F1}).",
                    "Konizität"));
            }
            else if (conicity >= VascularExtendedMetricsHelper.ReferenceRanges.ConicityWarningMmPer10Mm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Warning, "neck-conicity",
                    $"Konizität {conicity:F2} mm/10mm ist erhöht (≥ {VascularExtendedMetricsHelper.ReferenceRanges.ConicityWarningMmPer10Mm:F1}).",
                    "Konizität"));
            }
        }

        // Neck angulation.
        if (input.NeckAngulationDegrees is double angulation)
        {
            if (angulation >= VascularExtendedMetricsHelper.ReferenceRanges.AngulationCriticalDeg)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Critical, "neck-angulation",
                    $"Angulation {angulation:F0}° ist kritisch (≥ {VascularExtendedMetricsHelper.ReferenceRanges.AngulationCriticalDeg:F0}°).",
                    "Angulation"));
            }
            else if (angulation >= VascularExtendedMetricsHelper.ReferenceRanges.AngulationWarningDeg)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Warning, "neck-angulation",
                    $"Angulation {angulation:F0}° ist erhöht (≥ {VascularExtendedMetricsHelper.ReferenceRanges.AngulationWarningDeg:F0}°).",
                    "Angulation"));
            }
        }

        // Access too small / calcified / material conflict, per side.
        foreach (VascularAccessPathMetrics access in input.AccessPaths)
        {
            string affected = $"Zugang {access.Side}";

            if (access.MinEquivalentDiameterMm is double minDia)
            {
                if (minDia <= VascularExtendedMetricsHelper.ReferenceRanges.AccessDiameterCriticalMm)
                {
                    warnings.Add(Warning(EndograftWarningSeverity.Critical, "access-too-small",
                        $"{affected}: min Ø {minDia:F1} mm ist kritisch klein (≤ {VascularExtendedMetricsHelper.ReferenceRanges.AccessDiameterCriticalMm:F0} mm).",
                        affected));
                }
                else if (minDia <= VascularExtendedMetricsHelper.ReferenceRanges.AccessDiameterWarningMm)
                {
                    warnings.Add(Warning(EndograftWarningSeverity.Warning, "access-too-small",
                        $"{affected}: min Ø {minDia:F1} mm ist klein (≤ {VascularExtendedMetricsHelper.ReferenceRanges.AccessDiameterWarningMm:F0} mm).",
                        affected));
                }
            }

            if (access.CalciumFraction is double calc)
            {
                if (calc >= VascularExtendedMetricsHelper.ReferenceRanges.AccessCalciumCriticalFraction)
                {
                    warnings.Add(Warning(EndograftWarningSeverity.Critical, "access-calcified",
                        $"{affected}: Kalk-Anteil {calc * 100:F0} % ist kritisch (≥ {VascularExtendedMetricsHelper.ReferenceRanges.AccessCalciumCriticalFraction * 100:F0} %).",
                        affected));
                }
                else if (calc >= VascularExtendedMetricsHelper.ReferenceRanges.AccessCalciumWarningFraction)
                {
                    warnings.Add(Warning(EndograftWarningSeverity.Warning, "access-calcified",
                        $"{affected}: Kalk-Anteil {calc * 100:F0} % ist erhöht (≥ {VascularExtendedMetricsHelper.ReferenceRanges.AccessCalciumWarningFraction * 100:F0} %).",
                        affected));
                }
            }

            // Material conflict: the recommended limb diameter must fit through the access vessel.
            if (distalDiameterMm is double limbDia &&
                access.MinEquivalentDiameterMm is double accessDia &&
                limbDia > accessDia)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Critical, "material-conflict",
                    $"{affected}: empfohlener Limb-Ø {limbDia:F1} mm übersteigt Zugangsweg min Ø {accessDia:F1} mm — Einführung nicht möglich.",
                    affected));
            }
        }

        // Limb negative length + landing overlap, per component.
        foreach (GraftComponent component in components)
        {
            double length = component.EndStationMm - component.StartStationMm;
            if (length < 0)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Critical, "limb-negative-length",
                    $"{component.Name}: End-Station {component.EndStationMm:F1} mm liegt proximal der Start-Station {component.StartStationMm:F1} mm.",
                    component.Name));
                continue;
            }

            if (component.Name.StartsWith("Aorten", StringComparison.OrdinalIgnoreCase) &&
                length < input.ProximalLandingOverlapMm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Warning, "proximal-landing-short",
                    $"{component.Name}: Länge {length:F1} mm ist kürzer als die erforderliche proximale Überlappung {input.ProximalLandingOverlapMm:F0} mm.",
                    component.Name));
            }
            else if (component.Name.StartsWith("Iliakal", StringComparison.OrdinalIgnoreCase) &&
                     length < input.IliacLandingOverlapMm)
            {
                warnings.Add(Warning(EndograftWarningSeverity.Warning, "iliac-landing-short",
                    $"{component.Name}: Länge {length:F1} mm ist kürzer als die erforderliche Iliakal-Überlappung {input.IliacLandingOverlapMm:F0} mm.",
                    component.Name));
            }
        }

        // Oversizing out of range.
        if (input.ProximalOversizing < Defaults.MinProximalOversizing ||
            input.ProximalOversizing > Defaults.MaxProximalOversizing)
        {
            warnings.Add(Warning(EndograftWarningSeverity.Warning, "oversizing-out-of-range",
                $"Proximale Oversizing {input.ProximalOversizing:P0} liegt ausserhalb {Defaults.MinProximalOversizing:P0}–{Defaults.MaxProximalOversizing:P0}.",
                "Oversizing"));
        }

        if (input.DistalOversizing < Defaults.MinDistalOversizing ||
            input.DistalOversizing > Defaults.MaxDistalOversizing)
        {
            warnings.Add(Warning(EndograftWarningSeverity.Warning, "oversizing-out-of-range",
                $"Distale Oversizing {input.DistalOversizing:P0} liegt ausserhalb {Defaults.MinDistalOversizing:P0}–{Defaults.MaxDistalOversizing:P0}.",
                "Oversizing"));
        }

        return warnings;
    }

    private static EndograftWarning Warning(
        EndograftWarningSeverity severity,
        string ruleKey,
        string message,
        string affectedMeasurement) =>
        new()
        {
            Severity = severity,
            RuleKey = ruleKey,
            Message = message,
            AffectedMeasurement = affectedMeasurement,
        };
}

/// <summary>
/// Phase D: all measured inputs the sizing service needs. Kept as a record so the wiring
/// layer can populate it from the workspace's planning bundle + vessel tree in one place.
/// </summary>
public sealed record EndograftSizingInput
{
    /// <summary>Neck equivalent diameter (mm) — proximal sizing basis.</summary>
    public double? NeckDiameterMm { get; init; }

    /// <summary>Proximal neck length (mm).</summary>
    public double? NeckLengthMm { get; init; }

    /// <summary>Neck conicity (mm/10 mm).</summary>
    public double? NeckConicityMmPer10Mm { get; init; }

    /// <summary>Neck angulation (deg).</summary>
    public double? NeckAngulationDegrees { get; init; }

    /// <summary>Distal landing equivalent diameter (mm) — distal sizing basis.</summary>
    public double? DistalLandingDiameterMm { get; init; }

    /// <summary>Proximal-neck start station (mm along the reference centerline).</summary>
    public double? ProximalNeckStartStationMm { get; init; }

    /// <summary>Aortic body end station (mm) — 2 mm proximal of the lowest renal ostium.</summary>
    public double? AorticEndStationMm { get; init; }

    /// <summary>Distal-landing start station (mm).</summary>
    public double? DistalLandingStartStationMm { get; init; }

    /// <summary>Distal-landing end station (mm).</summary>
    public double? DistalLandingEndStationMm { get; init; }

    /// <summary>Per-side iliac access-route assessments.</summary>
    public IReadOnlyList<VascularAccessPathMetrics> AccessPaths { get; init; } = [];

    /// <summary>Proximal oversizing fraction (0.10–0.20, recommended 0.15).</summary>
    public double ProximalOversizing { get; init; } = EndograftSizingService.Defaults.ProximalOversizing;

    /// <summary>Distal oversizing fraction (0.10–0.15).</summary>
    public double DistalOversizing { get; init; } = EndograftSizingService.Defaults.DistalOversizing;

    /// <summary>Required proximal landing overlap (mm).</summary>
    public double ProximalLandingOverlapMm { get; init; } = EndograftSizingService.Defaults.ProximalLandingOverlapMm;

    /// <summary>Required iliac landing overlap (mm).</summary>
    public double IliacLandingOverlapMm { get; init; } = EndograftSizingService.Defaults.IliacLandingOverlapMm;
}
