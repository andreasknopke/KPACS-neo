using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G3: pure, unit-testable leaflet calcification analysis (Agatston-like).
/// The service classifies HU samples into density bands, accumulates an Agatston-like
/// score (band factor × area) and derives a severity class. No Avalonia, no volume I/O:
/// the wiring layer supplies the HU samples and their voxel volume.
/// </summary>
internal static class LeafletCalciumService
{
    /// <summary>HU threshold above which a voxel counts as calcium.</summary>
    public const double CalciumHuThreshold = 350.0;

    /// <summary>Agatston band boundaries (HU).</summary>
    public static readonly (double MinHu, double MaxHu, double Factor)[] Bands =
    [
        (130.0, 199.0, 1.0),
        (200.0, 299.0, 2.0),
        (300.0, 399.0, 3.0),
        (400.0, double.PositiveInfinity, 4.0),
    ];

    /// <summary>
    /// Computes the calcium volume and Agatston-like score from HU samples. Each sample
    /// is a single voxel; the voxel volume (mm³) scales the area contribution.
    /// </summary>
    public static LeafletCalciumResult Compute(
        IReadOnlyList<double> huSamples,
        double voxelVolumeMm3)
    {
        ArgumentNullException.ThrowIfNull(huSamples);

        double volumeMm3 = 0.0;
        double score = 0.0;

        foreach (double hu in huSamples)
        {
            if (hu < CalciumHuThreshold)
            {
                continue;
            }

            volumeMm3 += voxelVolumeMm3;
            double factor = BandFactor(hu);
            score += factor * voxelVolumeMm3;
        }

        return new LeafletCalciumResult
        {
            VolumeMm3 = volumeMm3,
            AgatstonScore = score,
            Severity = ClassifySeverity(score),
        };
    }

    /// <summary>
    /// Returns the Agatston band factor for an HU value (1–4). Values below the calcium
    /// threshold return 0.
    /// </summary>
    public static double BandFactor(double hu)
    {
        foreach ((double minHu, double maxHu, double factor) in Bands)
        {
            if (hu >= minHu && hu <= maxHu)
            {
                return factor;
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Classifies the Agatston-like score into a severity: None, Light, Moderate, Severe.
    /// </summary>
    public static CalciumSeverity ClassifySeverity(double agatstonScore)
    {
        return agatstonScore switch
        {
            <= 0.0 => CalciumSeverity.None,
            <= 400.0 => CalciumSeverity.Light,
            < 800.0 => CalciumSeverity.Moderate,
            _ => CalciumSeverity.Severe,
        };
    }
}
