using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer.Services;

internal interface IVascularPlanningMetricsService
{
    VascularPlanningMetrics Compute(SeriesVolume volume, SegmentationMask3D mask, CenterlinePath path, VascularPlanningBundle bundle);
}

internal sealed class VascularPlanningMetricsService : IVascularPlanningMetricsService
{
    private const double CrossSectionFieldOfViewMm = 60.0;
    private const int CrossSectionSampleGridSize = 96;

    public VascularPlanningMetrics Compute(SeriesVolume volume, SegmentationMask3D mask, CenterlinePath path, VascularPlanningBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bundle);

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        VascularSpanMetrics? proximalNeck = TryComputeSpanMetrics(path, bundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckStart), bundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckEnd), buffer);
        VascularSpanMetrics? distalLanding = TryComputeSpanMetrics(path, bundle.GetMarker(VascularPlanningMarkerKind.DistalLandingStart), bundle.GetMarker(VascularPlanningMarkerKind.DistalLandingEnd), buffer);
        double? neckAngulation = TryComputeNeckAngulation(path, bundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckStart), bundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckEnd));

        // Phase C2: neck conicity from the neck span's diameter samples (arc-length vs Øeq slope).
        VascularConicityMetrics neckConicity = VascularExtendedMetricsHelper.BuildConicity(
            ComputeConicityFromSamples(proximalNeck));

        // Phase C2: aneurysm-sac maximum equivalent diameter — the widest Øeq between the
        // proximal-neck end and the distal-landing start (the segment the sac occupies).
        double? aneurysmMaxDiameterMm = TryComputeSacMaxDiameter(
            path,
            bundle.GetMarker(VascularPlanningMarkerKind.ProximalNeckEnd),
            bundle.GetMarker(VascularPlanningMarkerKind.DistalLandingStart),
            buffer);

        string summary = BuildSummary(proximalNeck, distalLanding, neckAngulation);
        return new VascularPlanningMetrics
        {
            ProximalNeck = proximalNeck,
            DistalLanding = distalLanding,
            NeckAngulationDegrees = neckAngulation,
            Summary = summary,
            NeckConicity = neckConicity,
            AneurysmMaxDiameterMm = aneurysmMaxDiameterMm,
        };
    }

    private static double? ComputeConicityFromSamples(VascularSpanMetrics? span)
    {
        if (span?.Samples is null || span.Samples.Count < 2)
        {
            return null;
        }

        List<(double ArcLengthMm, double DiameterMm)> tuples = new(span.Samples.Count);
        foreach (VascularDiameterSample sample in span.Samples)
        {
            tuples.Add((sample.ArcLengthMm, sample.EquivalentDiameterMm));
        }

        return VascularExtendedMetricsHelper.ComputeConicityMmPer10Mm(tuples);
    }

    private static double? TryComputeSacMaxDiameter(CenterlinePath path, VascularPlanningMarker? sacStart, VascularPlanningMarker? sacEnd, SegmentationMaskBuffer buffer)
    {
        if (sacStart is null || sacEnd is null || path.Points.Count == 0)
        {
            return null;
        }

        int startIndex = Math.Clamp(Math.Min(sacStart.StationIndex, sacEnd.StationIndex), 0, path.Points.Count - 1);
        int endIndex = Math.Clamp(Math.Max(sacStart.StationIndex, sacEnd.StationIndex), 0, path.Points.Count - 1);

        double max = double.NegativeInfinity;
        bool any = false;
        foreach (int sampleIndex in BuildSampleIndices(startIndex, endIndex))
        {
            CrossSectionMeasurement? measurement = MeasureCrossSection(path, sampleIndex, buffer);
            if (measurement is null)
            {
                continue;
            }

            any = true;
            max = Math.Max(max, measurement.Value.EquivalentDiameterMm);
        }

        return any ? max : null;
    }

    private static VascularSpanMetrics? TryComputeSpanMetrics(CenterlinePath path, VascularPlanningMarker? start, VascularPlanningMarker? end, SegmentationMaskBuffer buffer)
    {
        if (start is null || end is null || path.Points.Count == 0)
        {
            return null;
        }

        int startIndex = Math.Clamp(Math.Min(start.StationIndex, end.StationIndex), 0, path.Points.Count - 1);
        int endIndex = Math.Clamp(Math.Max(start.StationIndex, end.StationIndex), 0, path.Points.Count - 1);
        if (endIndex < startIndex)
        {
            return null;
        }

        List<int> sampleIndices = BuildSampleIndices(startIndex, endIndex);
        List<VascularDiameterSample> samples = [];
        foreach (int sampleIndex in sampleIndices)
        {
            CrossSectionMeasurement? measurement = MeasureCrossSection(path, sampleIndex, buffer);
            if (measurement is null)
            {
                continue;
            }

            samples.Add(new VascularDiameterSample
            {
                StationIndex = sampleIndex,
                ArcLengthMm = path.Points[sampleIndex].ArcLengthMm,
                EquivalentDiameterMm = measurement.Value.EquivalentDiameterMm,
                MajorDiameterMm = measurement.Value.MajorDiameterMm,
                MinorDiameterMm = measurement.Value.MinorDiameterMm,
            });
        }

        if (samples.Count == 0)
        {
            return new VascularSpanMetrics
            {
                LengthMm = Math.Abs(path.Points[endIndex].ArcLengthMm - path.Points[startIndex].ArcLengthMm),
            };
        }

        return new VascularSpanMetrics
        {
            LengthMm = Math.Abs(path.Points[endIndex].ArcLengthMm - path.Points[startIndex].ArcLengthMm),
            MeanEquivalentDiameterMm = samples.Average(sample => sample.EquivalentDiameterMm),
            MinEquivalentDiameterMm = samples.Min(sample => sample.EquivalentDiameterMm),
            MaxEquivalentDiameterMm = samples.Max(sample => sample.EquivalentDiameterMm),
            MeanMajorDiameterMm = samples.Average(sample => sample.MajorDiameterMm),
            MeanMinorDiameterMm = samples.Average(sample => sample.MinorDiameterMm),
            Samples = samples,
        };
    }

    private static List<int> BuildSampleIndices(int startIndex, int endIndex)
    {
        if (endIndex <= startIndex)
        {
            return [startIndex];
        }

        int span = endIndex - startIndex;
        int targetSamples = Math.Min(8, span + 1);
        List<int> indices = [];
        for (int sample = 0; sample < targetSamples; sample++)
        {
            int index = startIndex + (int)Math.Round((sample / (double)Math.Max(1, targetSamples - 1)) * span);
            if (indices.Count == 0 || indices[^1] != index)
            {
                indices.Add(index);
            }
        }

        return indices;
    }

    private static double? TryComputeNeckAngulation(CenterlinePath path, VascularPlanningMarker? start, VascularPlanningMarker? end)
    {
        if (start is null || end is null || path.Points.Count <= 1)
        {
            return null;
        }

        int startIndex = Math.Clamp(start.StationIndex, 0, path.Points.Count - 1);
        int endIndex = Math.Clamp(end.StationIndex, 0, path.Points.Count - 1);
        Vector3D startTangent = GetTangent(path, startIndex);
        Vector3D endTangent = GetTangent(path, endIndex);
        if (startTangent.Length <= 1e-6 || endTangent.Length <= 1e-6)
        {
            return null;
        }

        double dot = Math.Clamp(startTangent.Normalize().Dot(endTangent.Normalize()), -1.0, 1.0);
        return Math.Acos(dot) * (180.0 / Math.PI);
    }

    private static CrossSectionMeasurement? MeasureCrossSection(CenterlinePath path, int stationIndex, SegmentationMaskBuffer buffer)
    {
        Vector3D center = path.Points[stationIndex].PatientPoint;
        Vector3D tangent = GetTangent(path, stationIndex);
        if (tangent.Length <= 1e-6)
        {
            return null;
        }

        (Vector3D row, Vector3D column) = BuildCrossSectionAxes(buffer.Geometry, tangent);
        double pixelSpacingMm = CrossSectionFieldOfViewMm / CrossSectionSampleGridSize;
        int foregroundCount = 0;
        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int minY = int.MaxValue;
        int maxY = int.MinValue;

        for (int y = 0; y < CrossSectionSampleGridSize; y++)
        {
            double offsetY = ((CrossSectionSampleGridSize - 1) * 0.5 - y) * pixelSpacingMm;
            for (int x = 0; x < CrossSectionSampleGridSize; x++)
            {
                double offsetX = (x - ((CrossSectionSampleGridSize - 1) * 0.5)) * pixelSpacingMm;
                Vector3D patientPoint = center + (row * offsetX) + (column * offsetY);
                if (!TrySampleMask(buffer, patientPoint))
                {
                    continue;
                }

                foregroundCount++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (foregroundCount == 0)
        {
            return null;
        }

        double areaMm2 = foregroundCount * pixelSpacingMm * pixelSpacingMm;
        double equivalentDiameter = 2.0 * Math.Sqrt(areaMm2 / Math.PI);
        double majorDiameter = Math.Max(1, maxX - minX + 1) * pixelSpacingMm;
        double minorDiameter = Math.Max(1, maxY - minY + 1) * pixelSpacingMm;
        return new CrossSectionMeasurement(equivalentDiameter, Math.Max(majorDiameter, minorDiameter), Math.Min(majorDiameter, minorDiameter));
    }

    private static bool TrySampleMask(SegmentationMaskBuffer buffer, Vector3D patientPoint)
    {
        (double vx, double vy, double vz) = PatientToVoxel(buffer.Geometry, patientPoint);
        int x = (int)Math.Round(vx);
        int y = (int)Math.Round(vy);
        int z = (int)Math.Round(vz);
        return buffer.Geometry.ContainsVoxel(x, y, z) && buffer.Get(x, y, z);
    }

    private static (double X, double Y, double Z) PatientToVoxel(VolumeGridGeometry geometry, Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - geometry.Origin;
        double vx = relative.Dot(geometry.RowDirection) / geometry.SpacingX;
        double vy = relative.Dot(geometry.ColumnDirection) / geometry.SpacingY;
        double vz = relative.Dot(geometry.Normal) / geometry.SpacingZ;
        return (vx, vy, vz);
    }

    private static (Vector3D Row, Vector3D Column) BuildCrossSectionAxes(VolumeGridGeometry geometry, Vector3D tangent)
    {
        Vector3D referenceUp = Math.Abs(tangent.Dot(geometry.Normal)) < 0.92 ? geometry.Normal : geometry.ColumnDirection;
        Vector3D row = tangent.Cross(referenceUp);
        if (row.Length <= 1e-6)
        {
            row = tangent.Cross(geometry.RowDirection);
        }

        row = row.Length > 1e-6 ? row.Normalize() : new Vector3D(1, 0, 0);
        Vector3D column = row.Cross(tangent);
        column = column.Length > 1e-6 ? column.Normalize() : new Vector3D(0, 1, 0);
        return (row, column);
    }

    private static Vector3D GetTangent(CenterlinePath path, int index)
    {
        if (path.Points.Count <= 1)
        {
            return new Vector3D(0, 0, 1);
        }

        Vector3D previous = path.Points[Math.Max(0, index - 1)].PatientPoint;
        Vector3D next = path.Points[Math.Min(path.Points.Count - 1, index + 1)].PatientPoint;
        Vector3D tangent = next - previous;
        return tangent.Length > 1e-6 ? tangent.Normalize() : new Vector3D(0, 0, 1);
    }

    private static string BuildSummary(VascularSpanMetrics? proximalNeck, VascularSpanMetrics? distalLanding, double? neckAngulation)
    {
        List<string> parts = [];
        if (proximalNeck?.LengthMm is double neckLength)
        {
            parts.Add($"Neck {neckLength:0.0} mm");
        }

        if (proximalNeck?.MeanEquivalentDiameterMm is double neckDiameter)
        {
            parts.Add($"neck Øeq {neckDiameter:0.0} mm");
        }

        if (neckAngulation is double angle)
        {
            parts.Add($"angulation {angle:0.0}°");
        }

        if (distalLanding?.LengthMm is double distalLength)
        {
            parts.Add($"distal {distalLength:0.0} mm");
        }

        if (distalLanding?.MeanEquivalentDiameterMm is double distalDiameter)
        {
            parts.Add($"distal Øeq {distalDiameter:0.0} mm");
        }

        return parts.Count == 0 ? "Planning markers pending." : string.Join(" · ", parts);
    }

    private readonly record struct CrossSectionMeasurement(double EquivalentDiameterMm, double MajorDiameterMm, double MinorDiameterMm);
}
