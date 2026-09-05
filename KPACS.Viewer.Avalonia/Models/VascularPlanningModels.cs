namespace KPACS.Viewer.Models;

public enum VascularPlanningMarkerKind
{
    ProximalNeckStart,
    ProximalNeckEnd,
    DistalLandingStart,
    DistalLandingEnd,
}

public sealed record VascularPlanningMarker
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public VascularPlanningMarkerKind Kind { get; init; }

    public int StationIndex { get; init; }

    public double ArcLengthMm { get; init; }

    public Vector3D PatientPoint { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record VascularDiameterSample
{
    public int StationIndex { get; init; }

    public double ArcLengthMm { get; init; }

    public double EquivalentDiameterMm { get; init; }

    public double MajorDiameterMm { get; init; }

    public double MinorDiameterMm { get; init; }
}

public sealed record VascularSpanMetrics
{
    public double? LengthMm { get; init; }

    public double? MeanEquivalentDiameterMm { get; init; }

    public double? MinEquivalentDiameterMm { get; init; }

    public double? MaxEquivalentDiameterMm { get; init; }

    public double? MeanMajorDiameterMm { get; init; }

    public double? MeanMinorDiameterMm { get; init; }

    public List<VascularDiameterSample> Samples { get; init; } = [];
}

public sealed record VascularPlanningMetrics
{
    public VascularSpanMetrics? ProximalNeck { get; init; }

    public VascularSpanMetrics? DistalLanding { get; init; }

    public double? NeckAngulationDegrees { get; init; }

    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Phase C2: neck taper (mm diameter change per 10 mm of neck length) with status.
    /// Null when the neck span has too few diameter samples to fit a slope.
    /// </summary>
    public VascularConicityMetrics? NeckConicity { get; init; }

    /// <summary>
    /// Phase C2: largest equivalent diameter found across the aneurysm sac span
    /// (distal-landing start up to the proximal-neck end). Null when no sac samples exist.
    /// </summary>
    public double? AneurysmMaxDiameterMm { get; init; }

    /// <summary>
    /// Phase C2: thrombus volume inside the proximal-neck span, in cm³. Null when no
    /// thrombus sub-mask was supplied.
    /// </summary>
    public double? NeckThrombusVolumeCm3 { get; init; }

    /// <summary>
    /// Phase C2: calcium volume inside the proximal-neck span, in cm³. Null when no
    /// calcium sub-mask was supplied.
    /// </summary>
    public double? NeckCalciumVolumeCm3 { get; init; }

    /// <summary>
    /// Phase C2: per-side iliac access-route assessment (left/right). Empty when no
    /// access-path centerlines are available.
    /// </summary>
    public List<VascularAccessPathMetrics> AccessPaths { get; init; } = [];
}

/// <summary>
/// Phase C2: clinical severity of a single EVAR planning metric against the reference table.
/// </summary>
public enum VascularMetricStatus
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>
/// Phase C2: neck conicity (taper) — the magnitude of the diameter slope across the neck,
/// normalised to mm of diameter change per 10 mm of neck length.
/// </summary>
public sealed record VascularConicityMetrics
{
    public double? ConicityMmPer10Mm { get; init; }

    public VascularMetricStatus Status { get; init; } = VascularMetricStatus.Unknown;
}

/// <summary>
/// Phase C2: iliac access-route assessment for one side of the pelvis.
/// </summary>
public sealed record VascularAccessPathMetrics
{
    /// <summary>"Left" or "Right" (or a preset label). Display-only.</summary>
    public string Side { get; init; } = string.Empty;

    public double? MinEquivalentDiameterMm { get; init; }

    public double? LengthMm { get; init; }

    /// <summary>Arc length / straight chord. 1.0 = perfectly straight; higher = more tortuous.</summary>
    public double? Tortuosity { get; init; }

    /// <summary>Calcium volume / lumen volume within the path extent. 0..1. Null when no calcium mask.</summary>
    public double? CalciumFraction { get; init; }

    /// <summary>Worst status across the individual access-path criteria.</summary>
    public VascularMetricStatus Status { get; init; } = VascularMetricStatus.Unknown;
}

public sealed record VascularPlanningBundle
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CenterlineSeedSetId { get; init; }

    public Guid? CenterlinePathId { get; init; }

    public Guid? SegmentationMaskId { get; init; }

    public List<VascularPlanningMarker> Markers { get; init; } = [];

    public VascularPlanningMetrics? Metrics { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public VascularPlanningMarker? GetMarker(VascularPlanningMarkerKind kind) =>
        Markers.FirstOrDefault(marker => marker.Kind == kind);

    public VascularPlanningBundle UpsertMarker(VascularPlanningMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        List<VascularPlanningMarker> updatedMarkers = [.. Markers.Where(existing => existing.Kind != marker.Kind), marker with { UpdatedUtc = DateTimeOffset.UtcNow }];
        updatedMarkers.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        return this with { Markers = updatedMarkers, UpdatedUtc = DateTimeOffset.UtcNow };
    }

    public VascularPlanningBundle RemoveMarker(VascularPlanningMarkerKind kind)
    {
        if (!Markers.Any(marker => marker.Kind == kind))
        {
            return this;
        }

        return this with
        {
            Markers = [.. Markers.Where(marker => marker.Kind != kind)],
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    public VascularPlanningBundle WithMetrics(VascularPlanningMetrics? metrics, Guid? centerlinePathId, Guid? segmentationMaskId) =>
        this with
        {
            Metrics = metrics,
            CenterlinePathId = centerlinePathId,
            SegmentationMaskId = segmentationMaskId,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
}
