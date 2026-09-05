using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Derives branch-ostium landmarks (e.g. renal artery origins) relative to a reference
/// centerline: the nearest station, the distance from a proximal reference station, and the
/// clock position on the cross-section, using the parallel-transport frames from
/// <see cref="CenterlineFrameBuilder"/>. Pure geometry once the frames are supplied, so it is
/// unit-testable without a volume.
/// </summary>
internal static class OstiaLandmarkService
{
    /// <summary>
    /// Compute a single ostium landmark. <paramref name="frames"/> must be parallel to
    /// <paramref name="referencePath"/>.Points (as produced by
    /// <see cref="CenterlineFrameBuilder.BuildFrames"/>).
    /// </summary>
    public static OstiumLandmark ComputeLandmark(
        CenterlinePath referencePath,
        IReadOnlyList<CenterlineSampleFrame> frames,
        string label,
        Vector3D ostiumPatientPoint,
        double referenceStationMm)
    {
        ArgumentNullException.ThrowIfNull(referencePath);
        ArgumentNullException.ThrowIfNull(frames);

        if (referencePath.Points.Count == 0 || frames.Count != referencePath.Points.Count)
        {
            throw new ArgumentException("Frames must be parallel to the reference path points.", nameof(frames));
        }

        int nearestIndex = FindNearestPointIndex(referencePath, ostiumPatientPoint);
        CenterlineSampleFrame frame = frames[nearestIndex];
        double stationMm = referencePath.Points[nearestIndex].ArcLengthMm;

        Vector3D inPlane = ostiumPatientPoint - frame.PatientPoint;
        double normalComponent = inPlane.Dot(frame.Normal);
        double binormalComponent = inPlane.Dot(frame.Binormal);

        // Angle from the frame normal (12 o'clock) toward the binormal (3 o'clock), clockwise.
        double angleRadians = Math.Atan2(binormalComponent, normalComponent);
        if (angleRadians < 0)
        {
            angleRadians += Math.Tau;
        }

        double clockHours = angleRadians / Math.Tau * 12.0;

        return new OstiumLandmark
        {
            Label = label,
            PatientPoint = ostiumPatientPoint,
            StationMm = stationMm,
            DistanceFromReferenceMm = Math.Abs(stationMm - referenceStationMm),
            ClockHours = clockHours,
        };
    }

    private static int FindNearestPointIndex(CenterlinePath path, Vector3D point)
    {
        int best = 0;
        double bestDistanceSq = double.MaxValue;
        for (int i = 0; i < path.Points.Count; i++)
        {
            Vector3D delta = path.Points[i].PatientPoint - point;
            double distanceSq = delta.Dot(delta);
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                best = i;
            }
        }

        return best;
    }
}
