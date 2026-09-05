using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G4: pure, unit-testable coronary ostium metrics relative to the annulus
/// plane. Computes the axial height along the annulus axis, the horizontal (in-plane)
/// distance to the annulus center, and the angle to the annulus plane. No Avalonia,
/// no volume I/O.
/// </summary>
internal static class CoronaryOstiumService
{
    /// <summary>
    /// Computes the ostium metrics for a single ostium point relative to the annulus
    /// plane. The axial height is the signed projection of (ostium − center) onto the
    /// plane normal; the horizontal distance is the in-plane magnitude.
    /// </summary>
    public static CoronaryOstiumResult Compute(
        string label,
        Vector3D ostiumPoint,
        AnnulusPlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);

        Vector3D d = ostiumPoint - plane.Center;
        double axial = d.Dot(plane.Normal);
        Vector3D inPlane = d - plane.Normal * axial;
        double horizontal = inPlane.Length;

        // Angle between the ostium vector and the plane = 90° − angle to the normal.
        double angleToNormal = AngleDegrees(d, plane.Normal);
        double angleToPlane = 90.0 - angleToNormal;

        return new CoronaryOstiumResult
        {
            Label = label,
            AxialHeightMm = axial,
            HorizontalDistanceMm = horizontal,
            AngleToPlaneDegrees = angleToPlane,
        };
    }

    private static double AngleDegrees(Vector3D a, Vector3D b)
    {
        double lenA = a.Length;
        double lenB = b.Length;
        if (lenA < 1e-12 || lenB < 1e-12)
        {
            return 0.0;
        }

        double cos = Math.Clamp(a.Dot(b) / (lenA * lenB), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }
}
