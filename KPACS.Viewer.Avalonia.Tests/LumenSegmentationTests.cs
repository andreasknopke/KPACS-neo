using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Phantom tests for lumen segmentation and EVAR sub-mask derivation. A synthetic vessel with a
/// contrast-filled lumen, a mural-thrombus crescent, and a calcific island is segmented; the
/// lumen volume and the sub-mask classification are checked against the known ground truth.
/// Pure model/math surface — no Avalonia, no Dispatcher.
/// </summary>
public class LumenSegmentationTests
{
    private static readonly Vector3D UnitX = new(1, 0, 0);
    private static readonly Vector3D UnitY = new(0, 1, 0);
    private static readonly Vector3D UnitZ = new(0, 0, 1);

    private const int SizeX = 40;
    private const int SizeY = 40;
    private const int SizeZ = 20;
    private const double Spacing = 0.5;
    private const double CenterX = 20.0;
    private const double CenterY = 20.0;
    private const double LumenRadiusVox = 8.0;

    private const short AirHu = -1000;
    private const short LumenHu = 400;
    private const short ThrombusHu = 100;

    // Dense calcium sits above the lumen HU band upper bound (1500) so the region grow stops at
    // the wall, while still exceeding the calcification threshold (350) for sub-mask detection.
    private const short CalciumHu = 1600;

    private static VolumeGridGeometry Geometry() =>
        new(SizeX, SizeY, SizeZ, Spacing, Spacing, Spacing, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.lumen.for");

    private static SeriesVolume BuildPhantomVolume(out int expectedLumenVoxels)
    {
        short[] voxels = new short[SizeX * SizeY * SizeZ];
        Array.Fill(voxels, AirHu);

        int lumenCount = 0;
        for (int z = 0; z < SizeZ; z++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int x = 0; x < SizeX; x++)
                {
                    double dx = x - CenterX;
                    double dy = y - CenterY;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    int idx = (z * SizeY * SizeX) + (y * SizeX) + x;

                    if (r <= LumenRadiusVox)
                    {
                        voxels[idx] = LumenHu;
                        lumenCount++;
                    }
                    else if (r <= LumenRadiusVox + 4 && dx > 0)
                    {
                        // Mural thrombus crescent on the +X side, just outside the lumen.
                        voxels[idx] = ThrombusHu;
                    }
                    else if (r <= LumenRadiusVox + 2 && dx < 0 && Math.Abs(dy) < 3)
                    {
                        // Calcification island on the -X wall, adjacent to the lumen.
                        voxels[idx] = CalciumHu;
                    }
                }
            }
        }

        expectedLumenVoxels = lumenCount;

        return new SeriesVolume(
            voxels,
            SizeX, SizeY, SizeZ,
            Spacing, Spacing, Spacing,
            new Vector3D(0, 0, 0),
            UnitX, UnitY, UnitZ,
            40, 400,
            AirHu, CalciumHu,
            false,
            "1.2.3.series",
            Geometry().FrameOfReferenceUid,
            string.Empty,
            [],
            []);
    }

    private static Vector3D VoxelToPatient(double vx, double vy, double vz) =>
        (UnitX * (vx * Spacing)) + (UnitY * (vy * Spacing)) + (UnitZ * (vz * Spacing));

    [Fact]
    public void Segment_ProducesLumenWithinFivePercentOfGroundTruth()
    {
        SeriesVolume volume = BuildPhantomVolume(out int expectedLumen);
        VolumeGridGeometry geometry = Geometry();

        LumenSegmentationService service = new();
        LumenSegmentationResult result = service.Segment(
            volume,
            geometry,
            [VoxelToPatient(CenterX, CenterY, SizeZ / 2.0)],
            new LumenSegmentationOptions { ClosingRadiusMm = 0 });

        Assert.True(result.Succeeded, result.Summary);
        Assert.NotNull(result.LumenMask);

        int actual = result.LumenMask!.Storage.ForegroundVoxelCount;
        double error = Math.Abs(actual - expectedLumen) / (double)expectedLumen;
        Assert.True(error < 0.05, $"Lumen voxel count {actual} deviates {error:P1} from expected {expectedLumen}.");
    }

    [Fact]
    public void Segment_CalciumSubMaskOnlyContainsHighHuVoxels()
    {
        SeriesVolume volume = BuildPhantomVolume(out _);
        VolumeGridGeometry geometry = Geometry();

        LumenSegmentationService service = new();
        LumenSegmentationResult result = service.Segment(
            volume,
            geometry,
            [VoxelToPatient(CenterX, CenterY, SizeZ / 2.0)],
            new LumenSegmentationOptions { ClosingRadiusMm = 0, DeriveThrombus = false });

        Assert.NotNull(result.CalciumMask);
        SegmentationMaskBuffer calcium = SegmentationMaskBuffer.FromStorage(geometry, result.CalciumMask!.Storage);

        int checkedVoxels = 0;
        foreach (int linear in calcium.EnumerateForegroundLinearIndices())
        {
            int z = linear / (SizeX * SizeY);
            int rem = linear - (z * SizeX * SizeY);
            int y = rem / SizeX;
            int x = rem - (y * SizeX);
            Assert.True(volume.GetVoxel(x, y, z) >= 350, "Calcium sub-mask contains a voxel below the HU threshold.");
            checkedVoxels++;
        }

        Assert.True(checkedVoxels > 0, "Expected a non-empty calcification sub-mask.");
    }

    [Fact]
    public void Segment_ThrombusSubMaskIsOutsideLumenAndInHuBand()
    {
        SeriesVolume volume = BuildPhantomVolume(out _);
        VolumeGridGeometry geometry = Geometry();

        LumenSegmentationService service = new();
        LumenSegmentationResult result = service.Segment(
            volume,
            geometry,
            [VoxelToPatient(CenterX, CenterY, SizeZ / 2.0)],
            new LumenSegmentationOptions { ClosingRadiusMm = 0, DeriveCalcium = false });

        Assert.NotNull(result.ThrombusMask);
        SegmentationMaskBuffer thrombus = SegmentationMaskBuffer.FromStorage(geometry, result.ThrombusMask!.Storage);
        SegmentationMaskBuffer lumen = SegmentationMaskBuffer.FromStorage(geometry, result.LumenMask!.Storage);

        int checkedVoxels = 0;
        foreach (int linear in thrombus.EnumerateForegroundLinearIndices())
        {
            Assert.False(lumen.GetLinear(linear), "Thrombus sub-mask overlaps the lumen.");

            int z = linear / (SizeX * SizeY);
            int rem = linear - (z * SizeX * SizeY);
            int y = rem / SizeX;
            int x = rem - (y * SizeX);
            double hu = volume.GetVoxel(x, y, z);
            Assert.InRange(hu, 40, 150);
            checkedVoxels++;
        }

        Assert.True(checkedVoxels > 0, "Expected a non-empty thrombus sub-mask.");
    }

    [Fact]
    public void Segment_NoSeedInBand_Fails()
    {
        SeriesVolume volume = BuildPhantomVolume(out _);
        VolumeGridGeometry geometry = Geometry();

        LumenSegmentationService service = new();
        LumenSegmentationResult result = service.Segment(
            volume,
            geometry,
            [VoxelToPatient(1, 1, 1)],
            new LumenSegmentationOptions { ClosingRadiusMm = 0 });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void LargestConnectedComponent_KeepsOnlyBiggestBlob()
    {
        VolumeGridGeometry geometry = new(10, 10, 10, 1, 1, 1, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.cc.for");
        bool[] mask = new bool[geometry.TotalVoxelCount];

        // Big 3x3x3 block near origin.
        for (int z = 1; z <= 3; z++)
        {
            for (int y = 1; y <= 3; y++)
            {
                for (int x = 1; x <= 3; x++)
                {
                    mask[x + (y * 10) + (z * 100)] = true;
                }
            }
        }

        // Isolated single voxel far away.
        mask[8 + (8 * 10) + (8 * 100)] = true;

        bool[] result = LumenSegmentationService.LargestConnectedComponent(mask, geometry, CancellationToken.None);

        Assert.True(result[2 + (2 * 10) + (2 * 100)]);
        Assert.False(result[8 + (8 * 10) + (8 * 100)]);
        Assert.Equal(27, result.Count(v => v));
    }

    [Fact]
    public void FillHolesPerSlice_FillsEnclosedPocket()
    {
        VolumeGridGeometry geometry = new(10, 10, 1, 1, 1, 1, new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.hole.for");
        bool[] mask = new bool[geometry.TotalVoxelCount];

        // Ring of foreground with an enclosed background hole at (5,5).
        for (int y = 3; y <= 7; y++)
        {
            for (int x = 3; x <= 7; x++)
            {
                bool isRing = x == 3 || x == 7 || y == 3 || y == 7;
                mask[x + (y * 10)] = isRing;
            }
        }

        Assert.False(mask[5 + (5 * 10)]);

        bool[] result = LumenSegmentationService.FillHolesPerSlice(mask, geometry, CancellationToken.None);

        Assert.True(result[5 + (5 * 10)], "Enclosed hole was not filled.");
        // Background reachable from the border stays background.
        Assert.False(result[0]);
    }
}
