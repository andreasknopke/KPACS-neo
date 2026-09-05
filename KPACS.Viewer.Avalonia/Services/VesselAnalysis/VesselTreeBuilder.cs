using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services.VesselAnalysis;

/// <summary>
/// Assembles named vessel segments into a <see cref="VesselTree"/> and locates the bifurcation
/// where a branch leaves its parent, by nearest-point search between the two centerlines.
/// </summary>
internal static class VesselTreeBuilder
{
    /// <summary>
    /// Attach <paramref name="child"/> to the parent segment named by <paramref name="parentLabel"/>
    /// (if present in <paramref name="tree"/>) and record the bifurcation as the closest pair of
    /// points between the two centerlines. Returns a new tree; the input is not mutated.
    /// </summary>
    public static VesselTree AttachBranch(
        VesselTree tree,
        VesselSegment child,
        string? parentLabel)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(child);

        List<VesselSegment> segments = [.. tree.Segments.Where(s => !string.Equals(s.Label, child.Label, StringComparison.OrdinalIgnoreCase))];

        VesselSegment attached = child;
        VesselSegment? parent = parentLabel is null
            ? null
            : segments.FirstOrDefault(s => string.Equals(s.Label, parentLabel, StringComparison.OrdinalIgnoreCase));

        if (parent is not null && parent.Path.HasRenderablePath && child.Path.HasRenderablePath)
        {
            Vector3D bifurcation = FindClosestPointOnParent(parent.Path, child.Path);
            attached = child with
            {
                ParentLabel = parent.Label,
                BifurcationPatientPoint = bifurcation,
            };
        }
        else
        {
            attached = child with { ParentLabel = null, BifurcationPatientPoint = null };
        }

        segments.Add(attached);
        return tree with { Segments = segments, UpdatedUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// The point on <paramref name="parent"/> closest to any point of <paramref name="branch"/>,
    /// i.e. the bifurcation location on the parent centerline.
    /// </summary>
    public static Vector3D FindClosestPointOnParent(CenterlinePath parent, CenterlinePath branch)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(branch);

        if (!parent.HasRenderablePath || !branch.HasRenderablePath)
        {
            throw new ArgumentException("Both centerlines need at least two points.", nameof(branch));
        }

        double bestDistanceSq = double.MaxValue;
        Vector3D best = parent.Points[0].PatientPoint;

        foreach (CenterlinePathPoint branchPoint in branch.Points)
        {
            foreach (CenterlinePathPoint parentPoint in parent.Points)
            {
                Vector3D delta = parentPoint.PatientPoint - branchPoint.PatientPoint;
                double distanceSq = delta.Dot(delta);
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    best = parentPoint.PatientPoint;
                }
            }
        }

        return best;
    }
}
