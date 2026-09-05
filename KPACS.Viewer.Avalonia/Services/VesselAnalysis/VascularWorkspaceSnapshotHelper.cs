using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Phase E: pure, unit-testable helpers for building and validating a
/// <see cref="VascularWorkspaceSnapshot"/> from the workspace's in-memory state.
/// Kept free of Avalonia and UI concerns so the snapshot round-trip can be proven
/// with deterministic unit tests.
/// </summary>
internal static class VascularWorkspaceSnapshotHelper
{
    /// <summary>
    /// Builds a snapshot from the given parts. Masks are referenced by id only; the
    /// caller persists the actual mask payloads in the envelope's mask list.
    /// </summary>
    public static VascularWorkspaceSnapshot Build(
        string seriesInstanceUid,
        string frameOfReferenceUid,
        VesselTree? vesselTree,
        EndograftPlan? endograftPlan,
        VascularPlanningBundle? planningBundle,
        Guid? lumenMaskId,
        Guid? calciumMaskId,
        Guid? thrombusMaskId,
        int chartStationIndex,
        string activeSegmentLabel)
    {
        return new VascularWorkspaceSnapshot
        {
            SeriesInstanceUid = seriesInstanceUid ?? string.Empty,
            FrameOfReferenceUid = frameOfReferenceUid ?? string.Empty,
            VesselTree = vesselTree,
            EndograftPlan = endograftPlan,
            PlanningBundle = planningBundle,
            LumenMaskId = lumenMaskId,
            CalciumMaskId = calciumMaskId,
            ThrombusMaskId = thrombusMaskId,
            ChartStationIndex = Math.Max(0, chartStationIndex),
            ActiveSegmentLabel = activeSegmentLabel ?? string.Empty,
        };
    }

    /// <summary>
    /// Validates that a snapshot is consistent with the volume it is being restored
    /// against: the series and frame-of-reference UIDs must match, and the chart
    /// station must be within the reference path's point count.
    /// </summary>
    public static bool IsCompatibleWithVolume(
        VascularWorkspaceSnapshot snapshot,
        string seriesInstanceUid,
        string frameOfReferenceUid,
        int referencePathPointCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.Equals(snapshot.SeriesInstanceUid, seriesInstanceUid, StringComparison.Ordinal) ||
            !string.Equals(snapshot.FrameOfReferenceUid, frameOfReferenceUid, StringComparison.Ordinal))
        {
            return false;
        }

        if (referencePathPointCount > 0 && snapshot.ChartStationIndex >= referencePathPointCount)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the reference path point count from a vessel tree's active segment,
    /// or 0 when no segment is available.
    /// </summary>
    public static int ResolveReferencePathPointCount(VesselTree? vesselTree, string activeSegmentLabel)
    {
        if (vesselTree is null || string.IsNullOrWhiteSpace(activeSegmentLabel))
        {
            return 0;
        }

        VesselSegment? segment = vesselTree.FindByLabel(activeSegmentLabel);
        return segment?.Path?.Points?.Count ?? 0;
    }
}
