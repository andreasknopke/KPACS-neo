// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - RoiDraft/AutoOutlineMath.cs
//
// The internal static grid→contour seam for the ROI auto-outline pipeline. Pure functions
// only: a bool[,] / voxel grid in, contours out. No Avalonia, no Dispatcher, no instance
// state. This is the finest-grained test surface for the outline engine (see CONTEXT.md).
//
// NOTE: VolumeRoiInterpolationHelper (Models) and SegmentationMaskVolumeRoiConverter (Services)
// still carry their own private copies of the marching-squares core. Consolidating them onto
// this seam is a tracked follow-up (candidate 3, the mask→ROI pipeline).
// ------------------------------------------------------------------------------------------------

using Avalonia;

namespace KPACS.Viewer.RoiDraft;

/// <summary>
/// Pure grid→contour math shared by the ROI draft pipeline. Internal to the viewer;
/// exposed to the unit-test assembly via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class AutoOutlineMath
{
    /// <summary>A vertex on the doubled marching-squares lattice (odd/even coordinates).</summary>
    internal readonly record struct ContourVertex(int X, int Y);

    /// <summary>An undirected edge between two <see cref="ContourVertex"/> values, order-normalised.</summary>
    internal readonly record struct ContourEdge
    {
        public ContourEdge(ContourVertex first, ContourVertex second)
        {
            if (first.X < second.X || (first.X == second.X && first.Y <= second.Y))
            {
                Start = first;
                End = second;
            }
            else
            {
                Start = second;
                End = first;
            }
        }

        public ContourVertex Start { get; }

        public ContourVertex End { get; }
    }

    /// <summary>
    /// Traces the dominant closed contour of a boolean mask, resampled to at most
    /// <paramref name="maxPointCount"/> points in counter-clockwise image space.
    /// </summary>
    internal static Point[] TraceBoundary(bool[,] mask, int maxPointCount)
    {
        List<(ContourVertex Start, ContourVertex End)> segments = BuildMarchingSquaresSegments(mask);
        if (segments.Count == 0)
        {
            return [];
        }

        List<ContourVertex[]> loops = BuildContourLoops(segments);
        if (loops.Count == 0)
        {
            return [];
        }

        Point[] dominantLoop = loops
            .Select(ConvertContourLoopToPoints)
            .Where(points => points.Length >= 3)
            .OrderByDescending(points => Math.Abs(ComputeSignedPolygonArea(points)))
            .FirstOrDefault([]);

        if (dominantLoop.Length < 3)
        {
            return [];
        }

        return ResampleContour(dominantLoop, maxPointCount);
    }

    internal static List<(ContourVertex Start, ContourVertex End)> BuildMarchingSquaresSegments(bool[,] mask)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] padded = new bool[width + 2, height + 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                padded[x + 1, y + 1] = mask[x, y];
            }
        }

        List<(ContourVertex Start, ContourVertex End)> segments = [];
        for (int y = 0; y <= height; y++)
        {
            for (int x = 0; x <= width; x++)
            {
                bool topLeft = padded[x, y];
                bool topRight = padded[x + 1, y];
                bool bottomRight = padded[x + 1, y + 1];
                bool bottomLeft = padded[x, y + 1];
                int caseIndex = (topLeft ? 8 : 0) | (topRight ? 4 : 0) | (bottomRight ? 2 : 0) | (bottomLeft ? 1 : 0);
                if (caseIndex == 0 || caseIndex == 15)
                {
                    continue;
                }

                ContourVertex left = new(2 * x, (2 * y) + 1);
                ContourVertex top = new((2 * x) + 1, 2 * y);
                ContourVertex right = new((2 * x) + 2, (2 * y) + 1);
                ContourVertex bottom = new((2 * x) + 1, (2 * y) + 2);

                switch (caseIndex)
                {
                    case 1:
                    case 14:
                        segments.Add((left, bottom));
                        break;
                    case 2:
                    case 13:
                        segments.Add((bottom, right));
                        break;
                    case 3:
                    case 12:
                        segments.Add((left, right));
                        break;
                    case 4:
                    case 11:
                        segments.Add((top, right));
                        break;
                    case 5:
                        segments.Add((top, left));
                        segments.Add((bottom, right));
                        break;
                    case 6:
                    case 9:
                        segments.Add((top, bottom));
                        break;
                    case 7:
                    case 8:
                        segments.Add((top, left));
                        break;
                    case 10:
                        segments.Add((top, right));
                        segments.Add((left, bottom));
                        break;
                }
            }
        }

        return segments;
    }

    internal static List<ContourVertex[]> BuildContourLoops(List<(ContourVertex Start, ContourVertex End)> segments)
    {
        Dictionary<ContourVertex, List<ContourVertex>> adjacency = [];
        foreach ((ContourVertex start, ContourVertex end) in segments)
        {
            if (!adjacency.TryGetValue(start, out List<ContourVertex>? startNeighbors))
            {
                startNeighbors = [];
                adjacency[start] = startNeighbors;
            }

            if (!adjacency.TryGetValue(end, out List<ContourVertex>? endNeighbors))
            {
                endNeighbors = [];
                adjacency[end] = endNeighbors;
            }

            startNeighbors.Add(end);
            endNeighbors.Add(start);
        }

        HashSet<ContourEdge> usedEdges = [];
        List<ContourVertex[]> loops = [];
        foreach ((ContourVertex start, ContourVertex end) in segments)
        {
            ContourEdge firstEdge = new(start, end);
            if (usedEdges.Contains(firstEdge))
            {
                continue;
            }

            ContourVertex[] loop = TraceContourLoop(start, end, adjacency, usedEdges);
            if (loop.Length >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }

    internal static ContourVertex[] TraceContourLoop(
        ContourVertex start,
        ContourVertex next,
        Dictionary<ContourVertex, List<ContourVertex>> adjacency,
        HashSet<ContourEdge> usedEdges)
    {
        List<ContourVertex> loop = [start];
        ContourVertex previous = start;
        ContourVertex current = next;
        usedEdges.Add(new ContourEdge(start, next));

        int guard = 0;
        while (guard++ < 200000)
        {
            loop.Add(current);
            if (current.Equals(start))
            {
                break;
            }

            if (!adjacency.TryGetValue(current, out List<ContourVertex>? neighbors) || neighbors.Count == 0)
            {
                return [];
            }

            ContourVertex? candidate = null;
            double bestTurnScore = double.MinValue;
            foreach (ContourVertex neighbor in neighbors)
            {
                if (neighbor.Equals(previous))
                {
                    continue;
                }

                ContourEdge edge = new(current, neighbor);
                if (usedEdges.Contains(edge) && !neighbor.Equals(start))
                {
                    continue;
                }

                double turnScore = ComputeContourTurnScore(previous, current, neighbor);
                if (turnScore > bestTurnScore)
                {
                    bestTurnScore = turnScore;
                    candidate = neighbor;
                }
            }

            if (candidate is null)
            {
                return [];
            }

            usedEdges.Add(new ContourEdge(current, candidate.Value));
            previous = current;
            current = candidate.Value;
        }

        if (loop.Count > 1 && loop[^1].Equals(loop[0]))
        {
            loop.RemoveAt(loop.Count - 1);
        }

        return loop.Count >= 3 ? [.. loop] : [];
    }

    internal static double ComputeContourTurnScore(ContourVertex previous, ContourVertex current, ContourVertex next)
    {
        int inX = current.X - previous.X;
        int inY = current.Y - previous.Y;
        int outX = next.X - current.X;
        int outY = next.Y - current.Y;
        int cross = (inX * outY) - (inY * outX);
        int dot = (inX * outX) + (inY * outY);
        return Math.Atan2(cross, dot);
    }

    internal static Point[] ConvertContourLoopToPoints(ContourVertex[] vertices)
    {
        Point[] points = new Point[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
        {
            points[index] = new Point((vertices[index].X / 2.0) - 1.0, (vertices[index].Y / 2.0) - 1.0);
        }

        return points;
    }

    internal static double ComputeSignedPolygonArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point current = points[index];
            Point next = points[(index + 1) % points.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area * 0.5;
    }

    internal static Point[] ResampleContour(Point[] points, int maxPointCount)
    {
        if (points.Length < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetPointDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        int targetCount = Math.Clamp(maxPointCount, 12, Math.Max(12, points.Length));
        if (points.Length <= targetCount)
        {
            return EnsureCounterClockwise(points);
        }

        Point[] result = new Point[targetCount];
        double step = totalLength / targetCount;
        int segmentIndex = 0;
        for (int sampleIndex = 0; sampleIndex < targetCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double t = (target - segmentStart) / segmentLength;
            Point start = points[segmentIndex];
            Point end = points[(segmentIndex + 1) % points.Length];
            result[sampleIndex] = new Point(start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t));
        }

        return EnsureCounterClockwise(result);
    }

    internal static Point[] EnsureCounterClockwise(Point[] points)
    {
        if (ComputeSignedPolygonArea(points) < 0)
        {
            Array.Reverse(points);
        }

        return points;
    }

    internal static double GetPointDistance(Point first, Point second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Decodes a flat voxel index (z·sizeY·sizeX + y·sizeX + x) back to (x, y, z).</summary>
    internal static void DecodeVoxelKey(int key, int sizeX, int sizeY, out int x, out int y, out int z)
    {
        int sliceSize = sizeX * sizeY;
        z = key / sliceSize;
        int withinSlice = key % sliceSize;
        y = withinSlice / sizeX;
        x = withinSlice % sizeX;
    }
}
