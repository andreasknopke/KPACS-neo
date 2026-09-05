namespace KPACS.Viewer.Models;

/// <summary>
/// Phase G: severity of a single TAVI planning warning, mirroring the structured
/// warning vocabulary used by the EVAR endograft sizing (Phase D).
/// </summary>
public enum TaviWarningSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Phase G: a single structured TAVI sizing warning produced by the valve sizing
/// rule engine. Each warning references the affected measurement so the report and
/// UI can point the clinician at the underlying value.
/// </summary>
public sealed record TaviWarning
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public TaviWarningSeverity Severity { get; init; }

    /// <summary>Stable rule key, e.g. "coronary-ostium-too-low", "severe-calcium".</summary>
    public string RuleKey { get; init; } = string.Empty;

    /// <summary>Human-readable message (no PHI).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Display name of the affected measurement, e.g. "Koronarostium".</summary>
    public string AffectedMeasurement { get; init; } = string.Empty;
}

/// <summary>
/// Phase G: a single annulus point clicked on an MPR plane (patient space). The
/// recommended set is the nodulus height plus the left/right/non-coronary leaflet
/// hinge points, but any three or more non-collinear points define the plane.
/// </summary>
public sealed record AnnulusPoint
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Patient-space position of the click.</summary>
    public Vector3D PatientPoint { get; init; }

    /// <summary>Optional semantic label, e.g. "Nodulus", "Left", "Right", "NonCoronary".</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Phase G: the best-fit plane through the annulus points (least-squares / SVD).
/// The normal is the annulus axis; the plane is used for en-face reformatting and
/// for the LVOT offset measurement.
/// </summary>
public sealed record AnnulusPlane
{
    /// <summary>Centroid of the annulus points (patient space).</summary>
    public Vector3D Center { get; init; }

    /// <summary>Normalized plane normal (annulus axis).</summary>
    public Vector3D Normal { get; init; }
}

/// <summary>
/// Phase G: the SlicerHeart-style annulus metric set — area, perimeter, the
/// perimeter-derived diameter (primary sizing basis, 3mensio convention), the
/// area-derived diameter, and the ellipse-fit min/max diameters.
/// </summary>
public sealed record AnnulusMetrics
{
    /// <summary>Enclosed area of the projected contour, mm².</summary>
    public double AreaMm2 { get; init; }

    /// <summary>Perimeter of the projected contour, mm.</summary>
    public double PerimeterMm { get; init; }

    /// <summary>Perimeter / π, mm.</summary>
    public double PerimeterDerivedDiameterMm { get; init; }

    /// <summary>√(4·Area/π), mm.</summary>
    public double AreaDerivedDiameterMm { get; init; }

    /// <summary>Minor ellipse-fit diameter, mm.</summary>
    public double MinDiameterMm { get; init; }

    /// <summary>Major ellipse-fit diameter, mm.</summary>
    public double MaxDiameterMm { get; init; }
}

/// <summary>
/// Phase G: the complete annulus analysis — the fitted plane, the annulus contour
/// metrics and (optionally) the LVOT contour metrics measured on a parallel plane
/// offset distally along the annulus axis.
/// </summary>
public sealed record AnnulusAnalysisResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The annulus points used for the fit.</summary>
    public IReadOnlyList<AnnulusPoint> Points { get; init; } = [];

    /// <summary>The best-fit annulus plane.</summary>
    public AnnulusPlane Plane { get; init; } = new();

    /// <summary>Annulus contour metrics.</summary>
    public AnnulusMetrics Annulus { get; init; } = new();

    /// <summary>LVOT contour metrics, when an LVOT contour was supplied.</summary>
    public AnnulusMetrics? Lvot { get; init; }

    /// <summary>Distal offset of the LVOT plane from the annulus plane, mm.</summary>
    public double LvotOffsetMm { get; init; } = 10.0;
}

/// <summary>
/// Phase G: severity classification of leaflet calcification (Agatston-like score).
/// </summary>
public enum CalciumSeverity
{
    None = 0,
    Light = 1,
    Moderate = 2,
    Severe = 3,
}

/// <summary>
/// Phase G: result of the leaflet calcium analysis — total calcium volume and an
/// Agatston-like score computed from HU density bands, plus a severity class.
/// </summary>
public sealed record LeafletCalciumResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Total calcium volume, mm³.</summary>
    public double VolumeMm3 { get; init; }

    /// <summary>Agatston-like score (HU-band factor × area).</summary>
    public double AgatstonScore { get; init; }

    /// <summary>Severity classification derived from the score.</summary>
    public CalciumSeverity Severity { get; init; }
}

/// <summary>
/// Phase G: metrics of a single coronary ostium (LCA or RCA) relative to the
/// annulus plane — axial height along the annulus axis, horizontal distance to the
/// annulus center, and the angle to the annulus plane.
/// </summary>
public sealed record CoronaryOstiumResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Ostium label, e.g. "LCA" or "RCA".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Signed distance from the annulus plane along the annulus axis, mm.</summary>
    public double AxialHeightMm { get; init; }

    /// <summary>Horizontal (in-plane) distance from the annulus center, mm.</summary>
    public double HorizontalDistanceMm { get; init; }

    /// <summary>Angle between the ostium vector and the annulus plane, degrees.</summary>
    public double AngleToPlaneDegrees { get; init; }
}

/// <summary>
/// Phase G: recommended C-arm angulation (LAO/RAO + CRA/CAU) to view the annulus
/// en-face, computed geometrically from the annulus plane normal relative to the
/// patient AP axis.
/// </summary>
public sealed record CarmAngulationResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Positive = LAO, negative = RAO, degrees.</summary>
    public double LaoRaoDegrees { get; init; }

    /// <summary>Positive = CRA (cranial), negative = CAU (caudal), degrees.</summary>
    public double CraCauDegrees { get; init; }
}

/// <summary>
/// Phase G: valve expansion mechanism — balloon-expandable (BEV) vs self-expanding
/// (SEV). Drives the oversizing rule band.
/// </summary>
public enum ValveType
{
    BalloonExpandable = 0,
    SelfExpanding = 1,
}

/// <summary>
/// Phase G: input to the valve sizing rule engine. The basis diameter is the
/// perimeter-derived annulus diameter by default (3mensio convention), with the
/// area-derived or mean diameter available as alternatives.
/// </summary>
public sealed record ValveSizingInput
{
    public ValveType ValveType { get; init; }

    /// <summary>Primary sizing basis diameter, mm (perimeter-derived by default).</summary>
    public double BasisDiameterMm { get; init; }

    /// <summary>LVOT diameter, mm (optional, for the LVOT-too-small rule).</summary>
    public double? LvotDiameterMm { get; init; }

    /// <summary>Coronary ostium height above the annulus plane, mm (optional).</summary>
    public double? CoronaryOstiumHeightMm { get; init; }

    /// <summary>Leaflet calcium severity (optional, for the valvuloplasty rule).</summary>
    public CalciumSeverity CalciumSeverity { get; init; } = CalciumSeverity.None;

    /// <summary>Whether a contralateral access path is available (optional).</summary>
    public bool ContralateralAccessOk { get; init; } = true;
}

/// <summary>
/// Phase G: the valve sizing result — the recommended diameter band (min–max) for
/// the chosen valve type plus any structured risk warnings.
/// </summary>
public sealed record ValveSizingResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ValveType ValveType { get; init; }

    /// <summary>The basis diameter used for sizing, mm.</summary>
    public double BasisDiameterMm { get; init; }

    /// <summary>Lower bound of the recommended valve diameter, mm.</summary>
    public double RecommendedMinDiameterMm { get; init; }

    /// <summary>Upper bound of the recommended valve diameter, mm.</summary>
    public double RecommendedMaxDiameterMm { get; init; }

    /// <summary>Structured risk warnings.</summary>
    public IReadOnlyList<TaviWarning> Warnings { get; init; } = [];
}

/// <summary>
/// Phase G6: a serializable bundle of the complete TAVI planning state — the annulus
/// points and best-fit plane, the annulus and LVOT metrics, the coronary ostium
/// results, the leaflet calcium result, the C-arm angulation, and the valve sizing
/// result. This is the unit persisted inside the measurement-session envelope and
/// rendered into the TAVI report section.
///
/// The bundle deliberately contains no PHI: it stores only geometry, metrics, and
/// stable identifiers.
/// </summary>
public sealed record TaviPlanningBundle
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The annulus points used for the plane fit (patient space).</summary>
    public IReadOnlyList<AnnulusPoint> Points { get; init; } = [];

    /// <summary>The best-fit annulus plane.</summary>
    public AnnulusPlane? Plane { get; init; }

    /// <summary>Annulus contour metrics.</summary>
    public AnnulusMetrics? Annulus { get; init; }

    /// <summary>LVOT contour metrics (parallel plane offset distally).</summary>
    public AnnulusMetrics? Lvot { get; init; }

    /// <summary>Distal offset of the LVOT plane from the annulus plane, mm.</summary>
    public double LvotOffsetMm { get; init; } = 10.0;

    /// <summary>Coronary ostium results (LCA/RCA), when measured.</summary>
    public IReadOnlyList<CoronaryOstiumResult> CoronaryOstia { get; init; } = [];

    /// <summary>Leaflet calcium result, when measured.</summary>
    public LeafletCalciumResult? Calcium { get; init; }

    /// <summary>Recommended C-arm angulation, when computed.</summary>
    public CarmAngulationResult? CarmAngulation { get; init; }

    /// <summary>Valve sizing result, when computed.</summary>
    public ValveSizingResult? Sizing { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
