using System.Runtime.CompilerServices;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

internal static class CenterlineFrameBuilder
{
    private const double DefaultReferenceDotThreshold = 0.92;

    // ── Cached frames list ───────────────────────────────────────
    // BuildFrames is expensive (cross products, normalizations, rotations per point).
    // GetFrame previously rebuilt the entire list on every call, causing O(n²) cost
    // when scrubbing along the centerline.  We now cache the result keyed by
    // (volume, path, rotation) and index directly into the list in O(1).
    private static readonly ConditionalWeakTable<CenterlinePath, CachedFrameList> s_frameCache = new();

    private sealed class CachedFrameList
    {
        public SeriesVolume? Volume;
        public double AxialRotationRadians;
        public IReadOnlyList<CenterlineSampleFrame>? Frames;
    }

    /// <summary>
    /// Returns the cached frame list for the given path, rebuilding only when the
    /// volume or rotation have changed.
    /// </summary>
    private static IReadOnlyList<CenterlineSampleFrame> GetOrBuildFrames(
        SeriesVolume volume,
        CenterlinePath path,
        double axialRotationRadians)
    {
        CachedFrameList entry = s_frameCache.GetOrCreateValue(path);

        if (entry.Frames is not null
            && ReferenceEquals(entry.Volume, volume)
            && Math.Abs(entry.AxialRotationRadians - axialRotationRadians) <= 1e-9)
        {
            return entry.Frames;
        }

        IReadOnlyList<CenterlineSampleFrame> frames = BuildFrames(volume, path, axialRotationRadians);
        entry.Volume = volume;
        entry.AxialRotationRadians = axialRotationRadians;
        entry.Frames = frames;
        return frames;
    }

    public static IReadOnlyList<CenterlineSampleFrame> BuildFrames(
        SeriesVolume volume,
        CenterlinePath path,
        double axialRotationRadians = 0)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(path);

        List<CenterlineSampleFrame> frames = new(path.Points.Count);
        if (path.Points.Count == 0)
        {
            return frames;
        }

        Vector3D previousNormal = new(0, 0, 0);
        Vector3D initialTangent = path.Points.Count > 1
            ? GetTangent(path, 0)
            : volume.Normal;
        Vector3D referenceUp = Math.Abs(volume.Normal.Dot(initialTangent)) < DefaultReferenceDotThreshold
            ? volume.Normal
            : volume.ColumnDirection;

        for (int index = 0; index < path.Points.Count; index++)
        {
            Vector3D tangent = GetTangent(path, index);
            Vector3D normal = ProjectOntoPlane(previousNormal.Length > 1e-6 ? previousNormal : referenceUp, tangent);
            if (normal.Length <= 1e-6)
            {
                normal = ProjectOntoPlane(volume.Normal, tangent);
            }

            if (normal.Length <= 1e-6)
            {
                normal = ProjectOntoPlane(volume.ColumnDirection, tangent);
            }

            if (normal.Length <= 1e-6)
            {
                normal = Math.Abs(tangent.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
                normal = ProjectOntoPlane(normal, tangent);
            }

            normal = normal.Length > 1e-6 ? normal.Normalize() : new Vector3D(0, 1, 0);
            Vector3D binormal = tangent.Cross(normal);
            if (binormal.Length <= 1e-6)
            {
                binormal = tangent.Cross(volume.RowDirection);
            }

            binormal = binormal.Length > 1e-6 ? binormal.Normalize() : new Vector3D(1, 0, 0);
            normal = binormal.Cross(tangent);
            normal = normal.Length > 1e-6 ? normal.Normalize() : new Vector3D(0, 1, 0);

            // Store the UNROTATED normal for the next frame's continuity
            // propagation.  The axial rotation is a view-only transform —
            // feeding the rotated normal back into the Frenet frame walk
            // would compound the angle (frame N would see N·θ instead of θ).
            previousNormal = normal;

            if (Math.Abs(axialRotationRadians) > 1e-6)
            {
                normal = RotateAroundAxis(normal, tangent, axialRotationRadians);
                binormal = RotateAroundAxis(binormal, tangent, axialRotationRadians);
                normal = normal.Length > 1e-6 ? normal.Normalize() : normal;
                binormal = binormal.Length > 1e-6 ? binormal.Normalize() : binormal;
            }

            frames.Add(new CenterlineSampleFrame(path.Points[index].PatientPoint, tangent, normal, binormal));
        }

        return frames;
    }

    public static CenterlineSampleFrame GetFrame(
        SeriesVolume volume,
        CenterlinePath path,
        int index,
        double axialRotationRadians = 0)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Points.Count == 0)
        {
            return new CenterlineSampleFrame(new Vector3D(0, 0, 0), new Vector3D(0, 0, 1), new Vector3D(0, 1, 0), new Vector3D(1, 0, 0));
        }

        IReadOnlyList<CenterlineSampleFrame> frames = GetOrBuildFrames(volume, path, axialRotationRadians);
        int clampedIndex = Math.Clamp(index, 0, frames.Count - 1);
        return frames[clampedIndex];
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
        if (tangent.Length <= 1e-6 && index > 0)
        {
            tangent = path.Points[index].PatientPoint - previous;
        }

        return tangent.Length > 1e-6 ? tangent.Normalize() : new Vector3D(0, 0, 1);
    }

    private static Vector3D ProjectOntoPlane(Vector3D vector, Vector3D normal)
    {
        if (vector.Length <= 1e-6 || normal.Length <= 1e-6)
        {
            return new Vector3D(0, 0, 0);
        }

        return vector - (normal * vector.Dot(normal));
    }

    private static Vector3D RotateAroundAxis(Vector3D value, Vector3D axis, double angleRadians)
    {
        if (value.Length <= 1e-6 || axis.Length <= 1e-6 || Math.Abs(angleRadians) <= 1e-6)
        {
            return value;
        }

        Vector3D normalizedAxis = axis.Normalize();
        double cosine = Math.Cos(angleRadians);
        double sine = Math.Sin(angleRadians);

        return (value * cosine)
            + (normalizedAxis.Cross(value) * sine)
            + (normalizedAxis * (normalizedAxis.Dot(value) * (1 - cosine)));
    }
}

internal readonly record struct CenterlineSampleFrame(Vector3D PatientPoint, Vector3D Tangent, Vector3D Normal, Vector3D Binormal);