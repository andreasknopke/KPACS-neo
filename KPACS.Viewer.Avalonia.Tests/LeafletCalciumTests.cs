using KPACS.Viewer.Models;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure leaflet calcification analysis (Phase G3): Agatston-like
/// band factors, volume accumulation, score and severity classification. Pure model
/// surface — no Avalonia, no volume I/O.
/// </summary>
public class LeafletCalciumTests
{
    [Fact]
    public void BandFactor_returns_expected_factors()
    {
        Assert.Equal(0.0, LeafletCalciumService.BandFactor(100.0), 6);
        Assert.Equal(1.0, LeafletCalciumService.BandFactor(150.0), 6);
        Assert.Equal(2.0, LeafletCalciumService.BandFactor(250.0), 6);
        Assert.Equal(3.0, LeafletCalciumService.BandFactor(350.0), 6);
        Assert.Equal(4.0, LeafletCalciumService.BandFactor(500.0), 6);
    }

    [Fact]
    public void BandFactor_boundary_values()
    {
        Assert.Equal(1.0, LeafletCalciumService.BandFactor(130.0), 6);
        Assert.Equal(1.0, LeafletCalciumService.BandFactor(199.0), 6);
        Assert.Equal(2.0, LeafletCalciumService.BandFactor(200.0), 6);
        Assert.Equal(3.0, LeafletCalciumService.BandFactor(399.0), 6);
        Assert.Equal(4.0, LeafletCalciumService.BandFactor(400.0), 6);
    }

    [Fact]
    public void Compute_accumulates_volume_and_score()
    {
        // 3 voxels of 1 mm³ each: HU 350 (factor 3), 400 (factor 4), 500 (factor 4).
        var samples = new List<double> { 350.0, 400.0, 500.0 };
        LeafletCalciumResult result = LeafletCalciumService.Compute(samples, 1.0);

        Assert.Equal(3.0, result.VolumeMm3, 6);
        Assert.Equal(11.0, result.AgatstonScore, 6);
    }

    [Fact]
    public void Compute_ignores_non_calcium_samples()
    {
        var samples = new List<double> { 100.0, 200.0, 400.0 };
        LeafletCalciumResult result = LeafletCalciumService.Compute(samples, 1.0);

        Assert.Equal(1.0, result.VolumeMm3, 6);
        Assert.Equal(4.0, result.AgatstonScore, 6);
    }

    [Fact]
    public void Compute_empty_samples_returns_zero()
    {
        LeafletCalciumResult result = LeafletCalciumService.Compute([], 1.0);

        Assert.Equal(0.0, result.VolumeMm3, 6);
        Assert.Equal(0.0, result.AgatstonScore, 6);
        Assert.Equal(CalciumSeverity.None, result.Severity);
    }

    [Theory]
    [InlineData(0.0, CalciumSeverity.None)]
    [InlineData(399.0, CalciumSeverity.Light)]
    [InlineData(400.0, CalciumSeverity.Light)]
    [InlineData(799.0, CalciumSeverity.Moderate)]
    [InlineData(800.0, CalciumSeverity.Severe)]
    public void ClassifySeverity_boundaries(double score, CalciumSeverity expected)
    {
        Assert.Equal(expected, LeafletCalciumService.ClassifySeverity(score));
    }
}
