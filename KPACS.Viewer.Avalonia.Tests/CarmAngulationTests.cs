using KPACS.Viewer.Models;
using KPACS.Viewer.Services.StructuralHeart;

namespace KPACS.Viewer.Avalonia.Tests;

/// <summary>
/// Unit tests for the pure C-arm angulation recommendation (Phase G4): LAO/RAO and
/// CRA/CAU angles derived geometrically from the annulus plane normal. Pure model
/// surface — no Avalonia, no volume I/O.
/// </summary>
public class CarmAngulationTests
{
    [Fact]
    public void Compute_ap_facing_normal_gives_zero_angulation()
    {
        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(0, 0, 1) };
        CarmAngulationResult r = CarmAngulationService.Compute(plane);

        Assert.Equal(0.0, r.LaoRaoDegrees, 6);
        Assert.Equal(0.0, r.CraCauDegrees, 6);
    }

    [Fact]
    public void Compute_lateral_tilt_gives_lao()
    {
        // Normal tilted 30° toward the patient's left (positive X).
        double tilt = 30.0 * Math.PI / 180.0;
        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(Math.Sin(tilt), 0, Math.Cos(tilt)) };
        CarmAngulationResult r = CarmAngulationService.Compute(plane);

        Assert.Equal(30.0, r.LaoRaoDegrees, 1);
        Assert.Equal(0.0, r.CraCauDegrees, 6);
    }

    [Fact]
    public void Compute_superior_tilt_gives_cra()
    {
        // Normal tilted 20° cranially (positive Y).
        double tilt = 20.0 * Math.PI / 180.0;
        var plane = new AnnulusPlane { Center = default, Normal = new Vector3D(0, Math.Sin(tilt), Math.Cos(tilt)) };
        CarmAngulationResult r = CarmAngulationService.Compute(plane);

        Assert.Equal(0.0, r.LaoRaoDegrees, 6);
        Assert.Equal(20.0, r.CraCauDegrees, 1);
    }

    [Fact]
    public void Compute_combined_tilt_gives_both_angles()
    {
        double lao = 25.0 * Math.PI / 180.0;
        double cra = 15.0 * Math.PI / 180.0;
        // Normal with lateral and cranial components.
        var normal = new Vector3D(Math.Sin(lao), Math.Sin(cra), Math.Cos(lao) * Math.Cos(cra)).Normalize();
        var plane = new AnnulusPlane { Center = default, Normal = normal };
        CarmAngulationResult r = CarmAngulationService.Compute(plane);

        Assert.True(Math.Abs(r.LaoRaoDegrees - 25.0) < 2.0, $"LAO/RAO {r.LaoRaoDegrees:F2}° off.");
        Assert.True(Math.Abs(r.CraCauDegrees - 15.0) < 2.0, $"CRA/CAU {r.CraCauDegrees:F2}° off.");
    }
}
