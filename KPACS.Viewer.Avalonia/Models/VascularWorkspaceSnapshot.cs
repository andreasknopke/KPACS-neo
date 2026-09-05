namespace KPACS.Viewer.Models;

/// <summary>
/// Phase E: a serializable snapshot of the vascular workspace state that is persisted
/// inside the measurement-session envelope. It captures the vessel tree, the endograft
/// plan, the planning bundle (markers + metrics), the active chart station, and the
/// segmentation masks (lumen + calcium/thrombus submasks) so a session can be restored
/// identically after a viewer restart.
///
/// The snapshot deliberately contains no PHI: it stores only geometry, metrics, and
/// stable identifiers. Mask payloads are referenced by <see cref="SegmentationMaskId"/>
/// and persisted separately in the envelope's mask list.
/// </summary>
public sealed record VascularWorkspaceSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Series instance UID the workspace was opened against.</summary>
    public string SeriesInstanceUid { get; init; } = string.Empty;

    /// <summary>Frame of reference UID of the volume (consistency check on restore).</summary>
    public string FrameOfReferenceUid { get; init; } = string.Empty;

    /// <summary>The vessel tree (aorta + iliac + renal segments).</summary>
    public VesselTree? VesselTree { get; init; }

    /// <summary>The endograft plan (components + warnings).</summary>
    public EndograftPlan? EndograftPlan { get; init; }

    /// <summary>The planning bundle (markers + metrics).</summary>
    public VascularPlanningBundle? PlanningBundle { get; init; }

    /// <summary>The TAVI planning bundle (annulus, LVOT, ostia, calcium, C-arm, sizing).</summary>
    public TaviPlanningBundle? TaviPlanning { get; init; }

    /// <summary>Id of the lumen mask in the envelope mask list.</summary>
    public Guid? LumenMaskId { get; init; }

    /// <summary>Id of the calcium submask in the envelope mask list.</summary>
    public Guid? CalciumMaskId { get; init; }

    /// <summary>Id of the thrombus submask in the envelope mask list.</summary>
    public Guid? ThrombusMaskId { get; init; }

    /// <summary>Active chart station index (0-based) along the reference centerline.</summary>
    public int ChartStationIndex { get; init; }

    /// <summary>Active centerline segment label (e.g. "aorta").</summary>
    public string ActiveSegmentLabel { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
