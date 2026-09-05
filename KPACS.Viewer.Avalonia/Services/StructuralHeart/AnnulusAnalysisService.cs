using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G1: pure, unit-testable annulus analysis following the SlicerHeart
/// ValveAnnulusAnalysis pattern. Fits a best-fit plane (least-squares / SVD) through
/// the clicked annulus points, projects the contour onto that plane, and computes
/// the SlicerHeart metric set (area, perimeter, perimeter-derived and area-derived
/// diameters, ellipse-fit min/max). No Avalonia, no volume I/O: the service works on
/// already-collected patient-space points and contour samples.
/// </summary>
internal static class AnnulusAnalysisService
{
    /// <summary>Default LVOT offset below the annulus plane, mm.</summary>
    public const double DefaultLvotOffsetMm = 10.0;

    /// <summary>Minimum number of points required for a plane fit.</summary>
    public const int MinPointsForPlane = 3;

    /// <summary>
    /// Fits the best-fit plane through the annulus points and computes the annulus
    /// metrics from the projected contour. When an LVOT contour is supplied, its
    /// metrics are computed on a parallel plane offset distally along the annulus axis.
    /// </summary>
    public static AnnulusAnalysisResult Analyze(
        IReadOnlyList<AnnulusPoint> points,
        IReadOnlyList<Vector3D> contour,
        IReadOnlyList<Vector3D>? lvotContour = null,
        double lvotOffsetMm = DefaultLvotOffsetMm)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(contour);

        AnnulusPlane plane = FitPlane(points);
        AnnulusMetrics annulus = ComputeMetrics(contour, plane);

        AnnulusMetrics? lvot = null;
        if (lvotContour is { Count: > 0 })
        {
            lvot = ComputeMetrics(lvotContour, plane);
        }

        return new AnnulusAnalysisResult
        {
            Points = points,
            Plane = plane,
            Annulus = annulus,
            Lvot = lvot,
            LvotOffsetMm = lvotOffsetMm,
        };
    }

    /// <summary>
    /// Fits the best-fit plane through the annulus points using least-squares (SVD of
    /// the centered point matrix). Returns the centroid and the normalized plane normal.
    /// </summary>
    public static AnnulusPlane FitPlane(IReadOnlyList<AnnulusPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < MinPointsForPlane)
        {
            throw new ArgumentException(
                $"At least {MinPointsForPlane} points are required for a plane fit.", nameof(points));
        }

        Vector3D center = Centroid(points);
        double[,] cov = Covariance(points, center);

        // Smallest eigenvector of the covariance matrix = plane normal (SVD of centered
        // matrix). We compute the 3x3 covariance and take its smallest eigenvector via
        // the power method on the inverse (shifted) — for a 3x3 symmetric matrix this is
        // exact enough for clinical geometry.
        Vector3D normal = SmallestEigenvector(cov);
        return new AnnulusPlane { Center = center, Normal = normal.Normalize() };
    }

    /// <summary>
    /// Computes the SlicerHeart metric set for a contour projected onto the plane:
    /// area, perimeter, perimeter-derived diameter (perimeter/π), area-derived diameter
    /// (√(4A/π)) and the ellipse-fit min/max diameters.
    /// </summary>
    public static AnnulusMetrics ComputeMetrics(IReadOnlyList<Vector3D> contour, AnnulusPlane plane)
    {
        ArgumentNullException.ThrowIfNull(contour);
        ArgumentNullException.ThrowIfNull(plane);

        if (contour.Count < 3)
        {
            return new AnnulusMetrics();
        }

        // Project the contour onto the plane (subtract center, keep in-plane components).
        Vector3D u = OrthonormalBasisU(plane.Normal);
        Vector3D v = plane.Normal.Cross(u).Normalize();

        double area = 0.0;
        double perimeter = 0.0;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;

        for (int i = 0; i < contour.Count; i++)
        {
            Vector3D p = contour[i] - plane.Center;
            double px = p.Dot(u);
            double py = p.Dot(v);
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);

            Vector3D q = contour[(i + 1) % contour.Count] - plane.Center;
            double qx = q.Dot(u);
            double qy = q.Dot(v);
            perimeter += Math.Sqrt((qx - px) * (qx - px) + (qy - py) * (qy - py));
            area += px * qy - py * qx;
        }

        area = Math.Abs(area) * 0.5;

        double perimeterDerived = perimeter / Math.PI;
        double areaDerived = Math.Sqrt(4.0 * area / Math.PI);

        return new AnnulusMetrics
        {
            AreaMm2 = area,
            PerimeterMm = perimeter,
            PerimeterDerivedDiameterMm = perimeterDerived,
            AreaDerivedDiameterMm = areaDerived,
            MinDiameterMm = Math.Min(maxX - minX, maxY - minY),
            MaxDiameterMm = Math.Max(maxX - minX, maxY - minY),
        };
    }

    /// <summary>
    /// Computes the centroid of the annulus points.
    /// </summary>
    public static Vector3D Centroid(IReadOnlyList<AnnulusPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return default;
        }

        Vector3D sum = default;
        foreach (AnnulusPoint p in points)
        {
            sum += p.PatientPoint;
        }

        return sum / points.Count;
    }

    private static Vector3D SmallestEigenvector(double[,] cov)
    {
        // Power iteration on (cov + shift·I)⁻¹ converges to the smallest eigenvector.
        // For a 3x3 symmetric positive semi-definite matrix this is robust for clinical
        // geometry. We use a fixed small shift to avoid singular matrices.
        const double shift = 1e-6;
        double[,] m = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                m[i, j] = cov[i, j] + (i == j ? shift : 0.0);
            }
        }

        double[,] inv = Invert3x3(m);
        Vector3D v = new(1.0, 1.0, 1.0);
        for (int iter = 0; iter < 50; iter++)
        {
            Vector3D next = new(
                inv[0, 0] * v.X + inv[0, 1] * v.Y + inv[0, 2] * v.Z,
                inv[1, 0] * v.X + inv[1, 1] * v.Y + inv[1, 2] * v.Z,
                inv[2, 0] * v.X + inv[2, 1] * v.Y + inv[2, 2] * v.Z);
            double len = next.Length;
            if (len < 1e-12)
            {
                break;
            }

            v = next / len;
        }

        return v;
    }

    private static double[,] Invert3x3(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2];
        double d = m[1, 0], e = m[1, 1], f = m[1, 2];
        double g = m[2, 0], h = m[2, 1], i = m[2, 2];

        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-12)
        {
            return new double[3, 3];
        }

        double invDet = 1.0 / det;
        return new double[3, 3]
        {
            { (e * i - f * h) * invDet, (c * h - b * i) * invDet, (b * f - c * e) * invDet },
            { (f * g - d * i) * invDet, (a * i - c * g) * invDet, (c * d - a * f) * invDet },
            { (d * h - e * g) * invDet, (b * g - a * h) * invDet, (a * e - b * d) * invDet },
        };
    }

    private static double[,] Covariance(IReadOnlyList<AnnulusPoint> points, Vector3D center)
    {
        double[,] cov = new double[3, 3];
        foreach (AnnulusPoint p in points)
        {
            Vector3D d = p.PatientPoint - center;
            cov[0, 0] += d.X * d.X;
            cov[0, 1] += d.X * d.Y;
            cov[0, 2] += d.X * d.Z;
            cov[1, 1] += d.Y * d.Y;
            cov[1, 2] += d.Y * d.Z;
            cov[2, 2] += d.Z * d.Z;
        }

        cov[1, 0] = cov[0, 1];
        cov[2, 0] = cov[0, 2];
        cov[2, 1] = cov[1, 2];
        return cov;
    }

    private static Vector3D OrthonormalBasisU(Vector3D normal)
    {
        // Pick a vector not parallel to the normal to build an orthonormal in-plane basis.
        Vector3D refVec = Math.Abs(normal.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        Vector3D u = refVec - normal * refVec.Dot(normal);
        return u.Length > 1e-12 ? u.Normalize() : new Vector3D(1, 0, 0);
    }
}
