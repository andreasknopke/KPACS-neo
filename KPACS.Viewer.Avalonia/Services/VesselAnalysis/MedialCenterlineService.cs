using System.Globalization;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Medial (max-inscribed-sphere) centerline extraction, the VMTK concept reimplemented in pure
/// C#: an exact Euclidean distance transform supplies a per-voxel inscribed radius, a
/// radius-weighted Dijkstra walks the vessel lumen preferring wide (central) voxels, each path
/// point is then refined onto the local distance ridge (the medial axis), and the result is
/// smoothed with a Catmull-Rom spline and resampled at a fixed spacing.
/// </summary>
/// <remarks>
/// Drop-in replacement for <see cref="CenterlineExtractionService"/> (same
/// <see cref="ICenterlineExtractionService"/> seam); the A* service stays available as a
/// quality fallback. Deterministic and offline; no GPU dependency.
/// </remarks>
internal sealed class MedialCenterlineService : ICenterlineExtractionService
{
    private const double ResampleSpacingMm = 1.0;
    private const double MedialRefineMaxShiftMm = 4.0;
    private const double MinRadiusMm = 0.05;
    private const int MaxDijkstraRelaxations = 4_000_000;

    private static readonly NeighborStep[] s_neighborSteps = CreateNeighborSteps();

    public CenterlineExtractionResult Extract(
        SegmentationMask3D mask,
        CenterlineSeedSet seedSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(seedSet);

        if (!seedSet.HasRequiredEndpoints)
        {
            return CenterlineExtractionResult.Failure("Centerline needs both start and end seeds.");
        }

        VolumeGridGeometry geometry = mask.Geometry;
        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(geometry, mask.Storage);
        if (buffer.ComputeStatistics() is null)
        {
            return CenterlineExtractionResult.Failure("The selected vessel mask is empty.");
        }

        IReadOnlyList<CenterlineSeed> orderedSeeds = seedSet.GetOrderedSeeds();
        if (orderedSeeds.Count < 2)
        {
            return CenterlineExtractionResult.Failure("Centerline needs at least two ordered seed points.");
        }

        DistanceField3D field = DistanceTransform3D.Compute(buffer, geometry);

        List<VoxelPoint> concatenated = [];
        for (int index = 0; index < orderedSeeds.Count - 1; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TrySnapToForeground(buffer, geometry, orderedSeeds[index].PatientPoint, out VoxelPoint start))
            {
                return CenterlineExtractionResult.Failure(
                    $"Could not snap the {orderedSeeds[index].Kind.ToString().ToLowerInvariant()} seed to the vessel mask.");
            }

            if (!TrySnapToForeground(buffer, geometry, orderedSeeds[index + 1].PatientPoint, out VoxelPoint end))
            {
                return CenterlineExtractionResult.Failure(
                    $"Could not snap the {orderedSeeds[index + 1].Kind.ToString().ToLowerInvariant()} seed to the vessel mask.");
            }

            List<VoxelPoint>? segment = RunRadiusWeightedDijkstra(buffer, field, geometry, start, end, cancellationToken);
            if (segment is null || segment.Count < 2)
            {
                return CenterlineExtractionResult.Failure(
                    "The radius-weighted search could not connect the seeds inside the vessel mask.");
            }

            int skip = index == 0 ? 0 : 1;
            for (int i = skip; i < segment.Count; i++)
            {
                concatenated.Add(segment[i]);
            }
        }

        if (concatenated.Count < 2)
        {
            return CenterlineExtractionResult.Failure("The computed centerline is too short.");
        }

        // Refine each voxel onto the local distance ridge (medial axis), then lift to patient space.
        List<Vector3D> medialPatient = new(concatenated.Count);
        foreach (VoxelPoint voxel in concatenated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VoxelPoint refined = RefineToMedialAxis(field, geometry, voxel, cancellationToken);
            medialPatient.Add(VoxelToPatient(geometry, refined));
        }

        List<Vector3D> smoothed = ResampleCatmullRom(medialPatient, ResampleSpacingMm);
        if (smoothed.Count < 2)
        {
            return CenterlineExtractionResult.Failure("The resampled centerline is too short.");
        }

        double[] radii = new double[smoothed.Count];
        double[] curvatures = new double[smoothed.Count];
        double[] tortuosities = new double[smoothed.Count];
        double totalLength = 0;

        List<CenterlinePathPoint> points = new(smoothed.Count);
        for (int i = 0; i < smoothed.Count; i++)
        {
            if (i > 0)
            {
                totalLength += (smoothed[i] - smoothed[i - 1]).Length;
            }

            (int vx, int vy, int vz) = PatientToVoxelNearest(geometry, smoothed[i]);
            radii[i] = SampleRadius(field, vx, vy, vz);
            points.Add(new CenterlinePathPoint
            {
                PatientPoint = smoothed[i],
                ArcLengthMm = totalLength,
                RadiusMm = radii[i],
            });
        }

        ComputeCurvature(smoothed, curvatures);
        ComputeTortuosity(smoothed, totalLength, tortuosities);

        for (int i = 0; i < points.Count; i++)
        {
            points[i] = points[i] with { CurvaturePerMm = curvatures[i] };
        }

        double quality = ComputeQuality(radii, curvatures, geometry);

        CenterlinePath path = new()
        {
            SeedSetId = seedSet.Id,
            SegmentationMaskId = seedSet.SegmentationMaskId,
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = points,
            TotalLengthMm = totalLength,
            QualityScore = quality,
            RadiiMm = radii,
            Curvatures = curvatures,
            Tortuosities = tortuosities,
            Summary = string.Format(
                CultureInfo.InvariantCulture,
                "Medial centerline with {0} points ({1:0.0} mm, mean radius {2:0.0} mm).",
                points.Count,
                totalLength,
                Mean(radii)),
        };

        return new CenterlineExtractionResult(true, path, path.Summary, quality);
    }

    /// <summary>
    /// Dijkstra over the 26-neighbourhood restricted to foreground voxels, where the cost of an
    /// edge is the physical step length divided by the inscribed radius at the target voxel. This
    /// pulls the shortest path toward the widest (most central) part of the lumen.
    /// </summary>
    private static List<VoxelPoint>? RunRadiusWeightedDijkstra(
        SegmentationMaskBuffer buffer,
        DistanceField3D field,
        VolumeGridGeometry geometry,
        VoxelPoint start,
        VoxelPoint end,
        CancellationToken cancellationToken)
    {
        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int area = sizeX * sizeY;
        int total = (int)geometry.TotalVoxelCount;

        double[] dist = new double[total];
        Array.Fill(dist, double.PositiveInfinity);
        int[] previous = new int[total];
        Array.Fill(previous, -1);

        int startLinear = start.X + start.Y * sizeX + start.Z * area;
        int endLinear = end.X + end.Y * sizeX + end.Z * area;
        dist[startLinear] = 0;

        PriorityQueue<int, double> queue = new();
        queue.Enqueue(startLinear, 0);
        int relaxations = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++relaxations > MaxDijkstraRelaxations)
            {
                return null;
            }

            int current = queue.Dequeue();
            double currentDist = dist[current];

            if (current == endLinear)
            {
                break;
            }

            if (currentDist == double.PositiveInfinity)
            {
                continue;
            }

            int cz = current / area;
            int rem = current - cz * area;
            int cy = rem / sizeX;
            int cx = rem - cy * sizeX;

            foreach (NeighborStep step in s_neighborSteps)
            {
                int nx = cx + step.DeltaX;
                int ny = cy + step.DeltaY;
                int nz = cz + step.DeltaZ;
                if (!geometry.ContainsVoxel(nx, ny, nz) || !buffer.Get(nx, ny, nz))
                {
                    continue;
                }

                int neighborLinear = nx + ny * sizeX + nz * area;
                double stepMm = PhysicalStep(geometry, step);
                double radius = Math.Max(field.DistanceMm[neighborLinear], MinRadiusMm);
                double candidate = currentDist + stepMm / radius;

                if (candidate < dist[neighborLinear])
                {
                    dist[neighborLinear] = candidate;
                    previous[neighborLinear] = current;
                    queue.Enqueue(neighborLinear, candidate);
                }
            }
        }

        if (dist[endLinear] == double.PositiveInfinity)
        {
            return null;
        }

        List<VoxelPoint> path = [];
        int walk = endLinear;
        while (walk != -1)
        {
            int z = walk / area;
            int rem = walk - z * area;
            int y = rem / sizeX;
            int x = rem - y * sizeX;
            path.Add(new VoxelPoint(x, y, z));
            if (walk == startLinear)
            {
                break;
            }

            walk = previous[walk];
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Hill-climb a voxel onto the local distance ridge (ascent toward the max-inscribed-sphere
    /// axis), bounded to <see cref="MedialRefineMaxShiftMm"/> of total displacement, mirroring
    /// VMTK's max-inscribed-sphere recentering.
    /// </summary>
    private static VoxelPoint RefineToMedialAxis(
        DistanceField3D field,
        VolumeGridGeometry geometry,
        VoxelPoint seed,
        CancellationToken cancellationToken)
    {
        VoxelPoint current = seed;
        double originX = current.X;
        double originY = current.Y;
        double originZ = current.Z;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bestX = current.X;
            int bestY = current.Y;
            int bestZ = current.Z;
            double bestValue = field.DistanceMm[current.X + current.Y * geometry.SizeX + current.Z * geometry.SizeX * geometry.SizeY];

            foreach (NeighborStep step in s_neighborSteps)
            {
                int nx = current.X + step.DeltaX;
                int ny = current.Y + step.DeltaY;
                int nz = current.Z + step.DeltaZ;
                if (!geometry.ContainsVoxel(nx, ny, nz))
                {
                    continue;
                }

                double value = field.DistanceMm[nx + ny * geometry.SizeX + nz * geometry.SizeX * geometry.SizeY];
                if (value > bestValue + 1e-6)
                {
                    bestValue = value;
                    bestX = nx;
                    bestY = ny;
                    bestZ = nz;
                }
            }

            if (bestX == current.X && bestY == current.Y && bestZ == current.Z)
            {
                break;
            }

            double shiftMm = PhysicalDistance(geometry, originX, originY, originZ, bestX, bestY, bestZ);
            if (shiftMm > MedialRefineMaxShiftMm)
            {
                break;
            }

            current = new VoxelPoint(bestX, bestY, bestZ);
        }

        return current;
    }

    private static bool TrySnapToForeground(
        SegmentationMaskBuffer buffer,
        VolumeGridGeometry geometry,
        Vector3D patientPoint,
        out VoxelPoint voxel)
    {
        (double vx, double vy, double vz) = PatientToVoxel(geometry, patientPoint);
        int cx = (int)Math.Round(vx);
        int cy = (int)Math.Round(vy);
        int cz = (int)Math.Round(vz);

        int radius = 6;
        double bestDistanceSq = double.MaxValue;
        bool found = false;
        int bestX = 0;
        int bestY = 0;
        int bestZ = 0;

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    int nz = cz + dz;
                    if (!geometry.ContainsVoxel(nx, ny, nz) || !buffer.Get(nx, ny, nz))
                    {
                        continue;
                    }

                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < bestDistanceSq)
                    {
                        bestDistanceSq = d;
                        bestX = nx;
                        bestY = ny;
                        bestZ = nz;
                        found = true;
                    }
                }
            }
        }

        voxel = new VoxelPoint(bestX, bestY, bestZ);
        return found;
    }

    /// <summary>
    /// Catmull-Rom spline through the control points, resampled to a fixed arc-length spacing.
    /// </summary>
    internal static List<Vector3D> ResampleCatmullRom(IReadOnlyList<Vector3D> control, double spacingMm)
    {
        if (control.Count < 2)
        {
            return [.. control];
        }

        if (spacingMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacingMm));
        }

        List<Vector3D> result = [];
        int n = control.Count;

        for (int i = 0; i < n - 1; i++)
        {
            Vector3D p0 = control[Math.Max(i - 1, 0)];
            Vector3D p1 = control[i];
            Vector3D p2 = control[i + 1];
            Vector3D p3 = control[Math.Min(i + 2, n - 1)];

            double segmentLength = EstimateSegmentLength(p0, p1, p2, p3);
            int steps = Math.Max(1, (int)Math.Ceiling(segmentLength / spacingMm));

            for (int s = 0; s < steps; s++)
            {
                double t = (double)s / steps;
                result.Add(EvaluateCatmullRom(p0, p1, p2, p3, t));
            }
        }

        result.Add(control[n - 1]);
        return DedupeByDistance(result, spacingMm * 0.25);
    }

    private static Vector3D EvaluateCatmullRom(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;
        return new Vector3D(
            0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
            0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3),
            0.5 * ((2 * p1.Z) + (-p0.Z + p2.Z) * t + (2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2 + (-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3));
    }

    private static double EstimateSegmentLength(Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3)
    {
        double length = 0;
        Vector3D previous = EvaluateCatmullRom(p0, p1, p2, p3, 0);
        const int samples = 8;
        for (int s = 1; s <= samples; s++)
        {
            Vector3D current = EvaluateCatmullRom(p0, p1, p2, p3, (double)s / samples);
            length += (current - previous).Length;
            previous = current;
        }

        return length;
    }

    private static List<Vector3D> DedupeByDistance(List<Vector3D> points, double minDistance)
    {
        List<Vector3D> result = new(points.Count) { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            if ((points[i] - result[^1]).Length >= minDistance)
            {
                result.Add(points[i]);
            }
        }

        if (result.Count >= 2)
        {
            result[^1] = points[^1];
        }

        return result;
    }

    private static void ComputeCurvature(IReadOnlyList<Vector3D> points, double[] curvature)
    {
        int n = points.Count;
        for (int i = 0; i < n; i++)
        {
            if (i == 0 || i == n - 1)
            {
                curvature[i] = 0;
                continue;
            }

            curvature[i] = MengerCurvature(points[i - 1], points[i], points[i + 1]);
        }
    }

    /// <summary>Menger curvature: reciprocal of the circumradius of the three points.</summary>
    private static double MengerCurvature(Vector3D a, Vector3D b, Vector3D c)
    {
        double ab = (b - a).Length;
        double bc = (c - b).Length;
        double ca = (a - c).Length;
        if (ab <= 0 || bc <= 0 || ca <= 0)
        {
            return 0;
        }

        Vector3D cross = (b - a).Cross(c - a);
        double area2 = cross.Length;
        if (area2 <= 1e-9)
        {
            return 0;
        }

        return 2.0 * area2 / (ab * bc * ca);
    }

    private static void ComputeTortuosity(IReadOnlyList<Vector3D> points, double totalLength, double[] tortuosity)
    {
        int n = points.Count;
        if (n < 2)
        {
            return;
        }

        double[] cumulative = new double[n];
        for (int i = 1; i < n; i++)
        {
            cumulative[i] = cumulative[i - 1] + (points[i] - points[i - 1]).Length;
        }

        double window = Math.Min(20.0, totalLength * 0.25);
        for (int i = 0; i < n; i++)
        {
            int j = FindIndexAtArcLength(cumulative, cumulative[i] + window);
            double arc = cumulative[j] - cumulative[i];
            double chord = (points[j] - points[i]).Length;
            tortuosity[i] = chord > 1e-6 ? arc / chord : 1.0;
        }
    }

    private static int FindIndexAtArcLength(double[] cumulative, double target)
    {
        int index = Array.BinarySearch(cumulative, target);
        if (index >= 0)
        {
            return index;
        }

        int insert = ~index;
        return Math.Clamp(insert, 0, cumulative.Length - 1);
    }

    private static double ComputeQuality(double[] radii, double[] curvatures, VolumeGridGeometry geometry)
    {
        if (radii.Length == 0)
        {
            return 0;
        }

        double maxRadius = radii.Max();
        if (maxRadius <= 0)
        {
            return 0;
        }

        // Support: fraction of points sitting comfortably inside the lumen (radius >= 1 voxel diagonal).
        double voxelDiagonal = Math.Sqrt(
            geometry.SpacingX * geometry.SpacingX +
            geometry.SpacingY * geometry.SpacingY +
            geometry.SpacingZ * geometry.SpacingZ);
        double support = radii.Count(r => r >= voxelDiagonal) / (double)radii.Length;

        // Centrality: mean ratio of local radius to the widest radius along the path.
        double centrality = radii.Average(r => Math.Clamp(r / maxRadius, 0, 1));

        // Smoothness: penalise sharp curvature (>= 1/5mm is treated as fully rough).
        double meanCurvature = curvatures.Average();
        double smoothness = Math.Clamp(1.0 - (meanCurvature * 5.0), 0, 1);

        return Math.Clamp((0.5 * support) + (0.3 * centrality) + (0.2 * smoothness), 0, 1);
    }

    private static double SampleRadius(DistanceField3D field, int vx, int vy, int vz)
    {
        int plane = field.Geometry.SizeX * field.Geometry.SizeY;
        int x = Math.Clamp(vx, 0, field.Geometry.SizeX - 1);
        int y = Math.Clamp(vy, 0, field.Geometry.SizeY - 1);
        int z = Math.Clamp(vz, 0, field.Geometry.SizeZ - 1);
        return field.DistanceMm[x + y * field.Geometry.SizeX + z * plane];
    }

    private static (double vx, double vy, double vz) PatientToVoxel(VolumeGridGeometry geometry, Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - geometry.Origin;
        double vx = relative.Dot(geometry.RowDirection) / geometry.SpacingX;
        double vy = relative.Dot(geometry.ColumnDirection) / geometry.SpacingY;
        double vz = relative.Dot(geometry.Normal) / geometry.SpacingZ;
        return (vx, vy, vz);
    }

    private static (int vx, int vy, int vz) PatientToVoxelNearest(VolumeGridGeometry geometry, Vector3D patientPoint)
    {
        (double vx, double vy, double vz) = PatientToVoxel(geometry, patientPoint);
        return ((int)Math.Round(vx), (int)Math.Round(vy), (int)Math.Round(vz));
    }

    private static Vector3D VoxelToPatient(VolumeGridGeometry geometry, VoxelPoint voxel) =>
        geometry.Origin
        + (geometry.RowDirection * (voxel.X * geometry.SpacingX))
        + (geometry.ColumnDirection * (voxel.Y * geometry.SpacingY))
        + (geometry.Normal * (voxel.Z * geometry.SpacingZ));

    private static double PhysicalStep(VolumeGridGeometry geometry, NeighborStep step) =>
        PhysicalDistance(geometry, 0, 0, 0, step.DeltaX, step.DeltaY, step.DeltaZ);

    private static double PhysicalDistance(
        VolumeGridGeometry geometry,
        double ax, double ay, double az,
        double bx, double by, double bz)
    {
        double dx = (bx - ax) * geometry.SpacingX;
        double dy = (by - ay) * geometry.SpacingY;
        double dz = (bz - az) * geometry.SpacingZ;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Mean(double[] values) => values.Length == 0 ? 0 : values.Average();

    private static NeighborStep[] CreateNeighborSteps()
    {
        List<NeighborStep> steps = [];
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                    {
                        continue;
                    }

                    steps.Add(new NeighborStep(dx, dy, dz));
                }
            }
        }

        return [.. steps];
    }

    private readonly record struct VoxelPoint(int X, int Y, int Z);
    private readonly record struct NeighborStep(int DeltaX, int DeltaY, int DeltaZ);
}
