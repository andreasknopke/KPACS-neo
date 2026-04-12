using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System.Diagnostics;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly Dictionary<Guid, CenterlineSeedSet> _centerlineSeedSets = [];
    private readonly Dictionary<Guid, CenterlinePath> _centerlinePaths = [];
    private readonly ICenterlineExtractionService _centerlineExtractionService = new CenterlineExtractionService();
    private Guid? _selectedCenterlineSeedSetId;
    private bool _isCenterlineEditMode;
    private CancellationTokenSource? _centerlineComputationCancellation;
    private int _centerlineComputationVersion;

    private void OnToolboxCenterlineClick(object? sender, RoutedEventArgs e)
    {
        bool enable = ToolboxCenterlineButton.IsChecked == true;
        SetCenterlineEditMode(enable, showToast: true);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void OnCenterlinePopupClick(object? sender, RoutedEventArgs e)
    {
        SetCenterlineEditMode(true, showToast: true);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void SetCenterlineEditMode(bool enabled, bool showToast)
    {
        if (_isCenterlineEditMode == enabled)
        {
            UpdateCenterlineToolButton();
            RefreshCenterlinePanels();
            UpdateStatus();
            return;
        }

        _isCenterlineEditMode = enabled;
        if (enabled)
        {
            EnsureActiveCenterlineSeedSet();
            _is3DCursorToolArmed = false;
            if (_measurementTool != MeasurementTool.None)
            {
                _measurementTool = MeasurementTool.None;
            }

            Update3DCursorToolButton();
            UpdateMeasurementToolButtons();
            RefreshMeasurementPanels();
            RefreshCenterlinePanels();
            if (showToast)
            {
                ShowToast(
                    "Centerline seeds armed. Click start then end; CTRL-click adds guide points, SHIFT-click resets start, ALT-click resets end.",
                    ToastSeverity.Info,
                    TimeSpan.FromSeconds(6));
            }
        }
        else
        {
            UpdateMeasurementToolButtons();
            UpdateCenterlineToolButton();
            RefreshCenterlinePanels();
            UpdateStatus();
            if (showToast)
            {
                ShowToast("Centerline seed mode closed.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
            }

            return;
        }

        UpdateCenterlineToolButton();
        RefreshCenterlinePanels();
        UpdateStatus();
        ScheduleMeasurementSessionSave();
    }

    private void UpdateCenterlineToolButton()
    {
        if (ToolboxCenterlineButton is not null)
        {
            ToolboxCenterlineButton.IsChecked = _isCenterlineEditMode;
        }
    }

    private bool TryHandleCenterlineSeedPlacement(ViewportSlot sourceSlot, DicomImagePointerInfo info)
    {
        if (!_isCenterlineEditMode || sourceSlot.CurrentSpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return false;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);

        CenterlineSeedSet seedSet = EnsureActiveCenterlineSeedSet();
        CenterlineSeedKind seedKind = ResolveCenterlineSeedKind(seedSet, info.Modifiers);
        CenterlineSeed? existingSeed = seedKind switch
        {
            CenterlineSeedKind.Start => seedSet.StartSeed,
            CenterlineSeedKind.End => seedSet.EndSeed,
            _ => null,
        };

        CenterlineSeed seed = new()
        {
            Id = existingSeed?.Id ?? Guid.NewGuid(),
            Kind = seedKind,
            PatientPoint = metadata.PatientPointFromPixel(info.ImagePoint),
            SeriesInstanceUid = sourceSlot.Series?.SeriesInstanceUid ?? metadata.SeriesInstanceUid,
            SopInstanceUid = metadata.SopInstanceUid,
            CreatedUtc = existingSeed?.CreatedUtc ?? DateTimeOffset.UtcNow,
        };

        CenterlineSeedSet updatedSeedSet = seedSet.UpsertSeed(seed);
        _centerlineSeedSets[updatedSeedSet.Id] = updatedSeedSet;
        _selectedCenterlineSeedSetId = updatedSeedSet.Id;
        RebuildCenterlinePreviewPath(updatedSeedSet);
        ScheduleCenterlineComputation(updatedSeedSet, showSuccessToast: seedKind != CenterlineSeedKind.Guide);
        ScheduleMeasurementSessionSave();
        UpdateStatus();

        string actionLabel = existingSeed is null ? "placed" : "updated";
        string guideSuffix = seedKind == CenterlineSeedKind.Guide
            ? $" ({updatedSeedSet.GuideSeeds.Count} guide point{(updatedSeedSet.GuideSeeds.Count == 1 ? string.Empty : "s")})"
            : string.Empty;
        ShowToast(
            $"Centerline {seedKind.ToString().ToLowerInvariant()} {actionLabel}{guideSuffix}.",
            ToastSeverity.Success,
            TimeSpan.FromSeconds(3));

        return true;
    }

    private bool TryRemoveLastCenterlineSeed()
    {
        if (_selectedCenterlineSeedSetId is not Guid selectedSeedSetId ||
            !_centerlineSeedSets.TryGetValue(selectedSeedSetId, out CenterlineSeedSet? seedSet))
        {
            return false;
        }

        CenterlineSeedSet updatedSeedSet = seedSet.RemoveLastSeed();
        if (ReferenceEquals(updatedSeedSet, seedSet) || updatedSeedSet == seedSet)
        {
            return false;
        }

        _centerlineSeedSets[selectedSeedSetId] = updatedSeedSet;
        RebuildCenterlinePreviewPath(updatedSeedSet);
        ScheduleCenterlineComputation(updatedSeedSet, showSuccessToast: false);
        ScheduleMeasurementSessionSave();
        UpdateStatus();
        ShowToast("Removed the last centerline seed.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
        return true;
    }

    private CenterlineSeedSet EnsureActiveCenterlineSeedSet()
    {
        if (_selectedCenterlineSeedSetId is Guid selectedSeedSetId &&
            _centerlineSeedSets.TryGetValue(selectedSeedSetId, out CenterlineSeedSet? existing))
        {
            return existing;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CenterlineSeedSet created = new()
        {
            Id = Guid.NewGuid(),
            Label = $"Centerline {_centerlineSeedSets.Count + 1}",
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        _centerlineSeedSets[created.Id] = created;
        _selectedCenterlineSeedSetId = created.Id;
        return created;
    }

    private CenterlineSeedKind ResolveCenterlineSeedKind(CenterlineSeedSet seedSet, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            return CenterlineSeedKind.Guide;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            return CenterlineSeedKind.End;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
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

    private void RebuildCenterlinePreviewPath(CenterlineSeedSet seedSet)
    {
        IReadOnlyList<CenterlineSeed> orderedSeeds = seedSet.GetOrderedSeeds();
        CenterlinePath? existingPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == seedSet.Id);
        if (orderedSeeds.Count == 0)
        {
            if (existingPath is not null)
            {
                _centerlinePaths.Remove(existingPath.Id);
            }

            return;
        }

        CenterlinePath previewPath = CenterlinePath.CreateSeedPolylinePreview(
            seedSet.Id,
            orderedSeeds,
            existingPath?.Id,
            existingPath?.CreatedUtc);
        _centerlinePaths[previewPath.Id] = previewPath;
    }

    private void ScheduleCenterlineComputation(CenterlineSeedSet seedSet, bool showSuccessToast)
    {
        if (!seedSet.HasRequiredEndpoints)
        {
            return;
        }

        _centerlineComputationCancellation?.Cancel();
        _centerlineComputationCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _centerlineComputationCancellation = cancellation;
        int version = ++_centerlineComputationVersion;
        _ = ComputeCenterlineAsync(seedSet, version, showSuccessToast, cancellation.Token);
    }

    private async Task ComputeCenterlineAsync(CenterlineSeedSet seedSet, int version, bool showSuccessToast, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!TryResolveCenterlineMask(seedSet, out CenterlineSeedSet resolvedSeedSet, out SegmentationMask3D mask, allowMeasurementMaskReconstruction: true))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _centerlineSeedSets[seedSet.Id] = resolvedSeedSet;
                RebuildCenterlinePreviewPath(resolvedSeedSet);
                ScheduleMeasurementSessionSave();

                // ── Auto-segment vessel mask when centerline seeds are ready ─
                if (TryAutoSegmentVesselForCenterline(resolvedSeedSet))
                {
                    ShowToast(
                        "Auto-segmenting vessel mask for centerline…",
                        ToastSeverity.Info,
                        TimeSpan.FromSeconds(3));
                }
                else
                {
                    ShowToast(
                        "Only a seed preview is available. Create or select a 3D vessel mask first, then the real centerline can be computed.",
                        ToastSeverity.Info,
                        TimeSpan.FromSeconds(5));
                }

                RefreshCenterlinePanels();
                UpdateStatus();
            });
            return;
        }

        CenterlineExtractionResult result;
        try
        {
            result = await Task.Run(() => _centerlineExtractionService.Extract(mask, resolvedSeedSet, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            result = CenterlineExtractionResult.Failure("Centerline extraction failed unexpectedly. Try adjusting the vessel mask or adding a guide seed.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _centerlineComputationVersion)
            {
                return;
            }

            _centerlineSeedSets[resolvedSeedSet.Id] = resolvedSeedSet;
            if (result.Succeeded && result.Path is not null)
            {
                CenterlinePath? existingPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == resolvedSeedSet.Id);
                CenterlinePath computedPath = result.Path with
                {
                    Id = existingPath?.Id ?? result.Path.Id,
                    CreatedUtc = existingPath?.CreatedUtc ?? result.Path.CreatedUtc,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };

                _centerlinePaths[computedPath.Id] = computedPath;
                if (showSuccessToast)
                {
                    ShowToast(result.Summary, ToastSeverity.Success, TimeSpan.FromSeconds(4));
                }
            }
            else
            {
                RebuildCenterlinePreviewPath(resolvedSeedSet);
                ShowToast(result.Summary, ToastSeverity.Warning, TimeSpan.FromSeconds(5));
            }

            ScheduleMeasurementSessionSave();
            RecomputeVascularPlanningBundle(resolvedSeedSet.Id, showToast: false);
            RefreshCenterlinePanels();
            UpdateStatus();
            RecordVascularPerformanceMetric("centerline-calculation", stopwatch.Elapsed.TotalMilliseconds);
        });
    }

    private bool TryResolveCenterlineMask(
        CenterlineSeedSet seedSet,
        out CenterlineSeedSet resolvedSeedSet,
        out SegmentationMask3D mask,
        bool allowMeasurementMaskReconstruction = false)
    {
        resolvedSeedSet = seedSet;
        mask = null!;

        if (seedSet.SegmentationMaskId is Guid boundMaskId &&
            _segmentationMasks.TryGetValue(boundMaskId, out SegmentationMask3D? boundMask))
        {
            mask = boundMask;
            return true;
        }

        if (_selectedMeasurementId is Guid selectedMeasurementId)
        {
            StudyMeasurement? selectedMeasurement = _studyMeasurements.FirstOrDefault(measurement => measurement.Id == selectedMeasurementId);
            if (selectedMeasurement?.SegmentationMaskId is Guid selectedMaskId &&
                _segmentationMasks.TryGetValue(selectedMaskId, out SegmentationMask3D? selectedMask))
            {
                resolvedSeedSet = seedSet.BindSegmentationMask(selectedMask.Id);
                mask = selectedMask;
                return true;
            }

            if (allowMeasurementMaskReconstruction &&
                TryResolveMaskFromVolumeRoiMeasurement(selectedMeasurement, seedSet, out resolvedSeedSet, out mask))
            {
                return true;
            }
        }

        if (allowMeasurementMaskReconstruction &&
            TryResolveMaskFromAnyCompatibleVolumeRoiMeasurement(seedSet, out resolvedSeedSet, out mask))
        {
            return true;
        }

        string seedSeriesInstanceUid = seedSet.StartSeed?.SeriesInstanceUid
            ?? seedSet.EndSeed?.SeriesInstanceUid
            ?? string.Empty;
        SegmentationMask3D? matchedMask = _segmentationMasks.Values
            .Where(candidate => string.IsNullOrWhiteSpace(seedSeriesInstanceUid)
                || string.Equals(candidate.SourceSeriesInstanceUid, seedSeriesInstanceUid, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Metadata.ModifiedUtc)
            .FirstOrDefault();

        if (matchedMask is null)
        {
            return false;
        }

        resolvedSeedSet = seedSet.BindSegmentationMask(matchedMask.Id);
        mask = matchedMask;
        return true;
    }

    private bool TryResolveMaskFromVolumeRoiMeasurement(
        StudyMeasurement? measurement,
        CenterlineSeedSet seedSet,
        out CenterlineSeedSet resolvedSeedSet,
        out SegmentationMask3D mask)
    {
        resolvedSeedSet = seedSet;
        mask = null!;

        if (measurement is null ||
            measurement.Kind != MeasurementKind.VolumeRoi ||
            measurement.VolumeContours is not { Length: > 0 } ||
            !TryResolveSeriesVolumeForCenterlineMeasurement(measurement, out SeriesVolume volume) ||
            !TryEnsureMeasurementSegmentationMask(measurement, volume, seedSet, out StudyMeasurement updatedMeasurement, out mask))
        {
            return false;
        }

        resolvedSeedSet = seedSet.BindSegmentationMask(mask.Id);
        if (_selectedMeasurementId == measurement.Id)
        {
            _selectedMeasurementId = updatedMeasurement.Id;
        }

        return true;
    }

    private bool TryResolveMaskFromAnyCompatibleVolumeRoiMeasurement(
        CenterlineSeedSet seedSet,
        out CenterlineSeedSet resolvedSeedSet,
        out SegmentationMask3D mask)
    {
        resolvedSeedSet = seedSet;
        mask = null!;

        foreach (StudyMeasurement measurement in _studyMeasurements
            .Where(candidate => candidate.Kind == MeasurementKind.VolumeRoi && candidate.VolumeContours is { Length: > 0 })
            .Reverse())
        {
            if (!TryResolveMaskFromVolumeRoiMeasurement(measurement, seedSet, out resolvedSeedSet, out mask))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryResolveSeriesVolumeForCenterlineMeasurement(StudyMeasurement measurement, out SeriesVolume volume)
    {
        ViewportSlot? slot = _slots
            .Where(candidate => candidate.Volume is not null)
            .OrderByDescending(candidate => ReferenceEquals(candidate, _activeSlot))
            .FirstOrDefault(candidate => IsMeasurementCompatibleWithVolume(measurement, candidate.Volume!));

        if (slot?.Volume is null)
        {
            volume = null!;
            return false;
        }

        volume = slot.Volume;
        return true;
    }

    private static bool IsMeasurementCompatibleWithVolume(StudyMeasurement measurement, SeriesVolume volume)
    {
        if (string.IsNullOrWhiteSpace(measurement.FrameOfReferenceUid) ||
            !string.Equals(measurement.FrameOfReferenceUid, volume.FrameOfReferenceUid, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(measurement.AcquisitionNumber) ||
            string.IsNullOrWhiteSpace(volume.AcquisitionNumber) ||
            string.Equals(measurement.AcquisitionNumber, volume.AcquisitionNumber, StringComparison.Ordinal);
    }

    private bool TryEnsureMeasurementSegmentationMask(
        StudyMeasurement measurement,
        SeriesVolume volume,
        CenterlineSeedSet seedSet,
        out StudyMeasurement updatedMeasurement,
        out SegmentationMask3D mask)
    {
        updatedMeasurement = measurement;
        mask = null!;

        if (measurement.SegmentationMaskId is Guid existingMaskId &&
            _segmentationMasks.TryGetValue(existingMaskId, out SegmentationMask3D? existingMask))
        {
            mask = existingMask;
            return true;
        }

        if (!TryCreateSegmentationMaskFromVolumeRoiMeasurement(measurement, volume, seedSet, out mask))
        {
            return false;
        }

        updatedMeasurement = measurement.WithSegmentationMask(mask.Id);
        int measurementIndex = _studyMeasurements.FindIndex(candidate => candidate.Id == measurement.Id);
        if (measurementIndex >= 0)
        {
            _studyMeasurements[measurementIndex] = updatedMeasurement;
        }

        _segmentationMasks[mask.Id] = mask;
        return true;
    }

    private static bool TryCreateSegmentationMaskFromVolumeRoiMeasurement(
        StudyMeasurement measurement,
        SeriesVolume volume,
        CenterlineSeedSet seedSet,
        out SegmentationMask3D mask)
    {
        mask = null!;
        if (measurement.VolumeContours is not { Length: > 0 } allContours)
        {
            return false;
        }

        // Clip contours to the range between start and end seeds so the
        // mask only covers the vessel segment the user actually wants.
        VolumeRoiContour[] contours = ClipContoursToSeedRange(allContours, seedSet);

        VolumeGridGeometry geometry = new(
            volume.SizeX,
            volume.SizeY,
            volume.SizeZ,
            volume.SpacingX > 0 ? volume.SpacingX : 1.0,
            volume.SpacingY > 0 ? volume.SpacingY : 1.0,
            volume.SpacingZ > 0 ? volume.SpacingZ : 1.0,
            volume.Origin,
            volume.RowDirection.Normalize(),
            volume.ColumnDirection.Normalize(),
            volume.Normal.Normalize(),
            volume.FrameOfReferenceUid);

        SegmentationMaskBuffer buffer = new(geometry);
        bool wroteForeground = false;

        // Group closed contours by component and sort by plane position so we
        // can interpolate between consecutive drawn contours — filling every
        // integer Z-slice in between.  Without this, slices between drawn
        // contours are left empty and the centerline A* search cannot find a
        // connected path through the mask.
        foreach (IGrouping<int, VolumeRoiContour> componentGroup in contours
            .Where(candidate => candidate.IsClosed && candidate.Anchors.Length >= 3)
            .GroupBy(candidate => candidate.ComponentId))
        {
            List<(VolumeRoiContour Contour, Point[] VoxelPolygon, int VoxelZ)> orderedContours = [];

            foreach (VolumeRoiContour contour in componentGroup.OrderBy(c => c.PlanePosition))
            {
                Point[] voxelPolygon = contour.Anchors
                    .Where(anchor => anchor.PatientPoint is not null)
                    .Select(anchor => volume.PatientToVoxel(anchor.PatientPoint!.Value))
                    .Select(voxel => new Point(voxel.X, voxel.Y))
                    .ToArray();
                if (voxelPolygon.Length < 3)
                {
                    continue;
                }

                double[] voxelZs = contour.Anchors
                    .Where(anchor => anchor.PatientPoint is not null)
                    .Select(anchor => volume.PatientToVoxel(anchor.PatientPoint!.Value).Z)
                    .ToArray();
                if (voxelZs.Length == 0)
                {
                    continue;
                }

                int z = (int)Math.Round(voxelZs.Average());
                if (z < 0 || z >= geometry.SizeZ)
                {
                    continue;
                }

                orderedContours.Add((contour, voxelPolygon, z));
            }

            // Rasterize each drawn contour at its own Z-slice.
            foreach ((_, Point[] voxelPolygon, int z) in orderedContours)
            {
                if (RasterizePolygonSlice(buffer, geometry, voxelPolygon, z))
                {
                    wroteForeground = true;
                }
            }

            // Interpolate between each consecutive pair of drawn contours to
            // fill every integer Z-slice in between.
            for (int ci = 0; ci < orderedContours.Count - 1; ci++)
            {
                (VolumeRoiContour lower, Point[] lowerPoly, int zA) = orderedContours[ci];
                (VolumeRoiContour upper, Point[] upperPoly, int zB) = orderedContours[ci + 1];

                int zMin = Math.Min(zA, zB);
                int zMax = Math.Max(zA, zB);
                if (zMax - zMin <= 1)
                {
                    continue; // adjacent or same slice — no gap
                }

                for (int z = zMin + 1; z < zMax; z++)
                {
                    double t = (z - zMin) / (double)(zMax - zMin);
                    Point[] interpolatedPoly = InterpolateVoxelPolygon(lowerPoly, upperPoly, t);
                    if (interpolatedPoly.Length < 3)
                    {
                        continue;
                    }

                    if (RasterizePolygonSlice(buffer, geometry, interpolatedPoly, z))
                    {
                        wroteForeground = true;
                    }
                }
            }
        }

        if (!wroteForeground)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        mask = new SegmentationMask3D(
            Guid.NewGuid(),
            "3D ROI vessel mask",
            volume.SeriesInstanceUid,
            volume.FrameOfReferenceUid,
            string.Empty,
            geometry,
            buffer.ToStorage(),
            new SegmentationMaskMetadata(
                SegmentationMaskSourceKind.AutoRoi,
                now,
                now,
                sourceMeasurementId: measurement.Id.ToString("D"),
                notes: "Reconstructed from finalized 3D ROI contours for centerline extraction.",
                revision: 0,
                buffer.ComputeStatistics()));
        return true;
    }

    /// <summary>
    /// Clips the contour array to only include contours whose
    /// <see cref="VolumeRoiContour.PlanePosition"/> falls between the start
    /// and end seed positions.  If no seeds are placed, returns all contours.
    /// </summary>
    private static VolumeRoiContour[] ClipContoursToSeedRange(
        VolumeRoiContour[] contours,
        CenterlineSeedSet seedSet)
    {
        if (seedSet.StartSeed is not { } startSeed ||
            seedSet.EndSeed is not { } endSeed ||
            contours.Length == 0)
        {
            return contours;
        }

        SpatialVector3D normal = contours[0].Normal.Normalize();
        if (normal.Length < 0.5)
        {
            return contours;
        }

        SpatialVector3D startPt = startSeed.PatientPoint;
        SpatialVector3D endPt = endSeed.PatientPoint;
        double startPos = startPt.Dot(normal);
        double endPos = endPt.Dot(normal);

        // Add a small margin (≈ 0.6 × average slice spacing) so the end
        // contours aren't trimmed by floating-point rounding.
        double margin = contours.Length >= 2
            ? Math.Abs(contours[^1].PlanePosition - contours[0].PlanePosition) / Math.Max(1, contours.Length - 1) * 0.6
            : 2.0;

        double lo = Math.Min(startPos, endPos) - margin;
        double hi = Math.Max(startPos, endPos) + margin;

        VolumeRoiContour[] clipped = contours
            .Where(c => c.PlanePosition >= lo && c.PlanePosition <= hi)
            .ToArray();

        return clipped.Length > 0 ? clipped : contours;
    }

    /// <summary>
    /// Rasterizes a 2D voxel polygon into the mask buffer at the given Z-slice.
    /// Returns <see langword="true"/> if any foreground voxels were written.
    /// </summary>
    private static bool RasterizePolygonSlice(
        SegmentationMaskBuffer buffer,
        VolumeGridGeometry geometry,
        Point[] voxelPolygon,
        int z)
    {
        if (z < 0 || z >= geometry.SizeZ || voxelPolygon.Length < 3)
        {
            return false;
        }

        int minX = Math.Max(0, (int)Math.Floor(voxelPolygon.Min(point => point.X)));
        int maxX = Math.Min(geometry.SizeX - 1, (int)Math.Ceiling(voxelPolygon.Max(point => point.X)));
        int minY = Math.Max(0, (int)Math.Floor(voxelPolygon.Min(point => point.Y)));
        int maxY = Math.Min(geometry.SizeY - 1, (int)Math.Ceiling(voxelPolygon.Max(point => point.Y)));
        if (minX > maxX || minY > maxY)
        {
            return false;
        }

        bool wrote = false;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsPointInsideVoxelPolygon(new Point(x + 0.5, y + 0.5), voxelPolygon))
                {
                    continue;
                }

                buffer.Set(x, y, z, true);
                wrote = true;
            }
        }

        return wrote;
    }

    /// <summary>
    /// Linearly interpolates between two voxel-space polygons at parameter
    /// <paramref name="t"/> (0 = <paramref name="lower"/>, 1 = <paramref name="upper"/>).
    /// When the polygons have different vertex counts the shorter one is
    /// resampled to match the longer.
    /// </summary>
    private static Point[] InterpolateVoxelPolygon(Point[] lower, Point[] upper, double t)
    {
        if (lower.Length == 0 || upper.Length == 0)
        {
            return [];
        }

        // If vertex counts differ, resample the shorter polygon to match.
        if (lower.Length != upper.Length)
        {
            int targetCount = Math.Max(lower.Length, upper.Length);
            if (lower.Length < targetCount)
            {
                lower = ResamplePolygon(lower, targetCount);
            }

            if (upper.Length < targetCount)
            {
                upper = ResamplePolygon(upper, targetCount);
            }
        }

        Point[] result = new Point[lower.Length];
        for (int i = 0; i < lower.Length; i++)
        {
            result[i] = new Point(
                lower[i].X + ((upper[i].X - lower[i].X) * t),
                lower[i].Y + ((upper[i].Y - lower[i].Y) * t));
        }

        return result;
    }

    /// <summary>
    /// Resamples a closed polygon to have exactly <paramref name="count"/>
    /// equally-spaced vertices.
    /// </summary>
    private static Point[] ResamplePolygon(Point[] polygon, int count)
    {
        if (polygon.Length < 2 || count < 3)
        {
            return polygon;
        }

        double[] cumulative = new double[polygon.Length + 1];
        for (int i = 0; i < polygon.Length; i++)
        {
            double dx = polygon[(i + 1) % polygon.Length].X - polygon[i].X;
            double dy = polygon[(i + 1) % polygon.Length].Y - polygon[i].Y;
            cumulative[i + 1] = cumulative[i] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return polygon;
        }

        Point[] result = new Point[count];
        double step = totalLength / count;
        int segmentIndex = 0;
        for (int i = 0; i < count; i++)
        {
            double target = i * step;
            while (segmentIndex < polygon.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segStart = cumulative[segmentIndex];
            double segEnd = cumulative[segmentIndex + 1];
            double segLen = Math.Max(double.Epsilon, segEnd - segStart);
            double u = (target - segStart) / segLen;
            int next = (segmentIndex + 1) % polygon.Length;
            result[i] = new Point(
                polygon[segmentIndex].X + ((polygon[next].X - polygon[segmentIndex].X) * u),
                polygon[segmentIndex].Y + ((polygon[next].Y - polygon[segmentIndex].Y) * u));
        }

        return result;
    }

    private static bool IsPointInsideVoxelPolygon(Point point, IReadOnlyList<Point> polygon)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            Point currentPoint = polygon[index];
            Point previousPoint = polygon[previous];
            bool intersects = ((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y)) &&
                              (point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / ((previousPoint.Y - currentPoint.Y) + double.Epsilon)) + currentPoint.X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private void OnSegmentationMaskChangedForCenterline(Guid segmentationMaskId)
    {
        foreach (CenterlineSeedSet seedSet in _centerlineSeedSets.Values.ToArray())
        {
            if (seedSet.SegmentationMaskId == segmentationMaskId)
            {
                ScheduleCenterlineComputation(seedSet, showSuccessToast: false);
                RecomputeVascularPlanningBundle(seedSet.Id, showToast: false);
            }
        }
    }

    private string BuildCenterlineStatusText()
    {
        int seedSetCount = _centerlineSeedSets.Count;
        if (!_isCenterlineEditMode)
        {
            return seedSetCount == 0
                ? string.Empty
                : $"   Centerline seeds: {seedSetCount} set{(seedSetCount == 1 ? string.Empty : "s")} saved";
        }

        CenterlineSeedSet seedSet = EnsureActiveCenterlineSeedSet();
        CenterlinePath? previewPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == seedSet.Id);
        bool hasResolvedMask = TryResolveCenterlineMask(seedSet, out _, out _);
        string pathText = previewPath is null
            ? "no preview path"
            : previewPath.Kind == CenterlinePathKind.Computed
                ? $"computed {previewPath.TotalLengthMm:F1} mm, q={previewPath.QualityScore:0.00}"
                : previewPath.HasRenderablePath
                    ? hasResolvedMask
                        ? $"preview {previewPath.TotalLengthMm:F1} mm"
                        : $"preview only {previewPath.TotalLengthMm:F1} mm (no vessel mask)"
                    : "preview seed only";

        return $"   Centerline: click {seedSet.PendingSeedLabel}; CTRL=guide, SHIFT=start, ALT=end, Backspace removes last, Esc exits ({seedSet.SeedCount} seed{(seedSet.SeedCount == 1 ? string.Empty : "s")}, {pathText})";
    }

    // ── Auto-segment vessel mask when centerline seeds are placed ──────────
    // When start + end are placed but no 3D mask exists yet, run the GPU 3D
    // auto-segmentation from BOTH seed positions (which are on the vessel).
    // The regions are unioned — if they connect, the mask spans the full
    // vessel between start and end.  On success the mask fires through the
    // normal event chain → centerline extraction → curved MPR.

    private bool TryAutoSegmentVesselForCenterline(CenterlineSeedSet seedSet)
    {
        if (seedSet.StartSeed is not { } startSeed || seedSet.EndSeed is not { } endSeed)
        {
            return false;
        }

        if (_activeSlot?.Panel is not DicomViewPanel panel ||
            !panel.IsVolumeBound)
        {
            return false;
        }

        // Pass the actual patient-space seed positions directly.
        // These are the 3D points where the user clicked — guaranteed to be
        // on the vessel.  No lossy image-plane projection needed.
        Models.Vector3D[] seedPatientPoints = [startSeed.PatientPoint, endSeed.PatientPoint];

        Guid seedSetId = seedSet.Id;
        return panel.TryAutoSegmentForCenterline(seedPatientPoints, result =>
        {
            if (result.Succeeded && result.Mask is not null)
            {
                // Bind the mask to the seed set so subsequent centerline computations find it.
                if (_centerlineSeedSets.TryGetValue(seedSetId, out CenterlineSeedSet? current))
                {
                    CenterlineSeedSet bound = current.BindSegmentationMask(result.Mask.Id);
                    _centerlineSeedSets[seedSetId] = bound;
                    // Re-schedule now that binding is in place.
                    ScheduleCenterlineComputation(bound, showSuccessToast: true);
                    ScheduleMeasurementSessionSave();
                }

                ShowToast(result.Message, ToastSeverity.Success, TimeSpan.FromSeconds(4));
            }
            else
            {
                ShowToast(
                    $"Auto vessel segmentation failed — place a 3D ROI manually.\n{result.Message}",
                    ToastSeverity.Warning,
                    TimeSpan.FromSeconds(6));
            }
        });
    }
}