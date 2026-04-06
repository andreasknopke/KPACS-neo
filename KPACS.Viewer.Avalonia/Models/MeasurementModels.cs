using Avalonia;

namespace KPACS.Viewer.Models;

public enum MeasurementKind
{
    Line,
    Angle,
    Annotation,
    RectangleRoi,
    EllipseRoi,
    PolygonRoi,
    VolumeRoi,
}

public sealed record MeasurementAnchor(Point ImagePoint, Vector3D? PatientPoint);

public sealed record VolumeRoiContour(
    MeasurementAnchor[] Anchors,
    string SourceFilePath,
    string ReferencedSopInstanceUid,
    Vector3D PlaneOrigin,
    Vector3D RowDirection,
    Vector3D ColumnDirection,
    Vector3D Normal,
    double PlanePosition,
    bool IsClosed = true,
    double RowSpacing = 1,
    double ColumnSpacing = 1,
    int ComponentId = 0)
{
    public bool TryProjectTo(DicomSpatialMetadata metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (Anchors.Length == 0)
        {
            return false;
        }

        // Quick reject on first anchor — all anchors share the same geometric plane,
        // so testing one is sufficient and avoids iterating the full array.
        double planeTolerance = Math.Max(0.75, Math.Min(metadata.RowSpacing, metadata.ColumnSpacing));
        if (Anchors[0].PatientPoint is not { } firstPatientPoint ||
            metadata.DistanceToPlane(firstPatientPoint) > planeTolerance)
        {
            return false;
        }

        Point[] points = new Point[Anchors.Length];
        for (int i = 0; i < Anchors.Length; i++)
        {
            if (Anchors[i].PatientPoint is null)
            {
                return false;
            }

            points[i] = metadata.PixelPointFromPatient(Anchors[i].PatientPoint!.Value);
        }

        imagePoints = points;
        return true;
    }
}

public sealed record StudyMeasurement(
    Guid Id,
    MeasurementKind Kind,
    string SourceFilePath,
    string ReferencedSopInstanceUid,
    string FrameOfReferenceUid,
    string AcquisitionNumber,
    MeasurementAnchor[] Anchors,
    string AnnotationText = "",
    MeasurementTrackingMetadata? Tracking = null,
    Point? LabelOffset = null,
    VolumeRoiContour[]? VolumeContours = null,
    Guid? SegmentationMaskId = null)
{
    /// <summary>
    /// Multi-entry cache for volume contour projections keyed by quantized plane position.
    /// Eliminates re-computation when scrolling back and forth through the same slices.
    /// Only accessed from the UI thread; does not participate in record equality.
    /// </summary>
    private Dictionary<long, (Point[][] Contours, bool IsInterpolated)>? _volumeContourProjectionCache;

    /// <summary>
    /// Lazily built sorted contour index grouped by component. Enables binary search
    /// for bracketing contour pairs instead of repeated GroupBy + OrderBy per scroll step.
    /// </summary>
    private (int ComponentId, double[] PlanePositions, VolumeRoiContour[] Contours)[]? _sortedContourComponentIndex;

    public static StudyMeasurement Create(
        MeasurementKind kind,
        string sourceFilePath,
        DicomSpatialMetadata? metadata,
        IReadOnlyList<Point> imagePoints,
        string annotationText = "")
    {
        ArgumentNullException.ThrowIfNull(imagePoints);

        return new StudyMeasurement(
            Guid.NewGuid(),
            kind,
            sourceFilePath,
            metadata?.SopInstanceUid ?? string.Empty,
            metadata?.FrameOfReferenceUid ?? string.Empty,
            metadata?.AcquisitionNumber ?? string.Empty,
            imagePoints.Select(point => new MeasurementAnchor(
                point,
                metadata?.PatientPointFromPixel(point))).ToArray(),
            annotationText,
            LabelOffset: new Point(10, 10));
    }

    public StudyMeasurement WithAnchors(DicomSpatialMetadata? metadata, IReadOnlyList<Point> imagePoints) =>
        this with
        {
            Anchors = imagePoints
                .Select(point => new MeasurementAnchor(point, metadata?.PatientPointFromPixel(point)))
                .ToArray(),
        };

    public StudyMeasurement WithAnnotationText(string annotationText) =>
        this with { AnnotationText = annotationText ?? string.Empty };

    public StudyMeasurement WithLabelOffset(Point labelOffset) =>
        this with { LabelOffset = labelOffset };

    public StudyMeasurement WithTracking(MeasurementTrackingMetadata? tracking) =>
        this with { Tracking = tracking };

    public StudyMeasurement WithVolumeContours(IEnumerable<VolumeRoiContour> volumeContours) =>
        this with
        {
            VolumeContours = volumeContours.ToArray(),
            Anchors = volumeContours.SelectMany(contour => contour.Anchors).ToArray(),
        };

    public StudyMeasurement WithSegmentationMask(Guid? segmentationMaskId) =>
        this with { SegmentationMaskId = segmentationMaskId };

    public static StudyMeasurement CreateVolumeRoi(
        string sourceFilePath,
        DicomSpatialMetadata? metadata,
        IEnumerable<VolumeRoiContour> volumeContours,
        Guid? segmentationMaskId = null)
    {
        VolumeRoiContour[] contours = volumeContours.ToArray();
        VolumeRoiContour? firstContour = contours.FirstOrDefault();

        return new StudyMeasurement(
            Guid.NewGuid(),
            MeasurementKind.VolumeRoi,
            firstContour?.SourceFilePath ?? sourceFilePath,
            firstContour?.ReferencedSopInstanceUid ?? metadata?.SopInstanceUid ?? string.Empty,
            metadata?.FrameOfReferenceUid ?? string.Empty,
            metadata?.AcquisitionNumber ?? string.Empty,
            contours.SelectMany(contour => contour.Anchors).ToArray(),
            LabelOffset: new Point(10, 10),
                VolumeContours: contours,
                SegmentationMaskId: segmentationMaskId);
    }

    public bool TryGetPatientCenter(out Vector3D center)
    {
        Vector3D[] patientPoints = Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();

        if (patientPoints.Length == 0)
        {
            center = default;
            return false;
        }

        Vector3D sum = default;
        foreach (Vector3D point in patientPoints)
        {
            sum += point;
        }

        center = sum / patientPoints.Length;
        return true;
    }

    public bool TryProjectTo(DicomSpatialMetadata? metadata, out Point[] imagePoints)
    {
        imagePoints = [];

        if (Kind == MeasurementKind.VolumeRoi)
        {
            if (TryProjectVolumeContoursTo(metadata, out Point[][] volumeContours) && volumeContours.Length > 0)
            {
                imagePoints = volumeContours[0];
                return true;
            }

            return false;
        }

        if (Anchors.Length == 0)
        {
            return false;
        }

        if (metadata is not null &&
            Anchors.All(anchor => anchor.PatientPoint is not null) &&
            IsCompatibleWith(metadata))
        {
            double planeTolerance = Math.Max(0.75, Math.Min(metadata.RowSpacing, metadata.ColumnSpacing));
            if (Anchors.Any(anchor => metadata.DistanceToPlane(anchor.PatientPoint!.Value) > planeTolerance))
            {
                return false;
            }

            imagePoints = Anchors
                .Select(anchor => metadata.PixelPointFromPatient(anchor.PatientPoint!.Value))
                .ToArray();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(SourceFilePath) &&
            metadata is not null &&
            string.Equals(SourceFilePath, metadata.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            imagePoints = Anchors.Select(anchor => anchor.ImagePoint).ToArray();
            return true;
        }

        return false;
    }

    public bool TryProjectVolumeContoursTo(DicomSpatialMetadata? metadata, out Point[][] contours)
    {
        return TryProjectVolumeContoursTo(metadata, out contours, out _);
    }

    public bool TryProjectVolumeContoursTo(DicomSpatialMetadata? metadata, out Point[][] contours, out bool isInterpolated)
    {
        contours = [];
        isInterpolated = false;

        if (metadata is null || VolumeContours is null || VolumeContours.Length == 0 || !IsCompatibleWith(metadata))
        {
            return false;
        }

        // Multi-entry cache — survives across scroll steps so revisited slices are free.
        double currentPlanePosition = metadata.Origin.Dot(metadata.Normal);
        long cacheKey = BitConverter.DoubleToInt64Bits(Math.Round(currentPlanePosition, 3));
        _volumeContourProjectionCache ??= [];
        if (_volumeContourProjectionCache.TryGetValue(cacheKey, out var cached))
        {
            contours = cached.Contours;
            isInterpolated = cached.IsInterpolated;
            return contours.Length > 0;
        }

        List<Point[]> projected = [];
        foreach (VolumeRoiContour contour in VolumeContours)
        {
            if (!contour.IsClosed || !contour.TryProjectTo(metadata, out Point[] imagePoints) || imagePoints.Length < 3)
            {
                continue;
            }

            projected.Add(imagePoints);
        }

        if (projected.Count > 0)
        {
            contours = projected.ToArray();
            _volumeContourProjectionCache[cacheKey] = (contours, false);
            return true;
        }

        if (TryProjectInterpolatedVolumeContours(metadata, out Point[][] interpolatedContours) && interpolatedContours.Length > 0)
        {
            contours = interpolatedContours;
            isInterpolated = true;
            _volumeContourProjectionCache[cacheKey] = (contours, true);
            return true;
        }

        // Cache negative result to avoid re-computation for out-of-range slices.
        _volumeContourProjectionCache[cacheKey] = ([], false);
        return false;
    }

    private bool TryProjectInterpolatedVolumeContours(DicomSpatialMetadata metadata, out Point[][] imagePoints)
    {
        imagePoints = [];

        if (VolumeContours is null || VolumeContours.Length < 2)
        {
            return false;
        }

        double currentPlanePosition = metadata.Origin.Dot(metadata.Normal);

        var componentIndex = GetSortedContourComponentIndex();
        if (componentIndex.Length == 0)
        {
            return false;
        }

        List<Point[]> interpolated = [];
        foreach ((int componentId, double[] planePositions, VolumeRoiContour[] contours) in componentIndex)
        {
            try
            {
                if (contours.Length < 2)
                {
                    continue;
                }

                // Binary search for the bracketing pair instead of linear scan.
                int searchResult = Array.BinarySearch(planePositions, currentPlanePosition);
                if (searchResult >= 0)
                {
                    // Exact match — the direct-projection path should have handled this.
                    continue;
                }

                int insertionPoint = ~searchResult;
                if (insertionPoint <= 0 || insertionPoint >= contours.Length)
                {
                    continue;
                }

                int lowerIndex = insertionPoint - 1;
                VolumeRoiContour lowerContour = contours[lowerIndex];
                VolumeRoiContour upperContour = contours[lowerIndex + 1];
                double lowerPlane = lowerContour.PlanePosition;
                double upperPlane = upperContour.PlanePosition;
                double thickness = upperPlane - lowerPlane;
                if (Math.Abs(thickness) <= double.Epsilon)
                {
                    continue;
                }

                double t = (currentPlanePosition - lowerPlane) / thickness;
                int sampleCount = Math.Clamp(
                    Math.Min(lowerContour.Anchors.Length, upperContour.Anchors.Length),
                    12,
                    20);

                Vector3D[] interpolatedPts;
                if (!VolumeRoiInterpolationHelper.TryInterpolateContour(
                        CreateInterpolationInput(lowerContour),
                        CreateInterpolationInput(upperContour),
                        t,
                        sampleCount,
                        out interpolatedPts) ||
                    interpolatedPts.Length < 3)
                {
                    Vector3D[] lowerPoints = ResampleClosedContour(lowerContour, sampleCount);
                    Vector3D[] upperPoints = ResampleClosedContour(upperContour, sampleCount);
                    if (lowerPoints.Length < 3 || upperPoints.Length < 3)
                    {
                        continue;
                    }

                    if (lowerPoints.Length == upperPoints.Length)
                    {
                        upperPoints = AlignContourPoints(lowerPoints, upperPoints);
                    }

                    interpolatedPts = InterpolateContourPoints(lowerPoints, upperPoints, t);
                }

                Point[] projected = new Point[interpolatedPts.Length];
                bool hasInvalidProjection = false;
                for (int i = 0; i < interpolatedPts.Length; i++)
                {
                    Point projectedPoint = metadata.PixelPointFromPatient(interpolatedPts[i]);
                    if (double.IsNaN(projectedPoint.X) || double.IsNaN(projectedPoint.Y) || double.IsInfinity(projectedPoint.X) || double.IsInfinity(projectedPoint.Y))
                    {
                        hasInvalidProjection = true;
                        break;
                    }

                    projected[i] = projectedPoint;
                }

                if (!hasInvalidProjection && projected.Length >= 3)
                {
                    interpolated.Add(projected);
                }
            }
            catch (Exception exception)
            {
                App.LogRuntimeDiagnostic(
                    "VOLUME-ROI",
                    $"Interpolation failed for measurement {Id:D} component {componentId}: {exception.GetType().Name} {exception.Message}");
            }
        }

        imagePoints = interpolated.ToArray();
        return imagePoints.Length > 0;
    }

    private (int ComponentId, double[] PlanePositions, VolumeRoiContour[] Contours)[] GetSortedContourComponentIndex()
    {
        if (_sortedContourComponentIndex is not null)
        {
            return _sortedContourComponentIndex;
        }

        if (VolumeContours is null || VolumeContours.Length == 0)
        {
            _sortedContourComponentIndex = [];
            return _sortedContourComponentIndex;
        }

        _sortedContourComponentIndex = VolumeContours
            .Where(c => c.IsClosed && c.Anchors.Length >= 3)
            .GroupBy(c => c.ComponentId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                VolumeRoiContour[] ordered = g.OrderBy(c => c.PlanePosition).ToArray();
                return (g.Key, ordered.Select(c => c.PlanePosition).ToArray(), ordered);
            })
            .ToArray();
        return _sortedContourComponentIndex;
    }

    private static VolumeContourInterpolationInput CreateInterpolationInput(VolumeRoiContour contour)
    {
        return new VolumeContourInterpolationInput(
            contour.Anchors.Where(anchor => anchor.PatientPoint is not null).Select(anchor => anchor.PatientPoint!.Value).ToArray(),
            contour.PlaneOrigin,
            contour.RowDirection,
            contour.ColumnDirection,
            contour.Normal,
            contour.PlanePosition,
            contour.RowSpacing,
            contour.ColumnSpacing);
    }

    private static Vector3D[] ResampleClosedContour(VolumeRoiContour contour, int sampleCount)
    {
        Vector3D[] points = contour.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (points.Length < 3 || sampleCount < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        Vector3D[] result = new Vector3D[sampleCount];
        double step = totalLength / sampleCount;
        int segmentIndex = 0;

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
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
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Length], t);
        }

        if (GetSignedContourArea(result, contour.PlaneOrigin, contour.RowDirection, contour.ColumnDirection) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static Vector3D[] AlignContourPoints(Vector3D[] reference, Vector3D[] candidate)
    {
        if (reference.Length == 0 || candidate.Length == 0 || reference.Length != candidate.Length)
        {
            return candidate;
        }

        int bestShift = 0;
        double bestCost = double.MaxValue;
        for (int shift = 0; shift < candidate.Length; shift++)
        {
            double cost = 0;
            for (int index = 0; index < reference.Length; index++)
            {
                Vector3D delta = reference[index] - candidate[(index + shift) % candidate.Length];
                cost += delta.Dot(delta);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestShift = shift;
            }
        }

        if (bestShift == 0)
        {
            return candidate;
        }

        Vector3D[] aligned = new Vector3D[candidate.Length];
        for (int index = 0; index < candidate.Length; index++)
        {
            aligned[index] = candidate[(index + bestShift) % candidate.Length];
        }

        return aligned;
    }

    private static Vector3D[] InterpolateContourPoints(Vector3D[] first, Vector3D[] second, double t)
    {
        int count = Math.Min(first.Length, second.Length);
        Vector3D[] points = new Vector3D[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = Lerp(first[index], second[index], t);
        }

        return points;
    }

    private static double GetDistance(Vector3D first, Vector3D second) => (first - second).Length;

    private static double GetSignedContourArea(
        IReadOnlyList<Vector3D> contour,
        Vector3D planeOrigin,
        Vector3D rowDirection,
        Vector3D columnDirection)
    {
        if (contour.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < contour.Count; index++)
        {
            Vector3D currentRelative = contour[index] - planeOrigin;
            Vector3D nextRelative = contour[(index + 1) % contour.Count] - planeOrigin;
            double currentX = currentRelative.Dot(rowDirection);
            double currentY = currentRelative.Dot(columnDirection);
            double nextX = nextRelative.Dot(rowDirection);
            double nextY = nextRelative.Dot(columnDirection);
            area += (currentX * nextY) - (nextX * currentY);
        }

        return area * 0.5;
    }

    private static Vector3D Lerp(Vector3D first, Vector3D second, double t) => first + ((second - first) * t);

    private bool IsCompatibleWith(DicomSpatialMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(FrameOfReferenceUid) ||
            string.IsNullOrWhiteSpace(metadata.FrameOfReferenceUid) ||
            !string.Equals(FrameOfReferenceUid, metadata.FrameOfReferenceUid, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(AcquisitionNumber) ||
            string.IsNullOrWhiteSpace(metadata.AcquisitionNumber) ||
            string.Equals(AcquisitionNumber, metadata.AcquisitionNumber, StringComparison.Ordinal);
    }
}