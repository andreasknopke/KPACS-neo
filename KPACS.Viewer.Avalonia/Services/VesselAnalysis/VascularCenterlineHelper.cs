using System.Globalization;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Pure, deterministic helpers for the EVAR centerline step (Phase B3): the named
/// vessel presets offered by the "Add segment" control, the modifier → seed-kind
/// resolution shared with the study viewer, a radius → colour mapping for the
/// radius-coded tube overlay, and status-card summaries. No Avalonia, no I/O —
/// fully unit-testable.
/// </summary>
internal static class VascularCenterlineHelper
{
    /// <summary>
    /// A named anatomical segment preset. <see cref="Label"/> is the stable key used in
    /// the <see cref="VesselTree"/>; <see cref="ParentLabel"/> is the tree parent (null for
    /// the root aorta) so a freshly computed branch can be attached at its bifurcation.
    /// </summary>
    public readonly record struct VesselPreset(string Label, string DisplayName, string? ParentLabel);

    /// <summary>
    /// The EVAR target tree presets: the aorta (root) plus the common iliac and renal
    /// arteries, each parented to the aorta. Order is the order shown in the preset picker.
    /// </summary>
    public static readonly VesselPreset[] Presets =
    [
        new("aorta", "Aorta", null),
        new("iliac_common_left", "Iliaca communis links", "aorta"),
        new("iliac_common_right", "Iliaca communis rechts", "aorta"),
        new("renal_left", "Nierenarterie links", "aorta"),
        new("renal_right", "Nierenarterie rechts", "aorta"),
    ];

    /// <summary>
    /// Resolve which seed kind a click represents. Mirrors the study-viewer schema:
    /// CTRL → guide, ALT → end, SHIFT → start; otherwise auto-assign the first missing
    /// endpoint (start, then end) and fall back to guide once both endpoints exist.
    /// </summary>
    public static CenterlineSeedKind ResolveSeedKind(
        CenterlineSeedSet seedSet,
        bool shift,
        bool alt,
        bool ctrl)
    {
        ArgumentNullException.ThrowIfNull(seedSet);

        if (ctrl)
        {
            return CenterlineSeedKind.Guide;
        }

        if (alt)
        {
            return CenterlineSeedKind.End;
        }

        if (shift)
        {
            return CenterlineSeedKind.Start;
        }

        if (seedSet.StartSeed is null)
        {
            return CenterlineSeedKind.Start;
        }

        return seedSet.EndSeed is null
            ? CenterlineSeedKind.End
            : CenterlineSeedKind.Guide;
    }

    /// <summary>
    /// Map a max-inscribed-sphere radius (mm) to an RGB tube colour for the radius-coded
    /// DVR/overlay. Small vessels trend red, medium green, large blue — a monotone ramp
    /// over the EVAR-relevant range (clamped at 1.5–15 mm). Deterministic and pure.
    /// </summary>
    public static (byte R, byte G, byte B) RadiusToColor(double radiusMm)
    {
        const double minRadius = 1.5;
        const double maxRadius = 15.0;
        double t = Math.Clamp((radiusMm - minRadius) / (maxRadius - minRadius), 0.0, 1.0);

        // Red → green → blue ramp: hue rises with radius.
        byte r = (byte)Math.Round(255 * Math.Clamp(1.0 - 2.0 * t, 0.0, 1.0));
        byte g = (byte)Math.Round(255 * Math.Clamp(1.0 - Math.Abs(2.0 * t - 1.0), 0.0, 1.0));
        byte b = (byte)Math.Round(255 * Math.Clamp(2.0 * t - 1.0, 0.0, 1.0));
        return (r, g, b);
    }

    /// <summary>
    /// Mean radius (mm) over a computed path's per-point radii, or null when the path
    /// carries no radius data (legacy / preview paths).
    /// </summary>
    public static double? MeanRadiusMm(CenterlinePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        double[]? radii = path.RadiiMm;
        if (radii is null || radii.Length == 0)
        {
            return null;
        }

        double sum = 0;
        int count = 0;
        foreach (double radius in radii)
        {
            if (radius > 0)
            {
                sum += radius;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>
    /// Build the status-card line for a computed centerline: length, mean diameter,
    /// quality score and a coverage hint. Pure string formatting over the path record.
    /// </summary>
    public static string Summarize(CenterlinePath path, string segmentName)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.HasRenderablePath)
        {
            return $"{segmentName}: noch keine Centerline.";
        }

        double? meanRadius = MeanRadiusMm(path);
        string diameter = meanRadius is double r
            ? $" · Ø {(2 * r).ToString("F1", CultureInfo.InvariantCulture)} mm"
            : string.Empty;

        string quality = path.QualityScore switch
        {
            >= 0.75 => "",
            >= 0.5 => " · Qualität mäßig — ggf. Guide-Seed setzen",
            _ => " · Qualität niedrig — Seed-Paar prüfen",
        };

        return $"{segmentName}: {path.TotalLengthMm.ToString("F0", CultureInfo.InvariantCulture)} mm{diameter} · q={path.QualityScore.ToString("F2", CultureInfo.InvariantCulture)}{quality}";
    }
}
