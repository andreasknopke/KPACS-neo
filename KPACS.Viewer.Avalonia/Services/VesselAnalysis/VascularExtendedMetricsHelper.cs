using System.Globalization;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Phase C2: pure, unit-testable EVAR planning metrics that extend the base
/// <see cref="VascularPlanningMetricsService"/> span metrics — neck conicity, access-route
/// tortuosity, and the reference-range status classification. No Avalonia, no volume I/O:
/// every method works on already-sampled diameter/arc-length data or a centerline path.
/// All numeric formatting uses <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
internal static class VascularExtendedMetricsHelper
{
    /// <summary>
    /// Constant EVAR reference thresholds (industry-typical IFU cut-offs). Kept as a single
    /// table so the status classifiers and any UI legend read the same numbers.
    /// </summary>
    public static class ReferenceRanges
    {
        /// <summary>Proximal neck length below this (mm) is short / critical.</summary>
        public const double NeckLengthCriticalMm = 10.0;

        /// <summary>Proximal neck length below this (mm) warrants a warning.</summary>
        public const double NeckLengthWarningMm = 15.0;

        /// <summary>Neck equivalent diameter at or above this (mm) is critical (too wide).</summary>
        public const double NeckDiameterCriticalMm = 28.0;

        /// <summary>Neck equivalent diameter at or above this (mm) warrants a warning.</summary>
        public const double NeckDiameterWarningMm = 25.0;

        /// <summary>Neck conicity at or above this (mm/10 mm) is critical.</summary>
        public const double ConicityCriticalMmPer10Mm = 2.0;

        /// <summary>Neck conicity at or above this (mm/10 mm) warrants a warning.</summary>
        public const double ConicityWarningMmPer10Mm = 1.0;

        /// <summary>Neck angulation at or above this (deg) is critical.</summary>
        public const double AngulationCriticalDeg = 90.0;

        /// <summary>Neck angulation at or above this (deg) warrants a warning.</summary>
        public const double AngulationWarningDeg = 60.0;

        /// <summary>Iliac access minimum diameter below this (mm) is critical.</summary>
        public const double AccessDiameterCriticalMm = 6.0;

        /// <summary>Iliac access minimum diameter below this (mm) warrants a warning.</summary>
        public const double AccessDiameterWarningMm = 7.0;

        /// <summary>Access tortuosity at or above this is critical.</summary>
        public const double AccessTortuosityCritical = 1.6;

        /// <summary>Access tortuosity at or above this warrants a warning.</summary>
        public const double AccessTortuosityWarning = 1.3;

        /// <summary>Access calcium fraction at or above this is critical.</summary>
        public const double AccessCalciumCriticalFraction = 0.5;

        /// <summary>Access calcium fraction at or above this warrants a warning.</summary>
        public const double AccessCalciumWarningFraction = 0.25;
    }

    /// <summary>
    /// Least-squares slope of diameter (mm) against arc length (mm) across the neck span,
    /// expressed as absolute diameter change per 10 mm of length. Returns null when fewer
    /// than two samples carry a positive diameter, or when all samples share one arc length
    /// (no span to fit against).
    /// </summary>
    public static double? ComputeConicityMmPer10Mm(IReadOnlyList<(double ArcLengthMm, double DiameterMm)> samples)
    {
        if (samples is null || samples.Count < 2)
        {
            return null;
        }

        // Only usable diameter points participate; degenerate (non-positive) diameters are skipped.
        int n = 0;
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;
        foreach ((double arc, double dia) in samples)
        {
            if (dia <= 0)
            {
                continue;
            }

            n++;
            sumX += arc;
            sumY += dia;
            sumXX += arc * arc;
            sumXY += arc * dia;
        }

        if (n < 2)
        {
            return null;
        }

        double denominator = (n * sumXX) - (sumX * sumX);
        if (Math.Abs(denominator) < 1e-9)
        {
            // All samples at the same arc length — no slope is defined.
            return null;
        }

        double slopePerMm = ((n * sumXY) - (sumX * sumY)) / denominator;
        return Math.Abs(slopePerMm) * 10.0;
    }

    /// <summary>
    /// Conicity from the chart helper's point list, keeping the sampling representation in one place.
    /// </summary>
    public static double? ComputeConicityMmPer10Mm(IReadOnlyList<VascularDiameterChartHelper.ChartPoint> points)
    {
        if (points is null)
        {
            return null;
        }

        List<(double, double)> tuples = new(points.Count);
        foreach (VascularDiameterChartHelper.ChartPoint p in points)
        {
            tuples.Add((p.ArcLengthMm, p.DiameterMm));
        }

        return ComputeConicityMmPer10Mm(tuples);
    }

    /// <summary>
    /// Tortuosity of a centerline path: total arc length divided by the straight chord between
    /// its endpoints. 1.0 = perfectly straight; higher = more tortuous. Returns null for a path
    /// with fewer than two points or a collapsed (zero-length) chord.
    /// </summary>
    public static double? ComputeTortuosity(CenterlinePath? path)
    {
        if (path is null || path.Points.Count < 2)
        {
            return null;
        }

        Vector3D first = path.Points[0].PatientPoint;
        Vector3D last = path.Points[^1].PatientPoint;
        double chord = (last - first).Length;
        if (chord <= 1e-6)
        {
            return null;
        }

        double arc = path.TotalLengthMm;
        if (arc <= 0)
        {
            // Fall back to summing segment lengths when TotalLengthMm was not populated.
            arc = SumSegmentLength(path);
        }

        return arc / chord;
    }

    /// <summary>
    /// Minimum equivalent diameter across a set of diameter samples (mm). Null when no sample
    /// carries a positive diameter.
    /// </summary>
    public static double? ComputeMinDiameterMm(IReadOnlyList<double> diametersMm)
    {
        if (diametersMm is null || diametersMm.Count == 0)
        {
            return null;
        }

        double min = double.PositiveInfinity;
        bool any = false;
        foreach (double d in diametersMm)
        {
            if (d <= 0)
            {
                continue;
            }

            any = true;
            min = Math.Min(min, d);
        }

        return any ? min : null;
    }

    /// <summary>Neck-length status: shorter is worse.</summary>
    public static VascularMetricStatus ClassifyNeckLength(double? lengthMm) =>
        ClassifyLowerIsWorse(lengthMm, ReferenceRanges.NeckLengthCriticalMm, ReferenceRanges.NeckLengthWarningMm);

    /// <summary>Neck-diameter status: wider is worse.</summary>
    public static VascularMetricStatus ClassifyNeckDiameter(double? diameterMm) =>
        ClassifyHigherIsWorse(diameterMm, ReferenceRanges.NeckDiameterCriticalMm, ReferenceRanges.NeckDiameterWarningMm);

    /// <summary>Conicity status: steeper taper is worse.</summary>
    public static VascularMetricStatus ClassifyConicity(double? conicityMmPer10Mm) =>
        ClassifyHigherIsWorse(conicityMmPer10Mm, ReferenceRanges.ConicityCriticalMmPer10Mm, ReferenceRanges.ConicityWarningMmPer10Mm);

    /// <summary>Angulation status: larger neck bend angle is worse.</summary>
    public static VascularMetricStatus ClassifyAngulation(double? degrees) =>
        ClassifyHigherIsWorse(degrees, ReferenceRanges.AngulationCriticalDeg, ReferenceRanges.AngulationWarningDeg);

    /// <summary>Access-route minimum-diameter status: narrower is worse.</summary>
    public static VascularMetricStatus ClassifyAccessDiameter(double? minDiameterMm) =>
        ClassifyLowerIsWorse(minDiameterMm, ReferenceRanges.AccessDiameterCriticalMm, ReferenceRanges.AccessDiameterWarningMm);

    /// <summary>Access-route tortuosity status: higher ratio is worse.</summary>
    public static VascularMetricStatus ClassifyAccessTortuosity(double? tortuosity) =>
        ClassifyHigherIsWorse(tortuosity, ReferenceRanges.AccessTortuosityCritical, ReferenceRanges.AccessTortuosityWarning);

    /// <summary>Access-route calcium-fraction status: more calcium is worse.</summary>
    public static VascularMetricStatus ClassifyAccessCalcium(double? calciumFraction) =>
        ClassifyHigherIsWorse(calciumFraction, ReferenceRanges.AccessCalciumCriticalFraction, ReferenceRanges.AccessCalciumWarningFraction);

    /// <summary>
    /// Worst (highest-severity) status across the individual access-route criteria that have
    /// data. Unknown criteria are ignored; if none have data the result is Unknown.
    /// </summary>
    public static VascularMetricStatus ClassifyAccessPath(
        double? minDiameterMm,
        double? tortuosity,
        double? calciumFraction)
    {
        VascularMetricStatus worst = VascularMetricStatus.Unknown;
        worst = Worst(worst, ClassifyAccessDiameter(minDiameterMm));
        worst = Worst(worst, ClassifyAccessTortuosity(tortuosity));
        worst = Worst(worst, ClassifyAccessCalcium(calciumFraction));
        return worst;
    }

    /// <summary>
    /// Build a fully-classified access-path record from already-computed primitives. Kept pure so
    /// the wiring layer only has to supply diameter/tortuosity/calcium numbers.
    /// </summary>
    public static VascularAccessPathMetrics BuildAccessPath(
        string side,
        double? minDiameterMm,
        double? lengthMm,
        double? tortuosity,
        double? calciumFraction)
    {
        return new VascularAccessPathMetrics
        {
            Side = side ?? string.Empty,
            MinEquivalentDiameterMm = minDiameterMm,
            LengthMm = lengthMm,
            Tortuosity = tortuosity,
            CalciumFraction = calciumFraction,
            Status = ClassifyAccessPath(minDiameterMm, tortuosity, calciumFraction),
        };
    }

    /// <summary>
    /// Build a fully-classified conicity record from a computed conicity value.
    /// </summary>
    public static VascularConicityMetrics BuildConicity(double? conicityMmPer10Mm) =>
        new()
        {
            ConicityMmPer10Mm = conicityMmPer10Mm,
            Status = ClassifyConicity(conicityMmPer10Mm),
        };

    private static VascularMetricStatus ClassifyHigherIsWorse(double? value, double criticalAtOrAbove, double warningAtOrAbove)
    {
        if (value is null)
        {
            return VascularMetricStatus.Unknown;
        }

        double v = value.Value;
        if (v >= criticalAtOrAbove)
        {
            return VascularMetricStatus.Critical;
        }

        if (v >= warningAtOrAbove)
        {
            return VascularMetricStatus.Warning;
        }

        return VascularMetricStatus.Ok;
    }

    private static VascularMetricStatus ClassifyLowerIsWorse(double? value, double criticalAtOrBelow, double warningAtOrBelow)
    {
        if (value is null)
        {
            return VascularMetricStatus.Unknown;
        }

        double v = value.Value;
        if (v <= criticalAtOrBelow)
        {
            return VascularMetricStatus.Critical;
        }

        if (v <= warningAtOrBelow)
        {
            return VascularMetricStatus.Warning;
        }

        return VascularMetricStatus.Ok;
    }

    private static VascularMetricStatus Worst(VascularMetricStatus a, VascularMetricStatus b) =>
        (int)a >= (int)b ? a : b;

    private static double SumSegmentLength(CenterlinePath path)
    {
        double total = 0;
        for (int i = 1; i < path.Points.Count; i++)
        {
            total += (path.Points[i].PatientPoint - path.Points[i - 1].PatientPoint).Length;
        }

        return total;
    }
}
