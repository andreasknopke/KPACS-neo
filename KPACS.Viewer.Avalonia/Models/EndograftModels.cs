namespace KPACS.Viewer.Models;

/// <summary>
/// Phase D: severity of a single endograft sizing warning, mirroring the clinical
/// EVAR planning status vocabulary used elsewhere in the workspace.
/// </summary>
public enum EndograftWarningSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Phase D: a single structured sizing warning produced by the warning engine. Each
/// warning references the affected measurement (e.g. "Neck-Länge", "Zugang Links") so
/// the report and UI can point the clinician at the underlying value.
/// </summary>
public sealed record EndograftWarning
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public EndograftWarningSeverity Severity { get; init; }

    /// <summary>Stable rule key, e.g. "neck-too-short", "limb-negative-length".</summary>
    public string RuleKey { get; init; } = string.Empty;

    /// <summary>Human-readable message (no PHI).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Display name of the affected measurement, e.g. "Neck-Länge".</summary>
    public string AffectedMeasurement { get; init; } = string.Empty;
}

/// <summary>
/// Phase D: one graft component (e.g. the aortic body or an iliac limb). A component is
/// a tapered tube spanning a station range along the reference centerline, with a proximal
/// and distal diameter and a nominal length.
/// </summary>
public sealed record GraftComponent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name, e.g. "Aorten-Body", "Iliakal-Limb Links".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Proximal (inflow) diameter in mm.</summary>
    public double ProximalDiameterMm { get; init; }

    /// <summary>Distal (outflow) diameter in mm.</summary>
    public double DistalDiameterMm { get; init; }

    /// <summary>Nominal component length in mm.</summary>
    public double LengthMm { get; init; }

    /// <summary>Start station (mm along the reference centerline from the proximal origin).</summary>
    public double StartStationMm { get; init; }

    /// <summary>End station (mm along the reference centerline).</summary>
    public double EndStationMm { get; init; }
}

/// <summary>
/// Phase D: the complete endograft plan — a list of components plus the sizing inputs
/// (neck diameter, oversizing percentages, landing overlaps) that produced them.
/// </summary>
public sealed record EndograftPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Reference centerline path id the plan is measured against.</summary>
    public Guid? CenterlinePathId { get; init; }

    /// <summary>Neck equivalent diameter (mm) used as the proximal sizing basis.</summary>
    public double? NeckDiameterMm { get; init; }

    /// <summary>Proximal oversizing fraction (0.10–0.20, recommended 0.15).</summary>
    public double ProximalOversizing { get; init; } = 0.15;

    /// <summary>Distal oversizing fraction (0.10–0.15).</summary>
    public double DistalOversizing { get; init; } = 0.12;

    /// <summary>Required proximal landing overlap (mm).</summary>
    public double ProximalLandingOverlapMm { get; init; } = 15.0;

    /// <summary>Required iliac landing overlap (mm).</summary>
    public double IliacLandingOverlapMm { get; init; } = 20.0;

    /// <summary>The graft components (aortic body + iliac limbs).</summary>
    public List<GraftComponent> Components { get; init; } = [];

    /// <summary>Structured warnings from the sizing rule engine.</summary>
    public List<EndograftWarning> Warnings { get; init; } = [];

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Recommended proximal (aortic body) diameter after oversizing.</summary>
    public double? RecommendedProximalDiameterMm { get; init; }

    /// <summary>Recommended distal (iliac limb) diameter after oversizing.</summary>
    public double? RecommendedDistalDiameterMm { get; init; }
}
