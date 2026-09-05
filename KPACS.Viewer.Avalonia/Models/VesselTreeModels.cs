using KPACS.Viewer.Models;

namespace KPACS.Viewer.Models;

/// <summary>
/// A named vessel branch: a computed centerline plus its place in the vessel tree. The parent
/// link and <see cref="BifurcationPatientPoint"/> describe where this branch leaves its parent.
/// </summary>
public sealed record VesselSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable anatomical key, e.g. "aorta", "iliac_common_left".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    public CenterlinePath Path { get; init; } = new();

    /// <summary>Label of the parent segment, or null for the root (e.g. the aorta).</summary>
    public string? ParentLabel { get; init; }

    /// <summary>Where this branch leaves the parent, when a parent is set.</summary>
    public Vector3D? BifurcationPatientPoint { get; init; }
}

/// <summary>
/// A named multi-segment arterial tree used by the vascular workspace (aorta + iliac + renals …).
/// </summary>
public sealed record VesselTree
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public List<VesselSegment> Segments { get; init; } = [];

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public VesselSegment? FindByLabel(string label) =>
        Segments.FirstOrDefault(s => string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A branch ostium (e.g. a renal artery origin) expressed relative to a reference centerline:
/// its clock position on the cross-section and its distance from a proximal reference station.
/// </summary>
public sealed record OstiumLandmark
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Anatomical key, e.g. "renal_left".</summary>
    public string Label { get; init; } = string.Empty;

    public Vector3D PatientPoint { get; init; }

    /// <summary>Nearest station on the reference centerline (mm from the proximal origin).</summary>
    public double StationMm { get; init; }

    /// <summary>Distance (mm) along the reference centerline from the proximal reference station.</summary>
    public double DistanceFromReferenceMm { get; init; }

    /// <summary>Clock position on the cross-section, 0–12 h (12 h = the frame's normal direction).</summary>
    public double ClockHours { get; init; }
}
