using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Result of an exact Euclidean distance transform over a mask grid. Stores the
/// distance (in millimetres) from every voxel to the nearest background voxel, so the
/// value at a foreground (vessel) voxel is its inscribed-sphere radius estimate.
/// </summary>
internal sealed record DistanceField3D(
    VolumeGridGeometry Geometry,
    float[] DistanceMm)
{
    /// <summary>
    /// Trilinearly sample the distance field at a voxel-space coordinate
    /// (<paramref name="vx"/>, <paramref name="vy"/>, <paramref name="vz"/> are fractional
    /// voxel indices, not millimetres). Out-of-bounds samples clamp to the grid edge.
    /// </summary>
    public double SampleTrilinear(double vx, double vy, double vz)
    {
        double cx = Math.Clamp(vx, 0, Geometry.SizeX - 1);
        double cy = Math.Clamp(vy, 0, Geometry.SizeY - 1);
        double cz = Math.Clamp(vz, 0, Geometry.SizeZ - 1);

        int x0 = (int)Math.Floor(cx);
        int y0 = (int)Math.Floor(cy);
        int z0 = (int)Math.Floor(cz);
        int x1 = Math.Min(x0 + 1, Geometry.SizeX - 1);
        int y1 = Math.Min(y0 + 1, Geometry.SizeY - 1);
        int z1 = Math.Min(z0 + 1, Geometry.SizeZ - 1);

        double fx = cx - x0;
        double fy = cy - y0;
        double fz = cz - z0;

        int plane = Geometry.SizeX * Geometry.SizeY;

        double c000 = DistanceMm[x0 + y0 * Geometry.SizeX + z0 * plane];
        double c100 = DistanceMm[x1 + y0 * Geometry.SizeX + z0 * plane];
        double c010 = DistanceMm[x0 + y1 * Geometry.SizeX + z0 * plane];
        double c110 = DistanceMm[x1 + y1 * Geometry.SizeX + z0 * plane];
        double c001 = DistanceMm[x0 + y0 * Geometry.SizeX + z1 * plane];
        double c101 = DistanceMm[x1 + y0 * Geometry.SizeX + z1 * plane];
        double c011 = DistanceMm[x0 + y1 * Geometry.SizeX + z1 * plane];
        double c111 = DistanceMm[x1 + y1 * Geometry.SizeX + z1 * plane];

        double c00 = c000 * (1 - fx) + c100 * fx;
        double c10 = c010 * (1 - fx) + c110 * fx;
        double c01 = c001 * (1 - fx) + c101 * fx;
        double c11 = c011 * (1 - fx) + c111 * fx;

        double c0 = c00 * (1 - fy) + c10 * fy;
        double c1 = c01 * (1 - fy) + c11 * fy;

        return c0 * (1 - fz) + c1 * fz;
    }
}

/// <summary>
/// Exact 3D Euclidean distance transform using the separable Felzenszwalb &amp; Huttenlocher
/// lower-envelope-of-parabolas algorithm, run as three axis passes with
/// <see cref="Parallel.For"/>. Anisotropic voxel spacing is honoured by scaling each axis
/// pass by its squared spacing. Complexity is O(n) per axis, i.e. O(n) overall.
/// </summary>
/// <remarks>
/// The transform treats background voxels as the "feature" set (distance 0) and computes,
/// for every voxel, the physical distance to the nearest background voxel. For a foreground
/// (vessel) voxel this equals the radius of the largest sphere centred there that stays
/// inside the mask — the max-inscribed-sphere radius that VMTK weights its centerlines with.
/// </remarks>
internal static class DistanceTransform3D
{
    private const double Infinity = double.PositiveInfinity;

    public static DistanceField3D Compute(SegmentationMask3D mask, VolumeGridGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(geometry);

        if (!ReferenceEquals(mask.Geometry, geometry) && !GeometryEquals(mask.Geometry, geometry))
        {
            throw new ArgumentException("Mask geometry must match the supplied grid geometry.", nameof(geometry));
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(geometry, mask.Storage);
        return Compute(buffer, geometry);
    }

    public static DistanceField3D Compute(SegmentationMaskBuffer buffer, VolumeGridGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(geometry);

        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        long total = geometry.TotalVoxelCount;

        if (buffer.SizeX != sizeX || buffer.SizeY != sizeY || buffer.SizeZ != sizeZ)
        {
            throw new ArgumentException("Buffer dimensions must match the supplied grid geometry.", nameof(buffer));
        }

        // f = 0 at background voxels, +inf at foreground voxels → EDT gives distance to nearest background.
        double[] current = new double[total];
        double[] scratch = new double[total];

        Parallel.For(
            0,
            sizeZ,
            z =>
            {
                int plane = sizeX * sizeY;
                int baseIndex = z * plane;
                for (int i = 0; i < plane; i++)
                {
                    current[baseIndex + i] = buffer.GetLinear(baseIndex + i) ? Infinity : 0.0;
                }
            });

        // Pass 1: along X (lines indexed by (y, z), contiguous runs of length sizeX).
        Parallel.For(
            0,
            sizeY * sizeZ,
            line =>
            {
                int baseIndex = line * sizeX;
                Span<double> f = current.AsSpan(baseIndex, sizeX);
                Span<double> d = scratch.AsSpan(baseIndex, sizeX);
                TransformLine(f, d, sizeX, geometry.SpacingX);
            });
        (current, scratch) = (scratch, current);

        // Pass 2: along Y (lines indexed by (x, z), stride sizeX).
        Parallel.For(
            0,
            sizeX * sizeZ,
            () => (f: new double[sizeY], d: new double[sizeY]),
            (line, _, local) =>
            {
                int x = line % sizeX;
                int z = line / sizeX;
                int plane = sizeX * sizeY;
                int baseIndex = z * plane + x;
                for (int y = 0; y < sizeY; y++)
                {
                    local.f[y] = current[baseIndex + y * sizeX];
                }

                TransformLine(local.f, local.d, sizeY, geometry.SpacingY);

                for (int y = 0; y < sizeY; y++)
                {
                    scratch[baseIndex + y * sizeX] = local.d[y];
                }

                return local;
            },
            local => { });
        (current, scratch) = (scratch, current);

        // Pass 3: along Z (lines indexed by (x, y), stride sizeX*sizeY).
        Parallel.For(
            0,
            sizeX * sizeY,
            () => (f: new double[sizeZ], d: new double[sizeZ]),
            (line, _, local) =>
            {
                int plane = sizeX * sizeY;
                for (int z = 0; z < sizeZ; z++)
                {
                    local.f[z] = current[line + z * plane];
                }

                TransformLine(local.f, local.d, sizeZ, geometry.SpacingZ);

                for (int z = 0; z < sizeZ; z++)
                {
                    scratch[line + z * plane] = local.d[z];
                }

                return local;
            },
            local => { });
        (current, scratch) = (scratch, current);

        float[] distanceMm = new float[total];
        Parallel.For(
            0,
            sizeZ,
            z =>
            {
                int plane = sizeX * sizeY;
                int baseIndex = z * plane;
                for (int i = 0; i < plane; i++)
                {
                    double squared = current[baseIndex + i];
                    distanceMm[baseIndex + i] = double.IsPositiveInfinity(squared)
                        ? 0f
                        : (float)Math.Sqrt(squared);
                }
            });

        return new DistanceField3D(geometry, distanceMm);
    }

    /// <summary>
    /// Exact 1D squared Euclidean distance transform of a sampled function with anisotropic
    /// sample spacing <paramref name="spacing"/>. Implements
    /// <c>d[p] = min_q { ((p - q) * spacing)^2 + f[q] }</c> via the lower envelope of parabolas.
    /// </summary>
    internal static void TransformLine(ReadOnlySpan<double> f, Span<double> d, int n, double spacing)
    {
        if (n == 1)
        {
            d[0] = f[0];
            return;
        }

        double h2 = spacing * spacing;

        // Scale trick: min_q((p-q)^2 h^2 + f[q]) = h^2 * min_q((p-q)^2 + f[q]/h^2).
        // Run the standard unscaled algorithm on f/h^2, then multiply the result by h^2.
        int[] v = new int[n];
        double[] z = new double[n + 1];
        double[] g = new double[n];
        for (int q = 0; q < n; q++)
        {
            g[q] = f[q] / h2;
        }

        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s = Intersection(g, v[k], q);
            while (s <= z[k])
            {
                k--;
                s = Intersection(g, v[k], q);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            double diff = q - v[k];
            d[q] = h2 * (diff * diff + g[v[k]]);
        }
    }

    private static double Intersection(double[] g, int p, int q)
    {
        // Column intersection of parabolas centred at p and q (unit spacing on g).
        // Both may be +inf; guard the degenerate inf - inf case.
        double gp = g[p];
        double gq = g[q];
        if (double.IsPositiveInfinity(gq))
        {
            return double.PositiveInfinity;
        }

        if (double.IsPositiveInfinity(gp))
        {
            return double.NegativeInfinity;
        }

        return ((q * q + gq) - (p * p + gp)) / (2.0 * (q - p));
    }

    private static bool GeometryEquals(VolumeGridGeometry a, VolumeGridGeometry b) =>
        a.SizeX == b.SizeX &&
        a.SizeY == b.SizeY &&
        a.SizeZ == b.SizeZ &&
        a.SpacingX == b.SpacingX &&
        a.SpacingY == b.SpacingY &&
        a.SpacingZ == b.SpacingZ;
}
