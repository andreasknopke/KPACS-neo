using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Tunable parameters for lumen segmentation and EVAR sub-mask derivation. HU thresholds follow
/// the contrast-CTA defaults from the plan; all are overridable so the panel can expose sliders.
/// </summary>
internal sealed record LumenSegmentationOptions
{
    /// <summary>Lower HU bound for contrast-filled lumen (region-grow seed band).</summary>
    public double LumenHuLower { get; init; } = 150;

    /// <summary>Upper HU bound for contrast-filled lumen.</summary>
    public double LumenHuUpper { get; init; } = 1500;

    /// <summary>Radius (mm) of the spherical structuring element used for morphological closing.</summary>
    public double ClosingRadiusMm { get; init; } = 2.0;

    /// <summary>When true, keep only the largest connected component of the lumen.</summary>
    public bool KeepLargestComponent { get; init; } = true;

    /// <summary>When true, derive a calcification sub-mask (HU &gt; <see cref="CalciumHuThreshold"/>).</summary>
    public bool DeriveCalcium { get; init; } = true;

    /// <summary>HU threshold above which a voxel counts as calcification.</summary>
    public double CalciumHuThreshold { get; init; } = 350;

    /// <summary>Wall-ring thickness (mm) beyond the lumen within which calcification is searched.</summary>
    public double CalciumWallRingMm { get; init; } = 2.0;

    /// <summary>When true, derive a mural-thrombus sub-mask (HU in the thrombus band, outside lumen).</summary>
    public bool DeriveThrombus { get; init; } = true;

    /// <summary>Lower HU bound of the mural-thrombus band.</summary>
    public double ThrombusHuLower { get; init; } = 40;

    /// <summary>Upper HU bound of the mural-thrombus band.</summary>
    public double ThrombusHuUpper { get; init; } = 150;

    /// <summary>How far (mm) outside the lumen thrombus may extend before it is treated as background tissue.</summary>
    public double ThrombusExtentMm { get; init; } = 12.0;
}

/// <summary>
/// Result of a lumen segmentation: the primary lumen mask plus optional calcification and
/// mural-thrombus sub-masks, all sharing the source geometry.
/// </summary>
internal sealed record LumenSegmentationResult(
    bool Succeeded,
    string Summary,
    SegmentationMask3D? LumenMask,
    SegmentationMask3D? CalciumMask,
    SegmentationMask3D? ThrombusMask)
{
    public static LumenSegmentationResult Failure(string summary) =>
        new(false, summary, null, null, null);
}

/// <summary>
/// Deterministic, offline lumen segmentation in pure C#: a 6-neighbourhood HU-band region grow
/// from seed voxels, followed by separable spherical morphological closing, per-slice hole
/// filling, and largest-connected-component selection. Calcification and mural-thrombus sub-masks
/// are derived from the lumen contour and the HU bands. The GPU flood-fill kernel is an optional
/// accelerator; this CPU path is the reference and is fully unit-testable.
/// </summary>
internal sealed class LumenSegmentationService
{
    private static readonly (int Dx, int Dy, int Dz)[] s_faceSteps =
    [
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    ];

    public LumenSegmentationResult Segment(
        SeriesVolume volume,
        VolumeGridGeometry geometry,
        IReadOnlyList<Vector3D> seedPatientPoints,
        LumenSegmentationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(seedPatientPoints);

        options ??= new LumenSegmentationOptions();

        if (seedPatientPoints.Count == 0)
        {
            return LumenSegmentationResult.Failure("Lumen segmentation needs at least one seed point.");
        }

        if (!VolumesMatch(volume, geometry))
        {
            return LumenSegmentationResult.Failure("Volume and grid geometry do not match.");
        }

        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        int area = sizeX * sizeY;

        bool[] inBand = new bool[(int)geometry.TotalVoxelCount];
        for (int z = 0; z < sizeZ; z++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    double hu = volume.GetVoxel(x, y, z);
                    inBand[x + (y * sizeX) + (z * area)] = hu >= options.LumenHuLower && hu <= options.LumenHuUpper;
                }
            }
        }

        bool[] lumen = new bool[inBand.Length];
        Queue<int> queue = new();
        int seeded = 0;

        foreach (Vector3D seed in seedPatientPoints)
        {
            (int sx, int sy, int sz) = PatientToVoxelNearest(geometry, seed);
            if (!geometry.ContainsVoxel(sx, sy, sz))
            {
                continue;
            }

            int linear = sx + (sy * sizeX) + (sz * area);
            if (!inBand[linear] || lumen[linear])
            {
                continue;
            }

            lumen[linear] = true;
            seeded++;
            queue.Enqueue(linear);
        }

        if (seeded == 0)
        {
            return LumenSegmentationResult.Failure("No seed point falls inside the lumen HU band.");
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int current = queue.Dequeue();
            int cz = current / area;
            int rem = current - (cz * area);
            int cy = rem / sizeX;
            int cx = rem - (cy * sizeX);

            foreach ((int dx, int dy, int dz) in s_faceSteps)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                int nz = cz + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= sizeX || ny >= sizeY || nz >= sizeZ)
                {
                    continue;
                }

                int neighbor = nx + (ny * sizeX) + (nz * area);
                if (inBand[neighbor] && !lumen[neighbor])
                {
                    lumen[neighbor] = true;
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (options.ClosingRadiusMm > 0)
        {
            lumen = MorphologicalClose(lumen, geometry, options.ClosingRadiusMm, cancellationToken);
        }

        lumen = FillHolesPerSlice(lumen, geometry, cancellationToken);

        if (options.KeepLargestComponent)
        {
            lumen = LargestConnectedComponent(lumen, geometry, cancellationToken);
        }

        SegmentationMaskBuffer lumenBuffer = new(geometry);
        int lumenCount = 0;
        for (int i = 0; i < lumen.Length; i++)
        {
            if (lumen[i])
            {
                lumenBuffer.SetLinear(i, true);
                lumenCount++;
            }
        }

        if (lumenCount == 0)
        {
            return LumenSegmentationResult.Failure("The lumen region grow produced an empty mask.");
        }

        SegmentationMask3D lumenMask = Seal(lumenBuffer, geometry, "Lumen", volume);

        SegmentationMask3D? calciumMask = null;
        if (options.DeriveCalcium)
        {
            bool[] wallRing = Dilate(lumen, geometry, options.CalciumWallRingMm);
            SegmentationMaskBuffer calciumBuffer = new(geometry);
            int calciumCount = 0;
            for (int z = 0; z < sizeZ; z++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        int linear = x + (y * sizeX) + (z * area);
                        if (!wallRing[linear])
                        {
                            continue;
                        }

                        if (volume.GetVoxel(x, y, z) >= options.CalciumHuThreshold)
                        {
                            calciumBuffer.SetLinear(linear, true);
                            calciumCount++;
                        }
                    }
                }
            }

            if (calciumCount > 0)
            {
                calciumMask = Seal(calciumBuffer, geometry, "Calcium", volume);
            }
        }

        SegmentationMask3D? thrombusMask = null;
        if (options.DeriveThrombus)
        {
            bool[] extent = Dilate(lumen, geometry, options.ThrombusExtentMm);
            SegmentationMaskBuffer thrombusBuffer = new(geometry);
            int thrombusCount = 0;
            for (int z = 0; z < sizeZ; z++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        int linear = x + (y * sizeX) + (z * area);
                        if (!extent[linear] || lumen[linear])
                        {
                            continue;
                        }

                        double hu = volume.GetVoxel(x, y, z);
                        if (hu >= options.ThrombusHuLower && hu <= options.ThrombusHuUpper)
                        {
                            thrombusBuffer.SetLinear(linear, true);
                            thrombusCount++;
                        }
                    }
                }
            }

            if (thrombusCount > 0)
            {
                thrombusMask = Seal(thrombusBuffer, geometry, "Thrombus", volume);
            }
        }

        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Lumen {0} voxels ({1:0.0} cm³){2}{3}.",
            lumenCount,
            lumenCount * geometry.VoxelVolumeCubicMillimeters / 1000.0,
            calciumMask is null ? string.Empty : $", calcium {calciumMask.Storage.ForegroundVoxelCount}",
            thrombusMask is null ? string.Empty : $", thrombus {thrombusMask.Storage.ForegroundVoxelCount}");

        return new LumenSegmentationResult(true, summary, lumenMask, calciumMask, thrombusMask);
    }

    /// <summary>Separable morphological closing (dilate then erode) with a spherical element.</summary>
    internal static bool[] MorphologicalClose(
        bool[] mask,
        VolumeGridGeometry geometry,
        double radiusMm,
        CancellationToken cancellationToken)
    {
        bool[] dilated = Dilate(mask, geometry, radiusMm);
        return Erode(dilated, geometry, radiusMm, cancellationToken);
    }

    internal static bool[] Dilate(bool[] mask, VolumeGridGeometry geometry, double radiusMm)
    {
        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        int area = sizeX * sizeY;

        int rx = (int)Math.Ceiling(radiusMm / geometry.SpacingX);
        int ry = (int)Math.Ceiling(radiusMm / geometry.SpacingY);
        int rz = (int)Math.Ceiling(radiusMm / geometry.SpacingZ);
        double r2 = radiusMm * radiusMm;

        bool[] result = new bool[mask.Length];

        Parallel.For(
            0,
            sizeZ,
            z =>
            {
                for (int y = 0; y < sizeY; y++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        int linear = x + (y * sizeX) + (z * area);
                        if (mask[linear])
                        {
                            result[linear] = true;
                            continue;
                        }

                        for (int dz = -rz; dz <= rz && !result[linear]; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0 || nz >= sizeZ)
                            {
                                continue;
                            }

                            double dzMm = dz * geometry.SpacingZ;
                            for (int dy = -ry; dy <= ry && !result[linear]; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0 || ny >= sizeY)
                                {
                                    continue;
                                }

                                double dyMm = dy * geometry.SpacingY;
                                for (int dx = -rx; dx <= rx; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0 || nx >= sizeX)
                                    {
                                        continue;
                                    }

                                    double dxMm = dx * geometry.SpacingX;
                                    if (dxMm * dxMm + dyMm * dyMm + dzMm * dzMm > r2)
                                    {
                                        continue;
                                    }

                                    if (mask[nx + (ny * sizeX) + (nz * area)])
                                    {
                                        result[linear] = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            });

        return result;
    }

    internal static bool[] Erode(
        bool[] mask,
        VolumeGridGeometry geometry,
        double radiusMm,
        CancellationToken cancellationToken)
    {
        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        int area = sizeX * sizeY;

        int rx = (int)Math.Ceiling(radiusMm / geometry.SpacingX);
        int ry = (int)Math.Ceiling(radiusMm / geometry.SpacingY);
        int rz = (int)Math.Ceiling(radiusMm / geometry.SpacingZ);
        double r2 = radiusMm * radiusMm;

        bool[] result = new bool[mask.Length];

        Parallel.For(
            0,
            sizeZ,
            z =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = 0; y < sizeY; y++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        int linear = x + (y * sizeX) + (z * area);
                        if (!mask[linear])
                        {
                            continue;
                        }

                        bool keep = true;
                        for (int dz = -rz; dz <= rz && keep; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0 || nz >= sizeZ)
                            {
                                keep = false;
                                break;
                            }

                            double dzMm = dz * geometry.SpacingZ;
                            for (int dy = -ry; dy <= ry && keep; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0 || ny >= sizeY)
                                {
                                    keep = false;
                                    break;
                                }

                                double dyMm = dy * geometry.SpacingY;
                                for (int dx = -rx; dx <= rx; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0 || nx >= sizeX)
                                    {
                                        keep = false;
                                        break;
                                    }

                                    double dxMm = dx * geometry.SpacingX;
                                    if (dxMm * dxMm + dyMm * dyMm + dzMm * dzMm > r2)
                                    {
                                        continue;
                                    }

                                    if (!mask[nx + (ny * sizeX) + (nz * area)])
                                    {
                                        keep = false;
                                        break;
                                    }
                                }
                            }
                        }

                        result[linear] = keep;
                    }
                }
            });

        return result;
    }

    /// <summary>
    /// Fill enclosed background holes per axial slice via a flood fill seeded from the slice border.
    /// Background reachable from the border stays background; enclosed pockets become foreground.
    /// </summary>
    internal static bool[] FillHolesPerSlice(
        bool[] mask,
        VolumeGridGeometry geometry,
        CancellationToken cancellationToken)
    {
        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        int area = sizeX * sizeY;
        bool[] result = (bool[])mask.Clone();

        for (int z = 0; z < sizeZ; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool[] reachable = new bool[sizeX * sizeY];
            Queue<int> stack = new();

            for (int y = 0; y < sizeY; y++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    bool isBorder = x == 0 || y == 0 || x == sizeX - 1 || y == sizeY - 1;
                    int idx = y * sizeX + x;
                    if (isBorder && !mask[z * area + idx])
                    {
                        reachable[idx] = true;
                        stack.Enqueue(idx);
                    }
                }
            }

            while (stack.Count > 0)
            {
                int idx = stack.Dequeue();
                int x = idx % sizeX;
                int y = idx / sizeX;

                TryPush(x - 1, y);
                TryPush(x + 1, y);
                TryPush(x, y - 1);
                TryPush(x, y + 1);

                void TryPush(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= sizeX || ny >= sizeY)
                    {
                        return;
                    }

                    int nidx = ny * sizeX + nx;
                    if (reachable[nidx] || mask[z * area + nidx])
                    {
                        return;
                    }

                    reachable[nidx] = true;
                    stack.Enqueue(nidx);
                }
            }

            for (int idx = 0; idx < reachable.Length; idx++)
            {
                if (!mask[z * area + idx] && !reachable[idx])
                {
                    result[z * area + idx] = true;
                }
            }
        }

        return result;
    }

    /// <summary>Keep only the largest 26-connected foreground component.</summary>
    internal static bool[] LargestConnectedComponent(
        bool[] mask,
        VolumeGridGeometry geometry,
        CancellationToken cancellationToken)
    {
        int sizeX = geometry.SizeX;
        int sizeY = geometry.SizeY;
        int sizeZ = geometry.SizeZ;
        int area = sizeX * sizeY;
        int[] label = new int[mask.Length];
        Array.Fill(label, -1);

        int bestLabel = -1;
        int bestSize = 0;
        int nextLabel = 0;
        Queue<int> queue = new();

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] >= 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            int currentLabel = nextLabel++;
            int size = 0;
            queue.Enqueue(start);
            label[start] = currentLabel;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                size++;

                int cz = current / area;
                int rem = current - (cz * area);
                int cy = rem / sizeX;
                int cx = rem - (cy * sizeX);

                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            int ny = cy + dy;
                            int nz = cz + dz;
                            if (nx < 0 || ny < 0 || nz < 0 || nx >= sizeX || ny >= sizeY || nz >= sizeZ)
                            {
                                continue;
                            }

                            int neighbor = nx + (ny * sizeX) + (nz * area);
                            if (mask[neighbor] && label[neighbor] < 0)
                            {
                                label[neighbor] = currentLabel;
                                queue.Enqueue(neighbor);
                            }
                        }
                    }
                }
            }

            if (size > bestSize)
            {
                bestSize = size;
                bestLabel = currentLabel;
            }
        }

        bool[] result = new bool[mask.Length];
        if (bestLabel >= 0)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                result[i] = label[i] == bestLabel;
            }
        }

        return result;
    }

    private static SegmentationMask3D Seal(SegmentationMaskBuffer buffer, VolumeGridGeometry geometry, string name, SeriesVolume volume)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SegmentationMask3D(
            Guid.NewGuid(),
            name,
            volume.SeriesInstanceUid,
            geometry.FrameOfReferenceUid,
            string.Empty,
            geometry,
            buffer.ToStorage(),
            new SegmentationMaskMetadata(
                SegmentationMaskSourceKind.Derived,
                now,
                now,
                null,
                null,
                0,
                buffer.ComputeStatistics()));
    }

    private static bool VolumesMatch(SeriesVolume volume, VolumeGridGeometry geometry) =>
        volume.SizeX == geometry.SizeX &&
        volume.SizeY == geometry.SizeY &&
        volume.SizeZ == geometry.SizeZ &&
        Math.Abs(volume.SpacingX - geometry.SpacingX) < 1e-6 &&
        Math.Abs(volume.SpacingY - geometry.SpacingY) < 1e-6 &&
        Math.Abs(volume.SpacingZ - geometry.SpacingZ) < 1e-6;

    private static (int vx, int vy, int vz) PatientToVoxelNearest(VolumeGridGeometry geometry, Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - geometry.Origin;
        double vx = relative.Dot(geometry.RowDirection) / geometry.SpacingX;
        double vy = relative.Dot(geometry.ColumnDirection) / geometry.SpacingY;
        double vz = relative.Dot(geometry.Normal) / geometry.SpacingZ;
        return ((int)Math.Round(vx), (int)Math.Round(vy), (int)Math.Round(vz));
    }
}
