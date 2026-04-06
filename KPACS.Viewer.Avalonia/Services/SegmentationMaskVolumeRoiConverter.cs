using System.Runtime.CompilerServices;
using Avalonia;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public static class SegmentationMaskVolumeRoiConverter
{
    private const int MinimumComponentPixels = 18;
    private const int DefaultContourPointBudget = 24;
    private static readonly ConditionalWeakTable<SegmentationMask3D, SeriesVolume> s_maskVolumeCache = new();

    public static bool TryCreateVolumeContours(
        SegmentationMask3D mask,
        SeriesVolume volume,
        out VolumeRoiContour[] contours,
        int contourPointBudget = DefaultContourPointBudget)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(volume);

        contours = [];
        if (!IsCompatible(mask, volume))
        {
            return false;
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        List<VolumeRoiContour> result = [];
        List<VolumeSliceComponentState> previousSliceComponents = [];
        int nextComponentId = 0;
        int normalizedPointBudget = Math.Clamp(contourPointBudget, 12, 128);

        for (int sliceIndex = 0; sliceIndex < volume.SizeZ; sliceIndex++)
        {
            if (!TryBuildSliceMask(buffer, sliceIndex, out bool[,] sliceMask, out int setCount) || setCount < MinimumComponentPixels)
            {
                previousSliceComponents.Clear();
                continue;
            }

            AutoOutlineMask[] componentMasks = ExtractConnectedSliceMasks(sliceMask);
            if (componentMasks.Length == 0)
            {
                previousSliceComponents.Clear();
                continue;
            }

            DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(volume, SliceOrientation.Axial, sliceIndex);
            List<VolumeSliceContourCandidate> currentCandidates = [];
            foreach (AutoOutlineMask componentMask in componentMasks.OrderByDescending(candidate => candidate.Count))
            {
                if (componentMask.Count < MinimumComponentPixels)
                {
                    continue;
                }

                Point[] imagePoints = TraceBoundary(componentMask, normalizedPointBudget);
                if (imagePoints.Length < 3)
                {
                    continue;
                }

                currentCandidates.Add(new VolumeSliceContourCandidate(componentMask, imagePoints, ComputeMaskCentroid(componentMask)));
            }

            if (currentCandidates.Count == 0)
            {
                previousSliceComponents.Clear();
                continue;
            }

            HashSet<int> claimedPreviousComponents = [];
            List<VolumeSliceComponentState> currentSliceComponents = [];
            foreach (VolumeSliceContourCandidate candidate in currentCandidates.OrderByDescending(entry => entry.Mask.Count))
            {
                int assignedComponentId = -1;
                double bestScore = 0;

                foreach (VolumeSliceComponentState previous in previousSliceComponents)
                {
                    if (!claimedPreviousComponents.Add(previous.ComponentId))
                    {
                        claimedPreviousComponents.Remove(previous.ComponentId);
                        continue;
                    }

                    int overlap = ComputeMaskOverlap(previous.Mask, candidate.Mask);
                    double overlapScore = overlap <= 0
                        ? 0
                        : overlap / (double)Math.Max(1, Math.Min(previous.Mask.Count, candidate.Mask.Count));
                    double centroidDistance = GetPointDistance(previous.Centroid, candidate.Centroid);
                    double score = overlapScore - (centroidDistance * 0.01);

                    claimedPreviousComponents.Remove(previous.ComponentId);
                    if (score <= bestScore || (overlap <= 0 && centroidDistance > 18))
                    {
                        continue;
                    }

                    assignedComponentId = previous.ComponentId;
                    bestScore = score;
                }

                if (assignedComponentId >= 0)
                {
                    claimedPreviousComponents.Add(assignedComponentId);
                }
                else
                {
                    assignedComponentId = nextComponentId++;
                }

                MeasurementAnchor[] anchors = candidate.ImagePoints
                    .Select(point => new MeasurementAnchor(point, metadata.PatientPointFromPixel(point)))
                    .ToArray();
                result.Add(new VolumeRoiContour(
                    anchors,
                    metadata.FilePath,
                    metadata.SopInstanceUid,
                    metadata.Origin,
                    metadata.RowDirection,
                    metadata.ColumnDirection,
                    metadata.Normal,
                    metadata.Origin.Dot(metadata.Normal),
                    true,
                    metadata.RowSpacing,
                    metadata.ColumnSpacing,
                    assignedComponentId));

                currentSliceComponents.Add(new VolumeSliceComponentState(assignedComponentId, candidate.Mask, candidate.Centroid));
            }

            previousSliceComponents = currentSliceComponents;
        }

        contours = [.. result];
        return contours.Length > 0;
    }

    public static bool TryCreateSliceContours(
        SegmentationMask3D mask,
        SeriesVolume volume,
        SliceOrientation orientation,
        int sliceIndex,
        out Point[][] contours,
        int contourPointBudget = DefaultContourPointBudget)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(volume);

        contours = [];
        if (!IsCompatible(mask, volume))
        {
            return false;
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        int normalizedPointBudget = Math.Clamp(contourPointBudget, 12, 128);
        if (!TryBuildSliceMask(mask, buffer, volume, orientation, sliceIndex, out bool[,] sliceMask, out int setCount) ||
            setCount < MinimumComponentPixels)
        {
            return false;
        }

        AutoOutlineMask[] componentMasks = ExtractConnectedSliceMasks(sliceMask);
        if (componentMasks.Length == 0)
        {
            return false;
        }

        List<Point[]> result = [];
        foreach (AutoOutlineMask componentMask in componentMasks.OrderByDescending(candidate => candidate.Count))
        {
            if (componentMask.Count < MinimumComponentPixels)
            {
                continue;
            }

            Point[] imagePoints = TraceBoundary(componentMask, normalizedPointBudget);
            if (imagePoints.Length < 3)
            {
                continue;
            }

            result.Add(imagePoints);
        }

        contours = [.. result];
        return contours.Length > 0;
    }

    private static bool IsCompatible(SegmentationMask3D mask, SeriesVolume volume)
    {
        VolumeGridGeometry geometry = mask.Geometry;
        return geometry.SizeX == volume.SizeX
            && geometry.SizeY == volume.SizeY
            && geometry.SizeZ == volume.SizeZ
            && string.Equals(geometry.FrameOfReferenceUid, volume.FrameOfReferenceUid, StringComparison.Ordinal);
    }

    private static bool TryBuildSliceMask(SegmentationMaskBuffer buffer, int sliceIndex, out bool[,] mask, out int setCount)
    {
        mask = new bool[1, 1];
        setCount = 0;
        if (sliceIndex < 0 || sliceIndex >= buffer.SizeZ)
        {
            return false;
        }

        bool[] slice = buffer.ExtractAxialSlice(sliceIndex);
        if (slice.Length == 0)
        {
            return false;
        }

        mask = new bool[buffer.SizeX, buffer.SizeY];
        int offset = 0;
        for (int y = 0; y < buffer.SizeY; y++)
        {
            for (int x = 0; x < buffer.SizeX; x++, offset++)
            {
                bool value = slice[offset];
                mask[x, y] = value;
                if (value)
                {
                    setCount++;
                }
            }
        }

        return setCount >= MinimumComponentPixels;
    }

    private static bool TryBuildSliceMask(
        SegmentationMask3D sourceMask,
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        SliceOrientation orientation,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        mask = new bool[1, 1];
        setCount = 0;

        return orientation switch
        {
            SliceOrientation.Axial => TryBuildSliceMask(buffer, sliceIndex, out mask, out setCount),
            SliceOrientation.Coronal => TryBuildCoronalSliceMask(sourceMask, buffer, volume, sliceIndex, out mask, out setCount),
            SliceOrientation.Sagittal => TryBuildSagittalSliceMask(sourceMask, buffer, volume, sliceIndex, out mask, out setCount),
            _ => false,
        };
    }

    private static bool TryBuildCoronalSliceMask(
        SegmentationMask3D sourceMask,
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        return TryBuildOrthogonalSliceMask(sourceMask, buffer, volume, SliceOrientation.Coronal, sliceIndex, out mask, out setCount);
    }

    private static bool TryBuildSagittalSliceMask(
        SegmentationMask3D sourceMask,
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        return TryBuildOrthogonalSliceMask(sourceMask, buffer, volume, SliceOrientation.Sagittal, sliceIndex, out mask, out setCount);
    }

    private static bool TryBuildOrthogonalSliceMask(
        SegmentationMask3D sourceMask,
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        SliceOrientation orientation,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        mask = new bool[1, 1];
        setCount = 0;

        int maxSliceIndex = orientation switch
        {
            SliceOrientation.Coronal => buffer.SizeY - 1,
            SliceOrientation.Sagittal => buffer.SizeX - 1,
            _ => -1,
        };
        if (sliceIndex < 0 || sliceIndex > maxSliceIndex)
        {
            return false;
        }

        try
        {
            SeriesVolume maskVolume = GetOrCreateMaskVolume(sourceMask, volume);
            ReslicedImage slice = VolumeReslicer.ExtractSlice(maskVolume, orientation, sliceIndex);
            if (slice.Width <= 0 || slice.Height <= 0 || slice.Pixels.Length != slice.Width * slice.Height)
            {
                return false;
            }

            mask = new bool[slice.Width, slice.Height];
            for (int row = 0; row < slice.Height; row++)
            {
                int rowOffset = row * slice.Width;
                for (int column = 0; column < slice.Width; column++)
                {
                    bool value = slice.Pixels[rowOffset + column] > 0;
                    mask[column, row] = value;
                    if (value)
                    {
                        setCount++;
                    }
                }
            }

            return setCount >= MinimumComponentPixels;
        }
        catch
        {
            return orientation switch
            {
                SliceOrientation.Coronal => TryBuildCoronalSliceMaskCpu(buffer, volume, sliceIndex, out mask, out setCount),
                SliceOrientation.Sagittal => TryBuildSagittalSliceMaskCpu(buffer, volume, sliceIndex, out mask, out setCount),
                _ => false,
            };
        }
    }

    private static bool TryBuildCoronalSliceMaskCpu(
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        mask = new bool[1, 1];
        setCount = 0;
        if (sliceIndex < 0 || sliceIndex >= buffer.SizeY)
        {
            return false;
        }

        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(buffer.SizeZ, volume.SpacingZ, targetSpacingY);
        mask = new bool[buffer.SizeX, height];
        for (int row = 0; row < height; row++)
        {
            int sourceZ = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, buffer.SizeZ)), 0, buffer.SizeZ - 1);
            for (int x = 0; x < buffer.SizeX; x++)
            {
                bool value = buffer.Get(x, sliceIndex, sourceZ);
                mask[x, row] = value;
                if (value)
                {
                    setCount++;
                }
            }
        }

        return setCount >= MinimumComponentPixels;
    }

    private static bool TryBuildSagittalSliceMaskCpu(
        SegmentationMaskBuffer buffer,
        SeriesVolume volume,
        int sliceIndex,
        out bool[,] mask,
        out int setCount)
    {
        mask = new bool[1, 1];
        setCount = 0;
        if (sliceIndex < 0 || sliceIndex >= buffer.SizeX)
        {
            return false;
        }

        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(buffer.SizeZ, volume.SpacingZ, targetSpacingY);
        mask = new bool[buffer.SizeY, height];
        for (int row = 0; row < height; row++)
        {
            int sourceZ = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, buffer.SizeZ)), 0, buffer.SizeZ - 1);
            for (int y = 0; y < buffer.SizeY; y++)
            {
                bool value = buffer.Get(sliceIndex, y, sourceZ);
                mask[y, row] = value;
                if (value)
                {
                    setCount++;
                }
            }
        }

        return setCount >= MinimumComponentPixels;
    }

    private static SeriesVolume GetOrCreateMaskVolume(SegmentationMask3D mask, SeriesVolume sourceVolume)
    {
        return s_maskVolumeCache.GetValue(mask, _ => CreateMaskVolume(SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage), sourceVolume));
    }

    private static SeriesVolume CreateMaskVolume(SegmentationMaskBuffer buffer, SeriesVolume sourceVolume)
    {
        int voxelCount = checked(buffer.SizeX * buffer.SizeY * buffer.SizeZ);
        short[] voxels = new short[voxelCount];
        int sliceSize = buffer.SizeX * buffer.SizeY;
        Parallel.For(0, buffer.SizeZ, z =>
        {
            bool[] axialSlice = buffer.ExtractAxialSlice(z);
            int offset = z * sliceSize;
            for (int index = 0; index < axialSlice.Length; index++)
            {
                voxels[offset + index] = axialSlice[index] ? (short)1 : (short)0;
            }
        });

        return new SeriesVolume(
            voxels,
            buffer.SizeX,
            buffer.SizeY,
            buffer.SizeZ,
            sourceVolume.SpacingX,
            sourceVolume.SpacingY,
            sourceVolume.SpacingZ,
            sourceVolume.Origin,
            sourceVolume.RowDirection,
            sourceVolume.ColumnDirection,
            sourceVolume.Normal,
            0.5,
            1.0,
            0,
            1,
            false,
            sourceVolume.SeriesInstanceUid,
            sourceVolume.FrameOfReferenceUid,
            sourceVolume.AcquisitionNumber,
            sourceVolume.SliceFilePaths,
            sourceVolume.SliceSopInstanceUids);
    }

    private static int GetResampledDepth(int sliceCount, double sliceSpacing, double targetSpacing)
    {
        if (sliceCount <= 1)
        {
            return Math.Max(1, sliceCount);
        }

        double safeSliceSpacing = sliceSpacing > 0 ? sliceSpacing : 1.0;
        double safeTargetSpacing = targetSpacing > 0 ? targetSpacing : safeSliceSpacing;
        double physicalDepth = (sliceCount - 1) * safeSliceSpacing;
        return Math.Max(1, (int)Math.Round(physicalDepth / safeTargetSpacing) + 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return Math.Max(0, sourceDepth - 1);
        }

        double normalized = row / (double)(outputHeight - 1);
        return (sourceDepth - 1) * (1.0 - normalized);
    }

    private static Point[] TraceBoundary(AutoOutlineMask mask, int maxPointCount)
    {
        List<(ContourVertex Start, ContourVertex End)> segments = BuildMarchingSquaresSegments(mask.Pixels);
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

        Point[] resampled = ResampleContour(dominantLoop, maxPointCount);
        for (int index = 0; index < resampled.Length; index++)
        {
            resampled[index] = new Point(resampled[index].X + mask.Left, resampled[index].Y + mask.Top);
        }

        return resampled;
    }

    private static List<(ContourVertex Start, ContourVertex End)> BuildMarchingSquaresSegments(bool[,] mask)
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

    private static List<ContourVertex[]> BuildContourLoops(List<(ContourVertex Start, ContourVertex End)> segments)
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

    private static ContourVertex[] TraceContourLoop(
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

    private static double ComputeContourTurnScore(ContourVertex previous, ContourVertex current, ContourVertex next)
    {
        int inX = current.X - previous.X;
        int inY = current.Y - previous.Y;
        int outX = next.X - current.X;
        int outY = next.Y - current.Y;
        int cross = (inX * outY) - (inY * outX);
        int dot = (inX * outX) + (inY * outY);
        return Math.Atan2(cross, dot);
    }

    private static Point[] ConvertContourLoopToPoints(ContourVertex[] vertices)
    {
        Point[] points = new Point[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
        {
            points[index] = new Point((vertices[index].X / 2.0) - 1.0, (vertices[index].Y / 2.0) - 1.0);
        }

        return points;
    }

    private static double ComputeSignedPolygonArea(IReadOnlyList<Point> points)
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

    private static Point[] ResampleContour(Point[] points, int maxPointCount)
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

    private static Point[] EnsureCounterClockwise(Point[] points)
    {
        if (ComputeSignedPolygonArea(points) < 0)
        {
            Array.Reverse(points);
        }

        return points;
    }

    private static AutoOutlineMask[] ExtractConnectedSliceMasks(bool[,] mask)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] visited = new bool[width, height];
        List<AutoOutlineMask> components = [];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[x, y] || visited[x, y])
                {
                    continue;
                }

                Queue<(int X, int Y)> queue = new();
                List<(int X, int Y)> pixels = [];
                int minX = x;
                int maxX = x;
                int minY = y;
                int maxY = y;

                visited[x, y] = true;
                queue.Enqueue((x, y));

                while (queue.Count > 0)
                {
                    (int currentX, int currentY) = queue.Dequeue();
                    pixels.Add((currentX, currentY));
                    minX = Math.Min(minX, currentX);
                    maxX = Math.Max(maxX, currentX);
                    minY = Math.Min(minY, currentY);
                    maxY = Math.Max(maxY, currentY);

                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            int nextX = currentX + offsetX;
                            int nextY = currentY + offsetY;
                            if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY] || !mask[nextX, nextY])
                            {
                                continue;
                            }

                            visited[nextX, nextY] = true;
                            queue.Enqueue((nextX, nextY));
                        }
                    }
                }

                bool[,] componentPixels = new bool[maxX - minX + 1, maxY - minY + 1];
                foreach ((int pixelX, int pixelY) in pixels)
                {
                    componentPixels[pixelX - minX, pixelY - minY] = true;
                }

                components.Add(new AutoOutlineMask(minX, minY, componentPixels, pixels.Count));
            }
        }

        return [.. components];
    }

    private static Point ComputeMaskCentroid(AutoOutlineMask mask)
    {
        double sumX = 0;
        double sumY = 0;
        int count = 0;
        for (int y = 0; y < mask.Pixels.GetLength(1); y++)
        {
            for (int x = 0; x < mask.Pixels.GetLength(0); x++)
            {
                if (!mask.Pixels[x, y])
                {
                    continue;
                }

                sumX += x + mask.Left;
                sumY += y + mask.Top;
                count++;
            }
        }

        return count == 0
            ? new Point(mask.Left, mask.Top)
            : new Point(sumX / count, sumY / count);
    }

    private static int ComputeMaskOverlap(AutoOutlineMask first, AutoOutlineMask second)
    {
        int left = Math.Max(first.Left, second.Left);
        int top = Math.Max(first.Top, second.Top);
        int right = Math.Min(first.Left + first.Pixels.GetLength(0) - 1, second.Left + second.Pixels.GetLength(0) - 1);
        int bottom = Math.Min(first.Top + first.Pixels.GetLength(1) - 1, second.Top + second.Pixels.GetLength(1) - 1);
        if (right < left || bottom < top)
        {
            return 0;
        }

        int overlap = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (first.Pixels[x - first.Left, y - first.Top] && second.Pixels[x - second.Left, y - second.Top])
                {
                    overlap++;
                }
            }
        }

        return overlap;
    }

    private static double GetPointDistance(Point first, Point second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct AutoOutlineMask(int Left, int Top, bool[,] Pixels, int Count);
    private readonly record struct ContourVertex(int X, int Y);
    private readonly record struct VolumeSliceContourCandidate(AutoOutlineMask Mask, Point[] ImagePoints, Point Centroid);
    private readonly record struct VolumeSliceComponentState(int ComponentId, AutoOutlineMask Mask, Point Centroid);

    private readonly record struct ContourEdge
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
}
