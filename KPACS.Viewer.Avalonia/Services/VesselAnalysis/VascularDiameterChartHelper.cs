using System.Globalization;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Pure, deterministic data-shaping helpers for the EVAR diameter chart (Phase C1).
/// Turns a computed <see cref="CenterlinePath"/> (or the metrics service's
/// <see cref="VascularDiameterSample"/> list) into the (arc-length, diameter) points the
/// ScottPlot chart draws, and maps a chart click back to the nearest centerline station so
/// the chart and the cross-section/CPR views stay in sync. No Avalonia, no I/O — fully
/// unit-testable.
/// </summary>
internal static class VascularDiameterChartHelper
{
    /// <summary>
    /// A single chart sample: <see cref="ArcLengthMm"/> is the distance along the centerline
    /// from the proximal reference (chart X), <see cref="DiameterMm"/> the vessel diameter
    /// there (chart Y).
    /// </summary>
    public readonly record struct ChartPoint(double ArcLengthMm, double DiameterMm);

    /// <summary>
    /// Build the diameter curve from a centerline path. Diameter is twice the
    /// max-inscribed-sphere radius at each point. Points without a computed radius are
    /// skipped, so a path with no radii yields an empty curve rather than a misleading line.
    /// </summary>
    public static IReadOnlyList<ChartPoint> BuildFromPath(CenterlinePath? path)
    {
        if (path is null || !path.HasRenderablePath)
        {
            return [];
        }

        double[]? radii = path.RadiiMm;
        List<ChartPoint> points = [];
        for (int i = 0; i < path.Points.Count; i++)
        {
            double? radius = RadiusAt(path, radii, i);
            if (radius is double r && r > 0)
            {
                points.Add(new ChartPoint(path.Points[i].ArcLengthMm, 2.0 * r));
            }
        }

        return points;
    }

    /// <summary>
    /// Build the diameter curve from the metrics service's equivalent-diameter samples.
    /// </summary>
    public static IReadOnlyList<ChartPoint> BuildFromSamples(IReadOnlyList<VascularDiameterSample>? samples)
    {
        if (samples is null || samples.Count == 0)
        {
            return [];
        }

        List<ChartPoint> points = new(samples.Count);
        foreach (VascularDiameterSample sample in samples)
        {
            if (sample.EquivalentDiameterMm > 0)
            {
                points.Add(new ChartPoint(sample.ArcLengthMm, sample.EquivalentDiameterMm));
            }
        }

        return points;
    }

    /// <summary>
    /// Map a chart position (arc length in mm) to the index of the nearest centerline point,
    /// so a click/drag in the chart can drive the cross-section station. Returns -1 for an
    /// unusable path. Uses the monotonic arc-length along the path.
    /// </summary>
    public static int ResolveStationIndex(CenterlinePath? path, double arcLengthMm)
    {
        if (path is null || !path.HasRenderablePath)
        {
            return -1;
        }

        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < path.Points.Count; i++)
        {
            double distance = Math.Abs(path.Points[i].ArcLengthMm - arcLengthMm);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Compact axis titles for the chart. Uses invariant culture so the text is identical
    /// regardless of the host locale.
    /// </summary>
    public static string FormatXAxisLabel(double maxArcLengthMm) =>
        string.Format(CultureInfo.InvariantCulture, "Station (mm ab proximal, 0–{0:0})", maxArcLengthMm);

    public static string FormatYAxisLabel() => "Durchmesser (mm)";

    private static double? RadiusAt(CenterlinePath path, double[]? radii, int index)
    {
        if (radii is not null && index < radii.Length)
        {
            return radii[index];
        }

        return path.Points[index].RadiusMm;
    }
}
