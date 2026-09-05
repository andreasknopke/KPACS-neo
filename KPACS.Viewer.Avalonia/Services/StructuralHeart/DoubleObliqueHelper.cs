using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G2: pure, unit-testable double-oblique reformat geometry. Builds the en-face
/// reformat plane (parallel to the annulus best-fit plane, stepped along the annulus
/// axis) and the orthogonal long-axis plane (perpendicular to the annulus axis, showing
/// the annulus axis in-plane), and maps en-face image clicks back to patient space.
/// No Avalonia, no volume I/O: the service works on the annulus plane and scalar
/// geometry parameters.
/// </summary>
internal static class DoubleObliqueHelper
{
    /// <summary>Default offset range (mm) around the annulus plane for the en-face slider.</summary>
    public const double DefaultOffsetRangeMm = 25.0;

    /// <summary>
    /// Builds the en-face reformat plane: a plane parallel to the annulus plane, offset
    /// along the annulus axis by <paramref name="currentOffsetMm"/>. The row/column basis
    /// is an orthonormal in-plane basis; the normal is the annulus axis.
    /// </summary>
    public static VolumeSlicePlane BuildEnFacePlane(
        AnnulusPlane annulus,
        double pixelSpacingX,
        double pixelSpacingY,
        double minOffsetMm,
        double maxOffsetMm,
        double currentOffsetMm,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(annulus);

        Vector3D n = annulus.Normal.Normalize();
        Vector3D u = OrthonormalBasisU(n);
        Vector3D v = n.Cross(u).Normalize();

        double step = Math.Max(0.1, Math.Min(pixelSpacingX, pixelSpacingY));
        double depthRange = Math.Max(0, maxOffsetMm - minOffsetMm);
        int count = Math.Max(1, (int)Math.Floor(depthRange / step) + 1);

        return new VolumeSlicePlane
        {
            VolumeCenter = annulus.Center + n * Math.Clamp(currentOffsetMm, minOffsetMm, maxOffsetMm),
            RowDirection = u,
            ColumnDirection = v,
            Normal = n,
            PixelSpacingX = Math.Max(0.1, pixelSpacingX),
            PixelSpacingY = Math.Max(0.1, pixelSpacingY),
            SliceSpacingMm = step,
            ScrollStepMm = step,
            MinOffsetMm = minOffsetMm,
            MaxOffsetMm = maxOffsetMm,
            CurrentOffsetMm = Math.Clamp(currentOffsetMm, minOffsetMm, maxOffsetMm),
            SliceCount = count,
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
        };
    }

    /// <summary>
    /// Builds the orthogonal long-axis plane: a plane through the annulus center that
    /// contains the annulus axis (so the axis appears vertically in the image). Its
    /// normal is perpendicular to the annulus axis.
    /// </summary>
    public static VolumeSlicePlane BuildLongAxisPlane(
        AnnulusPlane annulus,
        double pixelSpacingX,
        double pixelSpacingY,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(annulus);

        Vector3D n = annulus.Normal.Normalize();
        Vector3D u = OrthonormalBasisU(n);
        Vector3D v = n.Cross(u).Normalize();

        // Row = annulus axis (vertical), Column = in-plane v, Normal = u (perpendicular).
        return new VolumeSlicePlane
        {
            VolumeCenter = annulus.Center,
            RowDirection = n,
            ColumnDirection = v,
            Normal = u,
            PixelSpacingX = Math.Max(0.1, pixelSpacingX),
            PixelSpacingY = Math.Max(0.1, pixelSpacingY),
            SliceSpacingMm = 0.5,
            ScrollStepMm = 0.5,
            MinOffsetMm = 0,
            MaxOffsetMm = 0,
            CurrentOffsetMm = 0,
            SliceCount = 1,
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
        };
    }

    /// <summary>
    /// Maps an en-face image pixel (in image coordinates, origin top-left) to a patient
    /// point on the en-face plane at the given offset. The image X axis maps to the
    /// in-plane row basis and the image Y axis to the in-plane column basis.
    /// </summary>
    public static Vector3D ClickToPatientPoint(
        AnnulusPlane annulus,
        double offsetMm,
        double pixelX,
        double pixelY,
        double pixelSpacingX,
        double pixelSpacingY)
    {
        ArgumentNullException.ThrowIfNull(annulus);

        Vector3D n = annulus.Normal.Normalize();
        Vector3D u = OrthonormalBasisU(n);
        Vector3D v = n.Cross(u).Normalize();

        Vector3D center = annulus.Center + n * offsetMm;
        return center + u * (pixelX * pixelSpacingX) + v * (pixelY * pixelSpacingY);
    }

    /// <summary>
    /// Computes the offset range (min, max, count) for the en-face slider given a total
    /// depth range and a step size, centered on the annulus plane (offset 0).
    /// </summary>
    public static (double Min, double Max, int Count) ComputeOffsetRange(double depthMm, double stepMm)
    {
        double half = Math.Max(0, depthMm) * 0.5;
        double min = -half;
        double max = half;
        double step = Math.Max(0.1, stepMm);
        int count = Math.Max(1, (int)Math.Floor((max - min) / step) + 1);
        return (min, max, count);
    }

    private static Vector3D OrthonormalBasisU(Vector3D normal)
    {
        Vector3D refVec = Math.Abs(normal.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        Vector3D u = refVec - normal * refVec.Dot(normal);
        return u.Length > 1e-12 ? u.Normalize() : new Vector3D(1, 0, 0);
    }
}
