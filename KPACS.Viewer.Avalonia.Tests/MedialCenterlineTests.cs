using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Phantom tests for the medial centerline service. A synthetic tube of known radius and known
/// centre curve is segmented, then the extracted centerline is checked for centre deviation and
/// inscribed-radius accuracy. The medial axis is only well-defined where the lumen is radially
/// constrained on all sides, so seeds are placed strictly inside the grid and deviation is
/// measured in the interior (away from the open cut faces). Pure model/math surface — no Avalonia.
/// </summary>
public class MedialCenterlineTests
{
    private static readonly Vector3D UnitX = new(1, 0, 0);
    private static readonly Vector3D UnitY = new(0, 1, 0);
    private static readonly Vector3D UnitZ = new(0, 0, 1);

    private const int N = 64;
    private const double Spacing = 0.5;
    private const double RadiusMm = 10.0;
    private const double CenterXVox = 32.0;
    private const double CenterYVox = 32.0;

    // Seeds sit this many voxels in from each open end, so they lie in the radially-constrained
    // interior where the medial axis is well-defined.
    private const int SeedMarginVox = 12;

    // Gentle sinusoidal curve in X as a function of Z (voxel units). The curve radius is kept
    // much larger than the tube radius, as in a real vessel, so the medial axis is well-defined.
    private static double CurveCenterX(int z) => CenterXVox + 3.0 * Math.Sin(z * 0.05);

    private static VolumeGridGeometry Geometry() =>
        new(N, N, N, Spacing, Spacing, Spacing, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.tube.for");

    private static SegmentationMask3D BuildCurvedTubeMask(VolumeGridGeometry geometry)
    {
        SegmentationMaskBuffer buffer = new(geometry);
        double radiusVox = RadiusMm / Spacing;

        for (int z = 0; z < N; z++)
        {
            double cx = CurveCenterX(z);
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    double dx = x - cx;
                    double dy = y - CenterYVox;
                    if (dx * dx + dy * dy <= radiusVox * radiusVox)
                    {
                        buffer.Set(x, y, z, true);
                    }
                }
            }
        }

        return Seal(buffer, geometry, "tube");
    }

    private static SegmentationMask3D BuildStraightTubeMask(VolumeGridGeometry geometry)
    {
        SegmentationMaskBuffer buffer = new(geometry);
        double radiusVox = RadiusMm / Spacing;

        for (int z = 0; z < N; z++)
        {
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    double dx = x - CenterXVox;
                    double dy = y - CenterYVox;
                    if (dx * dx + dy * dy <= radiusVox * radiusVox)
                    {
                        buffer.Set(x, y, z, true);
                    }
                }
            }
        }

        return Seal(buffer, geometry, "straight");
    }

    private static SegmentationMask3D Seal(SegmentationMaskBuffer buffer, VolumeGridGeometry geometry, string name)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        return new SegmentationMask3D(
            Guid.NewGuid(),
            name,
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
                buffer.ComputeStatistics()));
    }

    private static CenterlineSeed Seed(CenterlineSeedKind kind, Vector3D patient) =>
        new() { Kind = kind, PatientPoint = patient, SeriesInstanceUid = "1.2.3.series" };

    private static Vector3D VoxelToPatient(VolumeGridGeometry g, double vx, double vy, double vz) =>
        g.Origin + (UnitX * (vx * g.SpacingX)) + (UnitY * (vy * g.SpacingY)) + (UnitZ * (vz * g.SpacingZ));

    [Fact]
    public void Extract_StraightTube_CentreExactAndRadiusAccurate()
    {
        // A straight tube is the unambiguous case: the medial axis coincides with the analytic
        // centre line, so any interior deviation here is a real algorithm bug.
        VolumeGridGeometry geometry = Geometry();
        SegmentationMask3D mask = BuildStraightTubeMask(geometry);

        CenterlineSeedSet seedSet = new()
        {
            StartSeed = Seed(CenterlineSeedKind.Start, VoxelToPatient(geometry, CenterXVox, CenterYVox, SeedMarginVox)),
            EndSeed = Seed(CenterlineSeedKind.End, VoxelToPatient(geometry, CenterXVox, CenterYVox, N - 1 - SeedMarginVox)),
        };

        MedialCenterlineService service = new();
        CenterlineExtractionResult result = service.Extract(mask, seedSet);

        Assert.True(result.Succeeded, result.Summary);
        CenterlinePath path = result.Path!;

        Vector3D axis = VoxelToPatient(geometry, CenterXVox, CenterYVox, 0);
        double maxLateralError = 0;
        List<double> interiorRadii = [];
        foreach (CenterlinePathPoint point in path.Points)
        {
            double vz = (point.PatientPoint - geometry.Origin).Dot(UnitZ) / geometry.SpacingZ;
            if (vz < SeedMarginVox || vz > N - 1 - SeedMarginVox)
            {
                continue;
            }

            double dx = point.PatientPoint.X - axis.X;
            double dy = point.PatientPoint.Y - axis.Y;
            maxLateralError = Math.Max(maxLateralError, Math.Sqrt(dx * dx + dy * dy));
            interiorRadii.Add(point.RadiusMm!.Value);
        }

        Assert.True(maxLateralError < 0.6, $"Straight-tube interior lateral error {maxLateralError:0.000} mm exceeds 0.6 mm.");
        Assert.True(
            Math.Abs(interiorRadii.Average() - RadiusMm) < 0.5,
            $"Mean interior radius {interiorRadii.Average():0.000} mm deviates from {RadiusMm} mm.");
    }

    [Fact]
    public void Extract_CurvedTube_CentreWithinOneMillimetreAndRadiusAccurate()
    {
        VolumeGridGeometry geometry = Geometry();
        SegmentationMask3D mask = BuildCurvedTubeMask(geometry);

        int zStart = SeedMarginVox;
        int zEnd = N - 1 - SeedMarginVox;
        CenterlineSeedSet seedSet = new()
        {
            Label = "tube",
            StartSeed = Seed(CenterlineSeedKind.Start, VoxelToPatient(geometry, CurveCenterX(zStart), CenterYVox, zStart)),
            EndSeed = Seed(CenterlineSeedKind.End, VoxelToPatient(geometry, CurveCenterX(zEnd), CenterYVox, zEnd)),
        };

        MedialCenterlineService service = new();
        CenterlineExtractionResult result = service.Extract(mask, seedSet);

        Assert.True(result.Succeeded, result.Summary);
        CenterlinePath path = result.Path!;
        Assert.Equal(CenterlineComputationStatus.Success, path.Status);
        Assert.NotNull(path.RadiiMm);

        double maxCentreError = 0;
        double maxErrorAtZ = 0;
        List<double> interiorRadii = [];
        foreach (CenterlinePathPoint point in path.Points)
        {
            double vz = (point.PatientPoint - geometry.Origin).Dot(UnitZ) / geometry.SpacingZ;
            if (vz < SeedMarginVox || vz > N - 1 - SeedMarginVox)
            {
                continue;
            }

            int z = (int)Math.Round(vz);
            Vector3D trueCentre = VoxelToPatient(geometry, CurveCenterX(z), CenterYVox, z);
            double dx = point.PatientPoint.X - trueCentre.X;
            double dy = point.PatientPoint.Y - trueCentre.Y;
            double err = Math.Sqrt(dx * dx + dy * dy);
            if (err > maxCentreError)
            {
                maxCentreError = err;
                maxErrorAtZ = vz;
            }

            interiorRadii.Add(point.RadiusMm!.Value);
        }

        Assert.True(
            maxCentreError < 1.0,
            $"Centre deviation {maxCentreError:0.000} mm at z={maxErrorAtZ:0.0} exceeds 1 mm.");
        Assert.True(
            Math.Abs(interiorRadii.Average() - RadiusMm) < 0.5,
            $"Mean interior radius {interiorRadii.Average():0.000} mm deviates from {RadiusMm} mm.");
    }

    [Fact]
    public void Extract_MissingEndSeed_Fails()
    {
        VolumeGridGeometry geometry = Geometry();
        SegmentationMask3D mask = BuildCurvedTubeMask(geometry);
        CenterlineSeedSet seedSet = new()
        {
            StartSeed = Seed(CenterlineSeedKind.Start, VoxelToPatient(geometry, CurveCenterX(SeedMarginVox), CenterYVox, SeedMarginVox)),
        };

        MedialCenterlineService service = new();
        CenterlineExtractionResult result = service.Extract(mask, seedSet);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
    }

    [Fact]
    public void Extract_EmptyMask_Fails()
    {
        VolumeGridGeometry geometry = Geometry();
        SegmentationMaskBuffer empty = new(geometry);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        SegmentationMask3D mask = new(
            Guid.NewGuid(),
            "empty",
            "1.2.3.series",
            geometry.FrameOfReferenceUid,
            "1.2.3.study",
            geometry,
            empty.ToStorage(),
            new SegmentationMaskMetadata(SegmentationMaskSourceKind.ManualEdit, now, now, null, null, 0, null));

        CenterlineSeedSet seedSet = new()
        {
            StartSeed = Seed(CenterlineSeedKind.Start, new Vector3D(1, 1, 1)),
            EndSeed = Seed(CenterlineSeedKind.End, new Vector3D(10, 10, 10)),
        };

        MedialCenterlineService service = new();
        CenterlineExtractionResult result = service.Extract(mask, seedSet);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ResampleCatmullRom_ProducesApproximatelyUniformSpacing()
    {
        List<Vector3D> control =
        [
            new(0, 0, 0),
            new(10, 0, 0),
            new(20, 5, 0),
            new(30, 5, 10),
        ];

        List<Vector3D> resampled = MedialCenterlineService.ResampleCatmullRom(control, 1.0);

        Assert.True(resampled.Count > control.Count);
        for (int i = 1; i < resampled.Count; i++)
        {
            double step = (resampled[i] - resampled[i - 1]).Length;
            Assert.InRange(step, 0.25, 1.6);
        }

        Assert.Equal(control[0], resampled[0]);
        Assert.Equal(control[^1], resampled[^1]);
    }
}
