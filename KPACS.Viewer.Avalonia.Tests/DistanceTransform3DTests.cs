using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Exactness tests for the separable Felzenszwalb Euclidean distance transform. Every
/// expectation is an analytically known distance (single background point, box interior,
/// anisotropic spacing), so the transform is checked against closed-form values rather
/// than a reference implementation. Pure model/math surface — no Avalonia, no Dispatcher.
/// </summary>
public class DistanceTransform3DTests
{
    private static readonly Vector3D UnitX = new(1, 0, 0);
    private static readonly Vector3D UnitY = new(0, 1, 0);
    private static readonly Vector3D UnitZ = new(0, 0, 1);

    private static VolumeGridGeometry Geometry(int sx, int sy, int sz, double dx, double dy, double dz) =>
        new(sx, sy, sz, dx, dy, dz, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.test.for");

    private static SegmentationMask3D BuildMask(
        VolumeGridGeometry geometry,
        Func<int, int, int, bool> isForeground)
    {
        SegmentationMaskBuffer buffer = new(geometry);
        int foreground = 0;
        for (int z = 0; z < geometry.SizeZ; z++)
        {
            for (int y = 0; y < geometry.SizeY; y++)
            {
                for (int x = 0; x < geometry.SizeX; x++)
                {
                    if (isForeground(x, y, z))
                    {
                        buffer.Set(x, y, z, true);
                        foreground++;
                    }
                }
            }
        }

        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        SegmentationMaskStatistics? stats = buffer.ComputeStatistics();
        return new SegmentationMask3D(
            Guid.NewGuid(),
            "test-mask",
            "1.2.3.series",
            geometry.FrameOfReferenceUid,
            "1.2.3.study",
            geometry,
            buffer.ToStorage(),
            new SegmentationMaskMetadata(
                SegmentationMaskSourceKind.ManualEdit,
                now,
                now,
                null,
                null,
                0,
                stats));
    }

    private static float Value(DistanceField3D field, int x, int y, int z)
    {
        int plane = field.Geometry.SizeX * field.Geometry.SizeY;
        return field.DistanceMm[x + y * field.Geometry.SizeX + z * plane];
    }

    [Fact]
    public void EmptyMask_AllDistancesAreZero()
    {
        VolumeGridGeometry geometry = Geometry(6, 6, 6, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (_, _, _) => false);

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        Assert.All(field.DistanceMm, v => Assert.Equal(0f, v, 6));
    }

    [Fact]
    public void SingleForegroundVoxel_DistanceEqualsNeighbourSpacing()
    {
        VolumeGridGeometry geometry = Geometry(7, 7, 7, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) => x == 3 && y == 3 && z == 3);

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        // The lone foreground voxel's nearest background voxel is any of its 6 neighbours at 1mm.
        Assert.Equal(1.0, Value(field, 3, 3, 3), 3);
        // Background voxels stay at zero.
        Assert.Equal(0f, Value(field, 0, 0, 0), 6);
    }

    [Fact]
    public void SingleBackgroundVoxel_Corner_DistanceIsExactEuclideanToCorner()
    {
        // Everything foreground except the (0,0,0) corner → EDT at (x,y,z) is the exact
        // Euclidean distance to that single background point.
        VolumeGridGeometry geometry = Geometry(5, 5, 5, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) => !(x == 0 && y == 0 && z == 0));

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        Assert.Equal(Math.Sqrt(48), Value(field, 4, 4, 4), 3);
        Assert.Equal(Math.Sqrt(3), Value(field, 1, 1, 1), 3);
    }

    [Fact]
    public void IsotropicBox_InteriorDistanceIsExact()
    {
        // Foreground box x,y,z in [2,7] inside a 10^3 grid, unit spacing.
        // Voxel (4,4,4): nearest background plane is at index 1 → distance 3mm exactly.
        VolumeGridGeometry geometry = Geometry(10, 10, 10, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) =>
            x >= 2 && x <= 7 && y >= 2 && y <= 7 && z >= 2 && z <= 7);

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        Assert.Equal(3.0, Value(field, 4, 4, 4), 3);
        // Voxel (2,4,4) sits on the box face; nearest background is x=1 → 1mm.
        Assert.Equal(1.0, Value(field, 2, 4, 4), 3);
    }

    [Fact]
    public void AnisotropicSpacing_InteriorDistanceHonoursAxisSpacing()
    {
        // Same box, spacing (2,1,0.5). Voxel (4,4,4): nearest background is the z=1 plane
        // at (4-1)*0.5 = 1.5mm (single-axis minimum dominates the Euclidean min).
        VolumeGridGeometry geometry = Geometry(10, 10, 10, 2, 1, 0.5);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) =>
            x >= 2 && x <= 7 && y >= 2 && y <= 7 && z >= 2 && z <= 7);

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        Assert.Equal(1.5, Value(field, 4, 4, 4), 3);
    }

    [Fact]
    public void SampleTrilinear_AtIntegerVoxel_ReturnsStoredValue()
    {
        VolumeGridGeometry geometry = Geometry(10, 10, 10, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) =>
            x >= 2 && x <= 7 && y >= 2 && y <= 7 && z >= 2 && z <= 7);

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        double sampled = field.SampleTrilinear(4, 4, 4);
        Assert.Equal(Value(field, 4, 4, 4), sampled, 3);
    }

    [Fact]
    public void SphereCenter_InscribedRadiusApproximatesRadius()
    {
        // Foreground ball of radius 5 voxels, unit spacing. The centre's EDT (max-inscribed
        // radius) should be close to 5mm within discretisation tolerance.
        const int n = 13;
        const double r = 5.0;
        double c = (n - 1) / 2.0;
        VolumeGridGeometry geometry = Geometry(n, n, n, 1, 1, 1);
        SegmentationMask3D mask = BuildMask(geometry, (x, y, z) =>
        {
            double dx = x - c;
            double dy = y - c;
            double dz = z - c;
            return dx * dx + dy * dy + dz * dz <= r * r;
        });

        DistanceField3D field = DistanceTransform3D.Compute(mask, geometry);

        int mid = (int)c;
        double center = Value(field, mid, mid, mid);
        Assert.InRange(center, r - 0.6, r + 0.6);
    }
}
