using KPACS.Viewer.Models;
using KPACS.Viewer.Services.VesselAnalysis;

namespace KPACS.Viewer;

/// <summary>
/// Phase E: persistence of the vascular workspace state. Builds a
/// <see cref="VascularWorkspaceSnapshot"/> from the workspace's in-memory state and
/// pushes it to the owning <see cref="StudyViewerWindow"/> via <see cref="SnapshotChanged"/>
/// so it can be stored in the measurement-session envelope.
/// </summary>
public partial class VascularWorkspaceWindow
{
    private VascularValidationSnapshot _vascularValidationSnapshot = VascularValidationSnapshot.CreateDefault();

    /// <summary>
    /// Records a performance metric into the workspace's validation snapshot (Phase E4).
    /// The snapshot is kept window-local; it is not persisted in the session envelope
    /// (the StudyViewerWindow owns the persisted snapshot).
    /// </summary>
    private void RecordVascularPerformanceMetric(string key, double elapsedMs)
    {
        if (elapsedMs < 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
        {
            return;
        }

        _vascularValidationSnapshot = _vascularValidationSnapshot.RecordPerformance(key, elapsedMs);
    }

    /// <summary>
    /// Builds the current workspace snapshot from the in-memory state and pushes it to
    /// the host (if any). Called after segmentation, centerline, measurement, sizing, or
    /// chart-station changes.
    /// </summary>
    private void PushWorkspaceSnapshot()
    {
        if (SnapshotChanged is null || _volume is null)
        {
            return;
        }

        SnapshotChanged(BuildCurrentSnapshot());
    }

    /// <summary>
    /// Builds the current workspace snapshot from the in-memory state, independent of
    /// whether a host callback is wired (used by the report step and persistence).
    /// </summary>
    private VascularWorkspaceSnapshot BuildCurrentSnapshot()
    {
        if (_volume is null)
        {
            return new VascularWorkspaceSnapshot();
        }

        string activeLabel = ActiveSegment?.PresetLabel ?? string.Empty;
        return VascularWorkspaceSnapshotHelper.Build(
            _volume.SeriesInstanceUid,
            _volume.FrameOfReferenceUid,
            _vesselTree,
            _endograftPlan,
            _planningBundle,
            _lumenMask?.Id,
            _calciumMask?.Id,
            _thrombusMask?.Id,
            _csStationIndex,
            activeLabel);
    }
}
