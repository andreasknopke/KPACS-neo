using Avalonia;
using Avalonia.Input;
using KPACS.Viewer.Models;
using KPACS.Viewer.RoiDraft;
using KPACS.Viewer.Services;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    private const int RoiBallMinRadiusPixels = 3;
    private const int RoiBallMaxRadiusPixels = 64;
    private const int RoiBallDefaultRadiusPixels = 10;

    private Point? _roiBallHoverImagePoint;
    private BallRoiDragSession? _roiBallDragSession;
    private int _roiBallRadiusPixels = RoiBallDefaultRadiusPixels;
    private VolumeMeasurementProjectionCache? _volumeMeasurementProjectionCache;
    private VolumeDraftProjectionCache? _volumeDraftProjectionCache;

    public int BallRoiRadiusPixels => _roiBallRadiusPixels;

    private readonly record struct RoiMask(int Left, int Top, bool[,] Pixels, int PixelCount)
    {
        public int Width => Pixels.GetLength(0);
        public int Height => Pixels.GetLength(1);
    }

    private readonly record struct RoiMaskComponent(int Left, int Top, bool[,] Pixels, int PixelCount, Point Centroid);

    private readonly record struct BallRoiBrushPreview(Point ImagePoint, int RadiusPixels, bool AddRegion, bool EdgeCollision);

    private sealed class BallRoiDragSession(bool addRegion, Guid? measurementId, bool targetsVolumeDraft, Point lastAppliedPoint)
    {
        public bool AddRegion { get; } = addRegion;
        public Guid? MeasurementId { get; } = measurementId;
        public bool TargetsVolumeDraft { get; } = targetsVolumeDraft;
        public Point LastAppliedPoint { get; set; } = lastAppliedPoint;
    }

    private sealed record VolumeMeasurementProjectionCache(
        Guid MeasurementId,
        int MeasurementVersion,
        string SliceKey,
        VolumeRoiContour[] CurrentSliceContours,
        Point[][] ProjectedContours,
        bool UsesInterpolatedContours);

    private sealed record VolumeDraftProjectionCache(
        VolumeRoiDraft Draft,
        int DraftVersion,
        string SliceKey,
        VolumeRoiDraftContour[] CurrentContours,
        Point[][] ProjectedContours);

    public bool TryAdjustBallRoiRadius(int deltaPixels, out int resultingRadius)
    {
        resultingRadius = _roiBallRadiusPixels;
        if (deltaPixels == 0)
        {
            return false;
        }

        int nextRadius = Math.Clamp(_roiBallRadiusPixels + deltaPixels, RoiBallMinRadiusPixels, RoiBallMaxRadiusPixels);
        resultingRadius = nextRadius;
        if (nextRadius == _roiBallRadiusPixels)
        {
            return false;
        }

        _roiBallRadiusPixels = nextRadius;
        UpdateMeasurementPresentation();
        return true;
    }

    private void InvalidateRoiBallProjectionCaches()
    {
        _volumeMeasurementProjectionCache = null;
        _volumeDraftProjectionCache = null;
    }

    private void ClearBallRoiInteraction(bool clearHover = true)
    {
        _roiBallDragSession = null;
        if (clearHover)
        {
            _roiBallHoverImagePoint = null;
        }
    }

    private void UpdateBallRoiHoverPoint(Point? imagePoint)
    {
        _roiBallHoverImagePoint = imagePoint is Point point ? ClampImagePoint(point) : null;
    }

    private bool TryStartBallRoiCorrectionSession(Point imagePoint, IPointer pointer)
    {
        Point clamped = ClampImagePoint(imagePoint);
        UpdateBallRoiHoverPoint(clamped);

        if (!TryGetBallRoiBrushPreview(clamped, forcedAddRegion: null, out BallRoiBrushPreview preview) || !preview.EdgeCollision)
        {
            UpdateMeasurementPresentation();
            return false;
        }

        if (!TryApplyBallRoiCorrection(clamped, preview.AddRegion, preview.RadiusPixels, requireEdgeCollision: true))
        {
            return false;
        }

        _roiBallDragSession = new BallRoiDragSession(
            preview.AddRegion,
            _selectedMeasurementId,
            _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice(),
            clamped);
        pointer.Capture(RootGrid);
        _capturedPointer = pointer;
        UpdateMeasurementPresentation();
        return true;
    }

    private bool TryContinueBallRoiCorrectionSession(Point imagePoint)
    {
        Point clamped = ClampImagePoint(imagePoint);
        UpdateBallRoiHoverPoint(clamped);
        if (_roiBallDragSession is not { } session || !IsBallRoiSessionCompatible(session))
        {
            ClearBallRoiInteraction(clearHover: false);
            UpdateMeasurementPresentation();
            return false;
        }

        double minimumSpacing = Math.Max(1.0, _roiBallRadiusPixels * 0.35);
        if (Distance(session.LastAppliedPoint, clamped) < minimumSpacing)
        {
            UpdateMeasurementPresentation();
            return false;
        }

        if (!TryApplyBallRoiCorrection(clamped, session.AddRegion, _roiBallRadiusPixels, requireEdgeCollision: true))
        {
            UpdateMeasurementPresentation();
            return false;
        }

        session.LastAppliedPoint = clamped;
        UpdateMeasurementPresentation();
        return true;
    }

    private bool IsBallRoiSessionCompatible(BallRoiDragSession session)
    {
        bool volumeDraftActive = _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice();
        if (session.TargetsVolumeDraft)
        {
            return volumeDraftActive;
        }

        return !volumeDraftActive && _selectedMeasurementId == session.MeasurementId;
    }

    public bool TryApplyBallRoiCorrection(Point imagePoint)
    {
        return TryApplyBallRoiCorrection(imagePoint, forcedAddRegion: null, _roiBallRadiusPixels, requireEdgeCollision: true);
    }

    private bool TryApplyBallRoiCorrection(Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        Point clamped = ClampImagePoint(imagePoint);

        if (_volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice() && TryApplyBallCorrectionToVolumeDraft(clamped, forcedAddRegion, radiusPixels, requireEdgeCollision))
        {
            return true;
        }

        if (_selectedMeasurementId is not Guid measurementId || SpatialMetadata is null)
        {
            return false;
        }

        StudyMeasurement? measurement = _measurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return false;
        }

        return measurement.Kind switch
        {
            MeasurementKind.PolygonRoi => TryApplyBallCorrectionToPolygon(measurement, clamped, forcedAddRegion, radiusPixels, requireEdgeCollision),
            MeasurementKind.VolumeRoi => TryApplyBallCorrectionToVolumeMeasurement(measurement, clamped, forcedAddRegion, radiusPixels, requireEdgeCollision),
            _ => false,
        };
    }

    private bool TryApplyBallCorrectionToPolygon(StudyMeasurement measurement, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        if (SpatialMetadata is null || !measurement.TryProjectTo(SpatialMetadata, out Point[] imagePoints) || imagePoints.Length < 3)
        {
            return false;
        }

        if (!TrySculptContour(imagePoints, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out Point[] updatedPoints, out _, out _))
        {
            return false;
        }

        StudyMeasurement updated = measurement.WithAnchors(SpatialMetadata, updatedPoints);
        InvalidateRoiBallProjectionCaches();
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
        PushRoiHistory(
            "Apply polygon ROI ball correction",
            () => ApplyMeasurementUpdatedFromHistory(measurement),
            () => ApplyMeasurementUpdatedFromHistory(updated));
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeMeasurement(StudyMeasurement measurement, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        if (measurement.SegmentationMaskId is Guid segmentationMaskId &&
            TryResolveSegmentationMask(segmentationMaskId, out SegmentationMask3D segmentationMask) &&
            TryApplyBallCorrectionToVolumeMeasurementMask(measurement, segmentationMask, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision))
        {
            return true;
        }

        if (!TryGetVolumeMeasurementProjectedContours(measurement, out VolumeRoiContour[] currentSliceContours, out Point[][] projectedContours, out bool usesInterpolatedContours))
        {
            return false;
        }

        VolumeRoiContour[] existingContours = measurement.VolumeContours ?? [];

        if (!usesInterpolatedContours)
        {
            if (!TrySelectSculptedContour(projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out _, out _))
            {
                return false;
            }

            VolumeRoiContour target = currentSliceContours[contourIndex];
            VolumeRoiContour updatedTarget = CreateUpdatedVolumeContour(target, updatedPoints);
            VolumeRoiContour[] updatedContours = existingContours
                .Select(contour => ReferenceEquals(contour, target) ? updatedTarget : contour)
                .OrderBy(contour => contour.PlanePosition)
                .ThenBy(contour => contour.ComponentId)
                .ToArray();

            StudyMeasurement updatedMeasurement = measurement.WithVolumeContours(updatedContours);
            InvalidateRoiBallProjectionCaches();
            MeasurementUpdated?.Invoke(updatedMeasurement);
            UpdateMeasurementPresentation();
            PushRoiHistory(
                "Apply volume ROI contour correction",
                () => ApplyMeasurementUpdatedFromHistory(measurement),
                () => ApplyMeasurementUpdatedFromHistory(updatedMeasurement));
            return true;
        }

        if (!TrySelectSculptedContour(projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out _, out Point[] interpolatedUpdatedPoints, out _, out _))
        {
            return false;
        }

        VolumeRoiContour insertedContour = CreateInterpolatedVolumeContour(measurement, interpolatedUpdatedPoints);
        VolumeRoiContour[] insertedContours = existingContours
            .Concat([insertedContour])
            .OrderBy(contour => contour.PlanePosition)
            .ThenBy(contour => contour.ComponentId)
            .ToArray();

        StudyMeasurement updated = measurement.WithVolumeContours(insertedContours);
        InvalidateRoiBallProjectionCaches();
        MeasurementUpdated?.Invoke(updated);
        UpdateMeasurementPresentation();
        PushRoiHistory(
            "Insert interpolated volume ROI contour",
            () => ApplyMeasurementUpdatedFromHistory(measurement),
            () => ApplyMeasurementUpdatedFromHistory(updated));
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeDraft(Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision)
    {
        VolumeRoiDraft? previousDraft = CloneVolumeRoiDraftState();

        if (_volumeRoiDraft?.SegmentationMask is SegmentationMask3D segmentationMask &&
            TryApplyBallCorrectionToVolumeDraftMask(segmentationMask, previousDraft, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision))
        {
            return true;
        }

        if (!TryGetVolumeDraftProjectedContours(out VolumeRoiDraftContour[] currentContours, out Point[][] projectedContours))
        {
            return false;
        }

        if (!TrySelectSculptedContour(projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out _, out _))
        {
            return false;
        }

        DicomSpatialMetadata metadata = SpatialMetadata ?? throw new InvalidOperationException("Spatial metadata is required for ROI draft ball correction.");
        VolumeRoiDraftContour target = currentContours[contourIndex];
        target.Anchors.Clear();
        target.Anchors.AddRange(updatedPoints.Select(point => new MeasurementAnchor(point, metadata.PatientPointFromPixel(point))));
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        RecordVolumeRoiDraftTransition("Apply volume ROI draft correction", previousDraft, _volumeRoiDraft);
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeMeasurementMask(
        StudyMeasurement measurement,
        SegmentationMask3D segmentationMask,
        Point imagePoint,
        bool? forcedAddRegion,
        int radiusPixels,
        bool requireEdgeCollision)
    {
        if (!TryGetVolumeMeasurementProjectedContours(measurement, out _, out Point[][] projectedContours, out _))
        {
            return false;
        }

        if (!TryApplySphereEditToSegmentationMask(segmentationMask, projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out SegmentationMask3D updatedMask, out VolumeRoiContour[] updatedContours))
        {
            return false;
        }

        StudyMeasurement updatedMeasurement = measurement.WithVolumeContours(updatedContours);
        InvalidateRoiBallProjectionCaches();
        SegmentationMaskUpdated?.Invoke(updatedMask);
        MeasurementUpdated?.Invoke(updatedMeasurement);
        UpdateMeasurementPresentation();
        PushRoiHistory(
            "Apply 3D segmentation mask correction",
            () => ApplyMeasurementUpdatedFromHistory(measurement, segmentationMask),
            () => ApplyMeasurementUpdatedFromHistory(updatedMeasurement, updatedMask));
        return true;
    }

    private bool TryApplyBallCorrectionToVolumeDraftMask(
        SegmentationMask3D segmentationMask,
        VolumeRoiDraft? previousDraft,
        Point imagePoint,
        bool? forcedAddRegion,
        int radiusPixels,
        bool requireEdgeCollision)
    {
        if (_volumeRoiDraft is null || !TryGetVolumeDraftProjectedContours(out _, out Point[][] projectedContours))
        {
            return false;
        }

        if (!TryApplySphereEditToSegmentationMask(segmentationMask, projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out SegmentationMask3D updatedMask, out VolumeRoiContour[] updatedContours))
        {
            return false;
        }

        ReplaceVolumeRoiDraftFromMaskContours(_volumeRoiDraft, updatedContours, updatedMask);
        InvalidateRoiBallProjectionCaches();
        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        RecordVolumeRoiDraftTransition("Apply 3D volume ROI draft mask correction", previousDraft, _volumeRoiDraft);
        return true;
    }

    private bool TryApplySphereEditToSegmentationMask(
        SegmentationMask3D segmentationMask,
        Point[][] projectedContours,
        Point imagePoint,
        bool? forcedAddRegion,
        int radiusPixels,
        bool requireEdgeCollision,
        out SegmentationMask3D updatedMask,
        out VolumeRoiContour[] updatedContours)
    {
        updatedMask = segmentationMask;
        updatedContours = [];

        if (_volume is null || SpatialMetadata is null || projectedContours.Length == 0)
        {
            return false;
        }

        if (!TryResolveBallEditMode(projectedContours, imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out bool addRegion))
        {
            return false;
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(segmentationMask.Geometry, segmentationMask.Storage);
        Point clamped = ClampImagePoint(imagePoint);
        SpatialVector3D centerPatientPoint = SpatialMetadata.PatientPointFromPixel(clamped);
        double radiusMillimeters = GetBallCorrectionRadiusMillimeters(radiusPixels);
        if (!ApplySphereEditToMaskBuffer(buffer, centerPatientPoint, radiusMillimeters, addRegion))
        {
            return false;
        }

        HashSet<int> region = BuildRegionFromSegmentationMaskBuffer(buffer);
        if (region.Count == 0)
        {
            return false;
        }

        updatedContours = BuildAutoOutlinedVolumeContours(region);
        if (updatedContours.Length == 0)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        updatedMask = segmentationMask with
        {
            Storage = buffer.ToStorage(),
            Metadata = segmentationMask.Metadata with
            {
                SourceKind = SegmentationMaskSourceKind.ManualEdit,
                ModifiedUtc = now,
                Revision = segmentationMask.Metadata.Revision + 1,
                Statistics = buffer.ComputeStatistics(),
            }
        };

        return true;
    }

    private bool TryResolveBallEditMode(
        Point[][] projectedContours,
        Point imagePoint,
        bool? forcedAddRegion,
        int radiusPixels,
        bool requireEdgeCollision,
        out bool addRegion)
    {
        addRegion = forcedAddRegion ?? !projectedContours.Any(contour => IsPointInsidePolygon(imagePoint, contour));
        if (!requireEdgeCollision)
        {
            return true;
        }

        double bestDistance = double.MaxValue;
        foreach (Point[] contour in projectedContours)
        {
            if (TryGetBrushCollision(contour, imagePoint, radiusPixels, out _, out double distance) && distance < bestDistance)
            {
                bestDistance = distance;
            }
        }

        return bestDistance <= radiusPixels;
    }

    private double GetBallCorrectionRadiusMillimeters(int radiusPixels)
    {
        if (SpatialMetadata is null)
        {
            return Math.Max(1.0, radiusPixels);
        }

        double pixelSpacing = Math.Max(0.1, (SpatialMetadata.RowSpacing + SpatialMetadata.ColumnSpacing) * 0.5);
        return Math.Max(pixelSpacing, radiusPixels * pixelSpacing);
    }

    private bool ApplySphereEditToMaskBuffer(
        SegmentationMaskBuffer buffer,
        SpatialVector3D centerPatientPoint,
        double radiusMillimeters,
        bool addRegion)
    {
        VolumeGridGeometry geometry = buffer.Geometry;
        (double centerX, double centerY, double centerZ) = PatientPointToMaskVoxel(geometry, centerPatientPoint);

        int minX = Math.Max(0, (int)Math.Floor(centerX - (radiusMillimeters / geometry.SpacingX) - 1));
        int maxX = Math.Min(geometry.SizeX - 1, (int)Math.Ceiling(centerX + (radiusMillimeters / geometry.SpacingX) + 1));
        int minY = Math.Max(0, (int)Math.Floor(centerY - (radiusMillimeters / geometry.SpacingY) - 1));
        int maxY = Math.Min(geometry.SizeY - 1, (int)Math.Ceiling(centerY + (radiusMillimeters / geometry.SpacingY) + 1));
        int minZ = Math.Max(0, (int)Math.Floor(centerZ - (radiusMillimeters / geometry.SpacingZ) - 1));
        int maxZ = Math.Min(geometry.SizeZ - 1, (int)Math.Ceiling(centerZ + (radiusMillimeters / geometry.SpacingZ) + 1));

        double radiusSquared = radiusMillimeters * radiusMillimeters;
        bool changed = false;
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    SpatialVector3D voxelPatientPoint = MaskVoxelToPatientPoint(geometry, x, y, z);
                    SpatialVector3D delta = voxelPatientPoint - centerPatientPoint;
                    double distanceSquared = delta.Dot(delta);
                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    if (buffer.Get(x, y, z) == addRegion)
                    {
                        continue;
                    }

                    buffer.Set(x, y, z, addRegion);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private HashSet<int> BuildRegionFromSegmentationMaskBuffer(SegmentationMaskBuffer buffer)
    {
        return [.. buffer.EnumerateForegroundLinearIndices()];
    }

    private static (double X, double Y, double Z) PatientPointToMaskVoxel(VolumeGridGeometry geometry, SpatialVector3D patientPoint)
    {
        SpatialVector3D relative = patientPoint - geometry.Origin;
        double x = relative.Dot(geometry.RowDirection) / geometry.SpacingX;
        double y = relative.Dot(geometry.ColumnDirection) / geometry.SpacingY;
        double z = relative.Dot(geometry.Normal) / geometry.SpacingZ;
        return (x, y, z);
    }

    private static SpatialVector3D MaskVoxelToPatientPoint(VolumeGridGeometry geometry, int x, int y, int z)
    {
        return geometry.Origin
            + (geometry.RowDirection * (x * geometry.SpacingX))
            + (geometry.ColumnDirection * (y * geometry.SpacingY))
            + (geometry.Normal * (z * geometry.SpacingZ));
    }

    private void ReplaceVolumeRoiDraftFromMaskContours(VolumeRoiDraft draft, IEnumerable<VolumeRoiContour> contours, SegmentationMask3D segmentationMask)
    {
        draft.Contours.Clear();
        foreach (VolumeRoiContour contour in contours.OrderBy(candidate => candidate.PlanePosition).ThenBy(candidate => candidate.ComponentId))
        {
            string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
            string contourKey = BuildVolumeRoiContourKey(sliceKey, contour.ComponentId);
            draft.Contours[contourKey] = new VolumeRoiDraftContour(
                sliceKey,
                contourKey,
                contour.ComponentId,
                contour.SourceFilePath,
                contour.ReferencedSopInstanceUid,
                contour.PlaneOrigin,
                contour.RowDirection,
                contour.ColumnDirection,
                contour.Normal,
                contour.PlanePosition,
                contour.RowSpacing,
                contour.ColumnSpacing,
                contour.Anchors.ToList(),
                contour.IsClosed);
        }

        draft.PendingAddContour = null;
        draft.ActiveAddComponentId = null;
        draft.NextComponentId = Math.Max(1, draft.Contours.Values.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);
        draft.SegmentationMask = segmentationMask;
    }

    private bool TryBuildVolumeDraftSliceMask(VolumeRoiDraftContour[] contours, out RoiMask mask)
    {
        mask = default;
        if (contours.Length == 0)
        {
            return false;
        }

        if (_volumeRoiDraft is not null && TryGetVolumeDraftProjectedContours(out VolumeRoiDraftContour[] cachedContours, out Point[][] projected) && AreSameDraftContours(cachedContours, contours))
        {
            return TryBuildMultiPolygonMask(projected, _roiBallRadiusPixels + 2, out mask);
        }

        if (SpatialMetadata is null)
        {
            return false;
        }

        Point[][] uncachedProjected = contours
            .Select(contour => contour.TryProjectTo(SpatialMetadata, out Point[] points) ? points : [])
            .Where(points => points.Length >= 3)
            .ToArray();
        if (uncachedProjected.Length == 0)
        {
            return false;
        }

        return TryBuildMultiPolygonMask(uncachedProjected, _roiBallRadiusPixels + 2, out mask);
    }

    private bool TryBuildVolumeSliceMask(StudyMeasurement measurement, out RoiMask mask, out VolumeRoiContour[] existingContours)
    {
        mask = default;
        existingContours = [];
        if (!TryGetVolumeMeasurementProjectedContours(measurement, out existingContours, out Point[][] projectedContours, out _))
        {
            return false;
        }

        return TryBuildMultiPolygonMask(projectedContours, _roiBallRadiusPixels + 2, out mask);
    }

    private bool TryGetBallRoiBrushPreview(Point imagePoint, bool? forcedAddRegion, out BallRoiBrushPreview preview)
    {
        preview = default;
        Point clamped = ClampImagePoint(imagePoint);

        if (_volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice())
        {
            return TryGetVolumeDraftProjectedContours(out _, out Point[][] currentContours)
                && TryCreateBallRoiBrushPreview(clamped, currentContours, forcedAddRegion, out preview);
        }

        if (_selectedMeasurementId is not Guid measurementId || SpatialMetadata is null)
        {
            return false;
        }

        StudyMeasurement? measurement = _measurements.FirstOrDefault(candidate => candidate.Id == measurementId);
        if (measurement is null)
        {
            return false;
        }

        switch (measurement.Kind)
        {
            case MeasurementKind.PolygonRoi:
                if (!measurement.TryProjectTo(SpatialMetadata, out Point[] polygonPoints) || polygonPoints.Length < 3)
                {
                    return false;
                }
                return TryCreateBallRoiBrushPreview(clamped, [polygonPoints], forcedAddRegion, out preview);
            case MeasurementKind.VolumeRoi:
                if (!TryGetVolumeMeasurementProjectedContours(measurement, out _, out Point[][] projectedContours, out _))
                {
                    return false;
                }

                return TryCreateBallRoiBrushPreview(clamped, projectedContours, forcedAddRegion, out preview);
            default:
                return false;
        }
    }

    private bool TryGetVolumeMeasurementProjectedContours(
        StudyMeasurement measurement,
        out VolumeRoiContour[] currentSliceContours,
        out Point[][] projectedContours,
        out bool usesInterpolatedContours)
    {
        currentSliceContours = [];
        projectedContours = [];
        usesInterpolatedContours = false;

        if (SpatialMetadata is null || measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return false;
        }

        string sliceKey = GetCurrentVolumeRoiSliceKey(SpatialMetadata);
        if (_volumeMeasurementProjectionCache is { } cache &&
            cache.MeasurementId == measurement.Id &&
            cache.MeasurementVersion == _measurementGeometryVersion &&
            string.Equals(cache.SliceKey, sliceKey, StringComparison.Ordinal))
        {
            currentSliceContours = cache.CurrentSliceContours;
            projectedContours = cache.ProjectedContours;
            usesInterpolatedContours = cache.UsesInterpolatedContours;
            return projectedContours.Length > 0;
        }

        currentSliceContours = measurement.VolumeContours
            .Where(IsContourOnCurrentSlice)
            .OrderBy(contour => contour.ComponentId)
            .ToArray();

        List<Point[]> projected = [];
        foreach (VolumeRoiContour contour in currentSliceContours)
        {
            if (contour.IsClosed && contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                projected.Add(points);
            }
        }

        if (projected.Count == 0 && measurement.TryProjectVolumeContoursTo(SpatialMetadata, out Point[][] interpolatedContours) && interpolatedContours.Length > 0)
        {
            projected.AddRange(interpolatedContours.Where(points => points.Length >= 3));
            currentSliceContours = [];
            usesInterpolatedContours = true;
        }

        projectedContours = projected.ToArray();
        _volumeMeasurementProjectionCache = new VolumeMeasurementProjectionCache(
            measurement.Id,
            _measurementGeometryVersion,
            sliceKey,
            currentSliceContours,
            projectedContours,
            usesInterpolatedContours);
        return projectedContours.Length > 0;
    }

    private bool TryGetVolumeDraftProjectedContours(
        out VolumeRoiDraftContour[] currentContours,
        out Point[][] projectedContours)
    {
        currentContours = [];
        projectedContours = [];

        if (_volumeRoiDraft is null || SpatialMetadata is null)
        {
            return false;
        }

        string sliceKey = GetCurrentVolumeRoiSliceKey(SpatialMetadata);
        if (_volumeDraftProjectionCache is { } cache &&
            ReferenceEquals(cache.Draft, _volumeRoiDraft) &&
            cache.DraftVersion == _volumeRoiDraftPreviewVersion &&
            string.Equals(cache.SliceKey, sliceKey, StringComparison.Ordinal))
        {
            currentContours = cache.CurrentContours;
            projectedContours = cache.ProjectedContours;
            return projectedContours.Length > 0;
        }

        currentContours = _volumeRoiDraft.Contours.Values
            .Where(contour => contour.IsClosed && string.Equals(contour.SliceKey, sliceKey, StringComparison.Ordinal))
            .OrderBy(contour => contour.ComponentId)
            .ToArray();
        if (currentContours.Length == 0)
        {
            _volumeDraftProjectionCache = new VolumeDraftProjectionCache(_volumeRoiDraft, _volumeRoiDraftPreviewVersion, sliceKey, currentContours, projectedContours);
            return false;
        }

        projectedContours = currentContours
            .Select(contour => contour.TryProjectTo(SpatialMetadata, out Point[] points) ? points : [])
            .Where(points => points.Length >= 3)
            .ToArray();
        _volumeDraftProjectionCache = new VolumeDraftProjectionCache(
            _volumeRoiDraft,
            _volumeRoiDraftPreviewVersion,
            sliceKey,
            currentContours,
            projectedContours);
        return projectedContours.Length > 0;
    }

    private static bool AreSameDraftContours(VolumeRoiDraftContour[] first, VolumeRoiDraftContour[] second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (int index = 0; index < first.Length; index++)
        {
            if (!ReferenceEquals(first[index], second[index]))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCreateBallRoiBrushPreview(Point imagePoint, Point[][] contours, bool? forcedAddRegion, out BallRoiBrushPreview preview)
    {
        bool addRegion = forcedAddRegion ?? !contours.Any(contour => IsPointInsidePolygon(imagePoint, contour));
        bool edgeCollision = false;
        double bestDistance = double.MaxValue;
        foreach (Point[] contour in contours)
        {
            if (TryGetBrushCollision(contour, imagePoint, _roiBallRadiusPixels, out _, out double distance) && distance < bestDistance)
            {
                bestDistance = distance;
                edgeCollision = true;
            }
        }

        preview = new BallRoiBrushPreview(imagePoint, _roiBallRadiusPixels, addRegion, edgeCollision);
        return true;
    }

    private bool TrySelectSculptedContour(Point[][] contours, Point imagePoint, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision, out int contourIndex, out Point[] updatedPoints, out bool addRegion, out double collisionDistance)
    {
        contourIndex = -1;
        updatedPoints = [];
        addRegion = false;
        collisionDistance = double.MaxValue;

        for (int index = 0; index < contours.Length; index++)
        {
            if (!TrySculptContour(contours[index], imagePoint, forcedAddRegion, radiusPixels, requireEdgeCollision, out Point[] candidatePoints, out bool candidateAddRegion, out double candidateDistance))
            {
                continue;
            }

            if (candidateDistance < collisionDistance)
            {
                contourIndex = index;
                updatedPoints = candidatePoints;
                addRegion = candidateAddRegion;
                collisionDistance = candidateDistance;
            }
        }

        return contourIndex >= 0;
    }

    private bool TrySculptContour(Point[] contour, Point brushCenter, bool? forcedAddRegion, int radiusPixels, bool requireEdgeCollision, out Point[] updatedPoints, out bool addRegion, out double collisionDistance)
    {
        updatedPoints = contour;
        addRegion = forcedAddRegion ?? !IsPointInsidePolygon(brushCenter, contour);
        collisionDistance = double.MaxValue;
        if (!TryGetBrushCollision(contour, brushCenter, radiusPixels, out Point collisionPoint, out collisionDistance))
        {
            return !requireEdgeCollision && contour.Length >= 3;
        }

        if (requireEdgeCollision && collisionDistance > radiusPixels)
        {
            return false;
        }

        updatedPoints = SculptContourLocally(contour, brushCenter, collisionPoint, radiusPixels);
        return updatedPoints.Length >= 3;
    }

    private Point[] SculptContourLocally(Point[] contour, Point brushCenter, Point collisionPoint, int radiusPixels)
    {
        Point[] adjusted = contour.ToArray();
        double collisionDistance = Distance(brushCenter, collisionPoint);
        double penetration = Math.Clamp((radiusPixels - collisionDistance) / Math.Max(1.0, radiusPixels), 0.08, 0.85);
        double influenceRadius = Math.Max(radiusPixels * 1.8, 6.0);

        for (int index = 0; index < contour.Length; index++)
        {
            Point point = contour[index];
            double localDistance = Distance(point, collisionPoint);
            if (localDistance > influenceRadius)
            {
                continue;
            }

            Vector radial = point - brushCenter;
            double radialLength = Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y));
            if (radialLength < 1e-3)
            {
                radial = collisionPoint - brushCenter;
                radialLength = Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y));
                if (radialLength < 1e-3)
                {
                    continue;
                }
            }

            Vector unit = new(radial.X / radialLength, radial.Y / radialLength);
            Point target = new(
                brushCenter.X + (unit.X * radiusPixels),
                brushCenter.Y + (unit.Y * radiusPixels));
            double influence = Math.Pow(1.0 - (localDistance / influenceRadius), 2.0) * penetration;
            adjusted[index] = ClampImagePoint(new Point(
                point.X + ((target.X - point.X) * influence),
                point.Y + ((target.Y - point.Y) * influence)));
        }

        Point[] smoothed = adjusted.ToArray();
        for (int index = 0; index < contour.Length; index++)
        {
            double localDistance = Distance(contour[index], collisionPoint);
            if (localDistance > influenceRadius * 1.1)
            {
                continue;
            }

            Point previous = adjusted[(index - 1 + adjusted.Length) % adjusted.Length];
            Point current = adjusted[index];
            Point next = adjusted[(index + 1) % adjusted.Length];
            smoothed[index] = ClampImagePoint(new Point(
                (current.X * 0.5) + ((previous.X + next.X) * 0.25),
                (current.Y * 0.5) + ((previous.Y + next.Y) * 0.25)));
        }

        return smoothed;
    }

    private bool TryGetBrushCollision(Point[] contour, Point brushCenter, int radiusPixels, out Point collisionPoint, out double collisionDistance)
    {
        collisionPoint = default;
        collisionDistance = double.MaxValue;

        for (int index = 0; index < contour.Length; index++)
        {
            Point start = contour[index];
            Point end = contour[(index + 1) % contour.Length];
            Point closest = GetClosestPointOnSegment(brushCenter, start, end);
            double distance = Distance(brushCenter, closest);
            if (distance < collisionDistance)
            {
                collisionDistance = distance;
                collisionPoint = closest;
            }
        }

        return collisionDistance <= radiusPixels;
    }

    private static Point GetClosestPointOnSegment(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        double lengthSquared = (segment.X * segment.X) + (segment.Y * segment.Y);
        if (lengthSquared <= double.Epsilon)
        {
            return start;
        }

        Vector pointVector = point - start;
        double t = ((pointVector.X * segment.X) + (pointVector.Y * segment.Y)) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        return new Point(start.X + (segment.X * t), start.Y + (segment.Y * t));
    }

    private VolumeRoiContour CreateUpdatedVolumeContour(VolumeRoiContour sourceContour, Point[] updatedPoints)
    {
        if (SpatialMetadata is null)
        {
            return sourceContour;
        }

        return new VolumeRoiContour(
            updatedPoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
            sourceContour.SourceFilePath,
            sourceContour.ReferencedSopInstanceUid,
            sourceContour.PlaneOrigin,
            sourceContour.RowDirection,
            sourceContour.ColumnDirection,
            sourceContour.Normal,
            sourceContour.PlanePosition,
            sourceContour.IsClosed,
            sourceContour.RowSpacing,
            sourceContour.ColumnSpacing,
            sourceContour.ComponentId);
    }

    private VolumeRoiContour CreateInterpolatedVolumeContour(StudyMeasurement measurement, Point[] updatedPoints)
    {
        if (SpatialMetadata is null)
        {
            throw new InvalidOperationException("Spatial metadata required for interpolated contour creation.");
        }

        int componentId = ResolveInterpolatedComponentId(measurement, GetPolygonCenter(updatedPoints));
        return new VolumeRoiContour(
            updatedPoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
            SpatialMetadata.FilePath,
            SpatialMetadata.SopInstanceUid,
            SpatialMetadata.Origin,
            SpatialMetadata.RowDirection,
            SpatialMetadata.ColumnDirection,
            SpatialMetadata.Normal,
            GetPlanePosition(SpatialMetadata),
            true,
            SpatialMetadata.RowSpacing,
            SpatialMetadata.ColumnSpacing,
            componentId);
    }

    private int ResolveInterpolatedComponentId(StudyMeasurement measurement, Point contourCenter)
    {
        if (SpatialMetadata is null || measurement.VolumeContours is null || measurement.VolumeContours.Length == 0)
        {
            return 0;
        }

        int bestComponentId = measurement.VolumeContours[0].ComponentId;
        double bestScore = double.MaxValue;
        double targetPlane = GetPlanePosition(SpatialMetadata);
        foreach (VolumeRoiContour contour in measurement.VolumeContours.Where(contour => contour.IsClosed && contour.Anchors.Length >= 3))
        {
            double score = Math.Abs(contour.PlanePosition - targetPlane);
            if (contour.TryProjectTo(SpatialMetadata, out Point[] projected) && projected.Length >= 3)
            {
                score += Distance(GetPolygonCenter(projected), contourCenter) * 0.01;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestComponentId = contour.ComponentId;
            }
        }

        return bestComponentId;
    }

    private VolumeRoiContour[] BuildReplacementVolumeContours(StudyMeasurement measurement, VolumeRoiContour[] existingContours, List<RoiMaskComponent> components)
    {
        if (SpatialMetadata is null)
        {
            return [];
        }

        List<(int ComponentId, Point Centroid)> existing = [];
        foreach (VolumeRoiContour contour in existingContours)
        {
            if (contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                existing.Add((contour.ComponentId, GetPolygonCenter(points)));
            }
        }

        List<int> availableComponentIds = existing.Select(item => item.ComponentId).ToList();
        int nextComponentId = Math.Max(0, (measurement.VolumeContours?.Select(contour => contour.ComponentId).DefaultIfEmpty(-1).Max() ?? -1) + 1);
        List<VolumeRoiContour> replacement = [];

        foreach (RoiMaskComponent component in components.OrderByDescending(component => component.PixelCount))
        {
            Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(component.Left, component.Top, component.Pixels, component.PixelCount), 80)
                .Select(point => new Point(point.X + component.Left, point.Y + component.Top))
                .ToArray();
            if (imagePoints.Length < 3)
            {
                continue;
            }

            int componentId;
            if (availableComponentIds.Count > 0)
            {
                int bestIndex = 0;
                double bestDistance = double.MaxValue;
                for (int index = 0; index < availableComponentIds.Count; index++)
                {
                    Point existingCentroid = existing.First(item => item.ComponentId == availableComponentIds[index]).Centroid;
                    double distance = Distance(existingCentroid, GetGlobalCentroid(component));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = index;
                    }
                }

                componentId = availableComponentIds[bestIndex];
                availableComponentIds.RemoveAt(bestIndex);
            }
            else
            {
                componentId = nextComponentId++;
            }

            replacement.Add(new VolumeRoiContour(
                imagePoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToArray(),
                SpatialMetadata.FilePath,
                SpatialMetadata.SopInstanceUid,
                SpatialMetadata.Origin,
                SpatialMetadata.RowDirection,
                SpatialMetadata.ColumnDirection,
                SpatialMetadata.Normal,
                GetPlanePosition(SpatialMetadata),
                true,
                SpatialMetadata.RowSpacing,
                SpatialMetadata.ColumnSpacing,
                componentId));
        }

        return replacement.ToArray();
    }

    private void ReplaceDraftSliceContours(VolumeRoiDraft draft, VolumeRoiDraftContour[] existingContours, List<RoiMaskComponent> components)
    {
        if (SpatialMetadata is null)
        {
            return;
        }

        string sliceKey = GetCurrentVolumeRoiSliceKey(SpatialMetadata);
        foreach (string key in draft.Contours.Where(entry => string.Equals(entry.Value.SliceKey, sliceKey, StringComparison.Ordinal)).Select(entry => entry.Key).ToArray())
        {
            draft.Contours.Remove(key);
        }

        List<(int ComponentId, Point Centroid)> existing = [];
        foreach (VolumeRoiDraftContour contour in existingContours)
        {
            if (contour.TryProjectTo(SpatialMetadata, out Point[] points) && points.Length >= 3)
            {
                existing.Add((contour.ComponentId, GetPolygonCenter(points)));
            }
        }

        List<int> availableComponentIds = existing.Select(item => item.ComponentId).ToList();
        int nextComponentId = Math.Max(draft.NextComponentId, existingContours.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);

        foreach (RoiMaskComponent component in components.OrderByDescending(component => component.PixelCount))
        {
            Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(component.Left, component.Top, component.Pixels, component.PixelCount), 80)
                .Select(point => new Point(point.X + component.Left, point.Y + component.Top))
                .ToArray();
            if (imagePoints.Length < 3)
            {
                continue;
            }

            int componentId;
            if (availableComponentIds.Count > 0)
            {
                int bestIndex = 0;
                double bestDistance = double.MaxValue;
                for (int index = 0; index < availableComponentIds.Count; index++)
                {
                    Point existingCentroid = existing.First(item => item.ComponentId == availableComponentIds[index]).Centroid;
                    double distance = Distance(existingCentroid, GetGlobalCentroid(component));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = index;
                    }
                }

                componentId = availableComponentIds[bestIndex];
                availableComponentIds.RemoveAt(bestIndex);
            }
            else
            {
                componentId = nextComponentId++;
            }

            string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
            draft.Contours[contourKey] = new VolumeRoiDraftContour(
                sliceKey,
                contourKey,
                componentId,
                SpatialMetadata.FilePath,
                SpatialMetadata.SopInstanceUid,
                SpatialMetadata.Origin,
                SpatialMetadata.RowDirection,
                SpatialMetadata.ColumnDirection,
                SpatialMetadata.Normal,
                GetPlanePosition(SpatialMetadata),
                SpatialMetadata.RowSpacing,
                SpatialMetadata.ColumnSpacing,
                imagePoints.Select(point => new MeasurementAnchor(point, SpatialMetadata.PatientPointFromPixel(point))).ToList(),
                true);
            draft.ActiveAddComponentId = componentId;
        }

        draft.NextComponentId = Math.Max(draft.NextComponentId, nextComponentId);
        draft.PendingAddContour = null;
    }

    private bool TryBuildPolygonMask(IReadOnlyList<Point> polygonPoints, int padding, out RoiMask mask)
    {
        mask = default;
        if (polygonPoints.Count < 3)
        {
            return false;
        }

        int left = Math.Clamp((int)Math.Floor(polygonPoints.Min(point => point.X)) - padding, 0, Math.Max(0, _imageWidth - 1));
        int right = Math.Clamp((int)Math.Ceiling(polygonPoints.Max(point => point.X)) + padding, 0, Math.Max(0, _imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(polygonPoints.Min(point => point.Y)) - padding, 0, Math.Max(0, _imageHeight - 1));
        int bottom = Math.Clamp((int)Math.Ceiling(polygonPoints.Max(point => point.Y)) + padding, 0, Math.Max(0, _imageHeight - 1));
        if (right <= left || bottom <= top)
        {
            return false;
        }

        Point[] polygon = polygonPoints as Point[] ?? polygonPoints.ToArray();
        bool[,] pixels = new bool[right - left + 1, bottom - top + 1];
        int pixelCount = 0;
        for (int y = 0; y < pixels.GetLength(1); y++)
        {
            for (int x = 0; x < pixels.GetLength(0); x++)
            {
                bool inside = IsPointInsidePolygon(new Point(left + x + 0.5, top + y + 0.5), polygon);
                pixels[x, y] = inside;
                if (inside)
                {
                    pixelCount++;
                }
            }
        }

        if (pixelCount < 3)
        {
            return false;
        }

        mask = new RoiMask(left, top, pixels, pixelCount);
        return true;
    }

    private bool TryBuildMultiPolygonMask(IEnumerable<Point[]> polygons, int padding, out RoiMask mask)
    {
        mask = default;
        Point[][] polygonArray = polygons.Where(points => points.Length >= 3).ToArray();
        if (polygonArray.Length == 0)
        {
            return false;
        }

        int left = Math.Clamp((int)Math.Floor(polygonArray.Min(points => points.Min(point => point.X))) - padding, 0, Math.Max(0, _imageWidth - 1));
        int right = Math.Clamp((int)Math.Ceiling(polygonArray.Max(points => points.Max(point => point.X))) + padding, 0, Math.Max(0, _imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(polygonArray.Min(points => points.Min(point => point.Y))) - padding, 0, Math.Max(0, _imageHeight - 1));
        int bottom = Math.Clamp((int)Math.Ceiling(polygonArray.Max(points => points.Max(point => point.Y))) + padding, 0, Math.Max(0, _imageHeight - 1));
        if (right <= left || bottom <= top)
        {
            return false;
        }

        bool[,] pixels = new bool[right - left + 1, bottom - top + 1];
        int pixelCount = 0;
        for (int y = 0; y < pixels.GetLength(1); y++)
        {
            for (int x = 0; x < pixels.GetLength(0); x++)
            {
                Point sample = new(left + x + 0.5, top + y + 0.5);
                bool inside = polygonArray.Any(points => IsPointInsidePolygon(sample, points));
                pixels[x, y] = inside;
                if (inside)
                {
                    pixelCount++;
                }
            }
        }

        if (pixelCount < 3)
        {
            return false;
        }

        mask = new RoiMask(left, top, pixels, pixelCount);
        return true;
    }

    private static void ApplyBallToMask(bool[,] pixels, int centerX, int centerY, int radius, bool add)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        int radiusSquared = radius * radius;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                pixels[x, y] = add;
            }
        }
    }

    private static bool DoesBrushIntersectRoiEdge(bool[,] pixels, int centerX, int centerY, int radius)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        bool hasInside = false;
        bool hasOutside = false;
        int radiusSquared = radius * radius;
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        {
            for (int x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
            {
                int dx = x - centerX;
                int dy = y - centerY;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                if (pixels[x, y])
                {
                    hasInside = true;
                }
                else
                {
                    hasOutside = true;
                }

                if (hasInside && hasOutside)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool[,] CloneMask(bool[,] source)
    {
        int width = source.GetLength(0);
        int height = source.GetLength(1);
        bool[,] clone = new bool[width, height];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private List<RoiMaskComponent> ExtractMaskComponents(bool[,] pixels, int leftOffset, int topOffset)
    {
        int width = pixels.GetLength(0);
        int height = pixels.GetLength(1);
        bool[,] visited = new bool[width, height];
        List<RoiMaskComponent> components = [];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!pixels[x, y] || visited[x, y])
                {
                    continue;
                }

                Queue<(int X, int Y)> queue = new();
                List<(int X, int Y)> points = [];
                queue.Enqueue((x, y));
                visited[x, y] = true;
                int left = x;
                int right = x;
                int top = y;
                int bottom = y;
                double sumX = 0;
                double sumY = 0;

                while (queue.Count > 0)
                {
                    (int currentX, int currentY) = queue.Dequeue();
                    if (!pixels[currentX, currentY])
                    {
                        continue;
                    }

                    points.Add((currentX, currentY));
                    left = Math.Min(left, currentX);
                    right = Math.Max(right, currentX);
                    top = Math.Min(top, currentY);
                    bottom = Math.Max(bottom, currentY);
                    sumX += currentX + 0.5;
                    sumY += currentY + 0.5;

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
                            if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                            {
                                continue;
                            }

                            visited[nextX, nextY] = true;
                            queue.Enqueue((nextX, nextY));
                        }
                    }
                }

                if (points.Count < 3)
                {
                    continue;
                }

                bool[,] componentPixels = new bool[right - left + 1, bottom - top + 1];
                foreach ((int pointX, int pointY) in points)
                {
                    componentPixels[pointX - left, pointY - top] = true;
                }

                components.Add(new RoiMaskComponent(
                    left + leftOffset,
                    top + topOffset,
                    componentPixels,
                    points.Count,
                    new Point((sumX / points.Count) + leftOffset, (sumY / points.Count) + topOffset)));
            }
        }

        return components;
    }

    private RoiMaskComponent ChoosePrimaryComponent(List<RoiMaskComponent> components, Point originalCentroid, Point imagePoint, bool addRegion)
    {
        if (components.Count == 1)
        {
            return components[0];
        }

        Point preferredPoint = addRegion ? imagePoint : originalCentroid;
        return components
            .OrderBy(component => Distance(GetGlobalCentroid(component), preferredPoint))
            .ThenByDescending(component => component.PixelCount)
            .First();
    }

    private static Point GetGlobalCentroid(RoiMaskComponent component) => component.Centroid;

    private bool IsContourOnCurrentSlice(VolumeRoiContour contour)
    {
        if (SpatialMetadata is null)
        {
            return false;
        }

        return string.Equals(
            BuildVolumeRoiSliceKey(SpatialMetadata.SeriesInstanceUid, SpatialMetadata.FrameOfReferenceUid, contour.PlanePosition),
            GetCurrentVolumeRoiSliceKey(SpatialMetadata),
            StringComparison.Ordinal);
    }
}

internal static class RoiBallCorrectionExtensions
{
    public static bool ContainsPoint(this bool[,] pixels, int x, int y)
    {
        return (uint)x < (uint)pixels.GetLength(0) && (uint)y < (uint)pixels.GetLength(1) && pixels[x, y];
    }
}