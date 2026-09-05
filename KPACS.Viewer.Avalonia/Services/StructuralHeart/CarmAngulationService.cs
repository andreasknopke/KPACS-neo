using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.StructuralHeart;

/// <summary>
/// Phase G4: pure, unit-testable C-arm angulation recommendation (LAO/RAO + CRA/CAU)
/// to view the annulus en-face, following the 3mensio / SlicerHeart VirtualCathLab
/// simplification. The angulation is derived geometrically from the annulus plane
/// normal relative to the patient AP axis. No Avalonia, no volume I/O.
/// </summary>
internal static class CarmAngulationService
{
    /// <summary>Patient AP axis (anterior-posterior), used as the reference for LAO/RAO.</summary>
    public static readonly Vector3D ApAxis = new(0, 0, 1);

    /// <summary>Patient superior-inferior axis, used as the reference for CRA/CAU.</summary>
    public static readonly Vector3D SiAxis = new(0, 1, 0);

    /// <summary>
    /// Computes the recommended LAO/RAO and CRA/CAU angulation to view the annulus
    /// en-face. The annulus normal is projected onto the AP-SI plane; the LAO/RAO angle
    /// is the rotation about the SI axis and the CRA/CAU angle is the rotation about the
    /// lateral axis. Positive LAO/RAO = LAO, negative = RAO; positive CRA/CAU = CRA,
    /// negative = CAU.
    /// </summary>
    public static CarmAngulationResult Compute(AnnulusPlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);

        Vector3D n = plane.Normal.Normalize();

        // LAO/RAO: angle between the normal's projection onto the AP-lateral plane and AP.
        // We treat the lateral axis as X (patient right-left). The normal's lateral
        // component drives LAO (positive) vs RAO (negative).
        double lateral = n.X;
        double ap = n.Z;
        double laoRao = Math.Atan2(lateral, ap) * 180.0 / Math.PI;

        // CRA/CAU: angle between the normal's projection onto the AP-SI plane and AP.
        double si = n.Y;
        double craCau = Math.Atan2(si, ap) * 180.0 / Math.PI;

        return new CarmAngulationResult
        {
            LaoRaoDegrees = laoRao,
            CraCauDegrees = craCau,
        };
    }
}
