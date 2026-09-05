using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure EVAR segmentation helpers (Phase B2): aorta/iliac filtering,
/// mask union, and status-card statistics. Pure model surface — no Avalonia, no I/O.
/// </summary>
public class VascularSegmentationHelperTests
{
    private static readonly Vector3D UnitX = new(1, 0, 0);
    private static readonly Vector3D UnitY = new(0, 1, 0);
    private static readonly Vector3D UnitZ = new(0, 0, 1);

    private const int SizeX = 10;
    private const int SizeY = 10;
    private const int SizeZ = 6;
    private const double Spacing = 1.0;

    private static VolumeGridGeometry Geometry() =>
        new(SizeX, SizeY, SizeZ, Spacing, Spacing, Spacing,
            new Vector3D(0, 0, 0), UnitX, UnitY, UnitZ, "1.2.840.vasc.for");

    private static SegmentationMask3D BuildMask(string name, Action<SegmentationMaskBuffer> paint)
    {
        VolumeGridGeometry geometry = Geometry();
        var buffer = new SegmentationMaskBuffer(geometry);
        paint(buffer);
        return VascularSegmentationHelper.FromBuffer(
            geometry, name, "1.2.840.series", "1.2.840.vasc.for", "1.2.840.study",
            buffer, SegmentationMaskSourceKind.Imported);
    }

    [Theory]
    [InlineData("Aorta", true)]
    [InlineData("Left Iliac Artery", true)]
    [InlineData("iliac_artery_right", true)]
    [InlineData("Abdominal Aorta", true)]
    [InlineData("Left Iliac Vena", false)]
    [InlineData("iliac_vena_left", false)]
    [InlineData("Liver", false)]
    [InlineData("Femur", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVascularStructure_filters_arterial_tree(string? name, bool expected) =>
        Assert.Equal(expected, VascularSegmentationHelper.IsVascularStructure(name));

    [Fact]
    public void FilterVascular_keeps_only_aorta_and_iliac_arteries()
    {
        List<SegmentationMask3D> masks =
        [
            BuildMask("Aorta", _ => { }),
            BuildMask("Left Iliac Artery", _ => { }),
            BuildMask("Left Iliac Vena", _ => { }),
            BuildMask("Liver", _ => { }),
        ];

        IReadOnlyList<SegmentationMask3D> filtered = VascularSegmentationHelper.FilterVascular(masks);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, m => m.Name == "Aorta");
        Assert.Contains(filtered, m => m.Name == "Left Iliac Artery");
    }

    [Fact]
    public void Union_combines_disjoint_masks()
    {
        // Mask A: a single voxel at (0,0,0). Mask B: a single voxel at (5,5,5).
        SegmentationMask3D a = BuildMask("Aorta", b => b.Set(0, 0, 0, true));
        SegmentationMask3D b = BuildMask("Left Iliac Artery", buf => buf.Set(5, 5, 5, true));

        SegmentationMask3D? union = VascularSegmentationHelper.Union(
            Geometry(), "Lumen", "1.2.840.series", "1.2.840.vasc.for", "1.2.840.study", [a, b]);

        Assert.NotNull(union);
        SegmentationMaskBuffer result = SegmentationMaskBuffer.FromStorage(union!.Geometry, union.Storage);
        Assert.True(result.Get(0, 0, 0));
        Assert.True(result.Get(5, 5, 5));
        Assert.Equal(2, result.CountForeground());
    }

    [Fact]
    public void Union_deduplicates_overlapping_voxels()
    {
        // Both masks set the same voxel; the union must count it once.
        SegmentationMask3D a = BuildMask("Aorta", b => b.Set(2, 3, 1, true));
        SegmentationMask3D b = BuildMask("Left Iliac Artery", buf => buf.Set(2, 3, 1, true));

        SegmentationMask3D? union = VascularSegmentationHelper.Union(
            Geometry(), "Lumen", "1.2.840.series", "1.2.840.vasc.for", "1.2.840.study", [a, b]);

        Assert.NotNull(union);
        SegmentationMaskBuffer result = SegmentationMaskBuffer.FromStorage(union!.Geometry, union.Storage);
        Assert.Equal(1, result.CountForeground());
    }

    [Fact]
    public void Union_returns_null_for_empty_input()
    {
        SegmentationMask3D? union = VascularSegmentationHelper.Union(
            Geometry(), "Lumen", "1.2.840.series", "1.2.840.vasc.for", "1.2.840.study", []);
        Assert.Null(union);
    }

    [Fact]
    public void Union_returns_null_when_all_masks_empty()
    {
        SegmentationMask3D a = BuildMask("Aorta", _ => { });
        SegmentationMask3D b = BuildMask("Left Iliac Artery", _ => { });

        SegmentationMask3D? union = VascularSegmentationHelper.Union(
            Geometry(), "Lumen", "1.2.840.series", "1.2.840.vasc.for", "1.2.840.study", [a, b]);
        Assert.Null(union);
    }

    [Fact]
    public void CountCoveredZSlices_counts_distinct_z_planes()
    {
        // Foreground on z=0 and z=3 only → 2 covered slices.
        SegmentationMask3D mask = BuildMask("Aorta", b =>
        {
            b.Set(1, 1, 0, true);
            b.Set(2, 2, 0, true);
            b.Set(4, 4, 3, true);
        });

        Assert.Equal(2, VascularSegmentationHelper.CountCoveredZSlices(mask));
    }

    [Fact]
    public void GetVolumeCubicCentimeters_matches_voxel_count_at_1mm_spacing()
    {
        // 3 voxels at 1mm³ each = 3 mm³ = 0.003 cm³.
        SegmentationMask3D mask = BuildMask("Aorta", b =>
        {
            b.Set(0, 0, 0, true);
            b.Set(1, 0, 0, true);
            b.Set(2, 0, 0, true);
        });

        double cm3 = VascularSegmentationHelper.GetVolumeCubicCentimeters(mask);
        Assert.Equal(0.003, cm3, precision: 6);
    }
}
