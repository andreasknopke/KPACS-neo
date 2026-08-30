using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.RoiDraft;
using KPACS.Viewer.Services;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    public sealed record AutoOutlinedMeasurementInfo(Guid MeasurementId, MeasurementKind Kind, Point SeedPoint, int SensitivityLevel);
    public sealed record AutoOutlineAttemptInfo(MeasurementKind Kind, Point SeedPoint, int SensitivityLevel, bool Succeeded, string Message);
    public sealed record AutoOutlinePreviewResult(
        bool Succeeded,
        AutoOutlineDeveloperSettings Settings,
        VolumeRoiContour[] Contours,
        SegmentationMask3D? Mask,
        string Message,
        long ElapsedMilliseconds);
    public sealed record AutoOutlineDeveloperSettings(
        bool UseRobustHomogenization = true,
        double TwoDimensionalToleranceScale = 2.6,
        double VolumeToleranceScale = 2.6,
        double SliceSignatureToleranceScale = 1.0,
        double SeedNeighborhoodRadiusMm = 2.0,
        int PolygonPointBudget = 20,
        int VolumeContourPointBudget = 20);

    private static readonly int[] s_autoOutlineKernel = [1, 4, 6, 4, 1];
    private const int AutoOutlineMinPixels = 18;
    private const int AutoOutlineMaxPixels = 32000;
    private const int AutoOutlineMaxVoxelCount = 180000;
    private const int AutoOutlineMaxSeedCandidates = 7;
    private const int AutoOutlineMaxVisitedVoxelsPerSeed = 60000;
    private const int AutoOutlineSlicePropagationMaxMisses = 4;
    private const int AutoOutlineSlicePropagationSeedSampleCount = 10;
    private const int AutoOutlineParallelCandidateThreshold = 4;
    private const int AutoOutlineIntensitySignatureTargetSamples = 4096;
    private const int AutoOutlineDirectionParallelismMinCpuCount = 4;
    private const double AutoOutlineSeedNeighborhoodRadiusMm = 4.5;
    private const double AutoOutlineAdaptiveStdDevWeight = 2.35;
    private const double AutoOutlineMrToleranceMultiplier = 1.35;
    private const double AutoOutlineRelaxedToleranceMultiplier = 1.22;
    private const double AutoOutlineCtPositiveOutlierMadMultiplier = 2.1;
    private const double AutoOutlineCtNegativeOutlierMadMultiplier = 3.2;
    private const double AutoOutlineGenericOutlierMadMultiplier = 2.8;
    private const int AutoOutlineMinSensitivityLevel = -6;
    private const int AutoOutlineMaxSensitivityLevel = 6;

    private const int AutoOutlineSlicePropagationSkipVolumeGrowMinContours = 3;

    public event Action<AutoOutlinedMeasurementInfo>? AutoOutlinedMeasurementCreated;
    public event Action<AutoOutlineAttemptInfo>? AutoOutlineAttempted;
    private bool _autoOutlineInProgress;
    private bool? _cachedIsMrLike;
    private bool? _cachedIsCtLike;

    /// <summary>
    /// Result of a headless auto-segmentation run (no draft, no finalization).
    /// Used by the centerline pipeline to create a 3D vessel mask programmatically.
    /// </summary>
    public sealed record AutoSegmentResult(bool Succeeded, VolumeRoiContour[] Contours, SegmentationMask3D? Mask, string Message);

    /// <summary>
    /// Runs GPU/CPU 3D auto-segmentation from each of the <paramref name="seedPatientPoints"/>
    /// (the centerline start and end positions in patient space), unions their
    /// voxel regions, builds contours and a <see cref="SegmentationMask3D"/>, then
    /// finalizes a <see cref="StudyMeasurement"/> without touching the draft UI.
    /// The <paramref name="callback"/> is posted to the UI thread.
    /// </summary>
    public bool TryAutoSegmentForCenterline(SpatialVector3D[] seedPatientPoints, Action<AutoSegmentResult> callback)
    {
        if (_autoOutlineInProgress || _volume is null || SpatialMetadata is null || seedPatientPoints.Length == 0)
        {
            return false;
        }

        _autoOutlineInProgress = true;

        _ = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                HashSet<int> combinedRegion = [];
                string lastGpuLog = string.Empty;
                int succeededSeeds = 0;

                foreach (SpatialVector3D seedPatient in seedPatientPoints)
                {
                    if (TrySegmentVolumeSeedFromPatient(seedPatient, sensitivityLevel: 0, out HashSet<int> region, out _))
                    {
                        combinedRegion.UnionWith(region);
                        succeededSeeds++;
                    }

                    if (!string.IsNullOrEmpty(_lastGpuSegmentLog))
                    {
                        lastGpuLog = _lastGpuSegmentLog;
                    }
                }

                if (succeededSeeds == 0 || combinedRegion.Count < AutoOutlineMinPixels * 2)
                {
                    sw.Stop();
                    Console.Error.WriteLine($"[AutoSegForCenterline] FAILED {succeededSeeds}/{seedPatientPoints.Length} seeds, {combinedRegion.Count} voxels in {sw.ElapsedMilliseconds}ms {lastGpuLog}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        _autoOutlineInProgress = false;
                        string detail = string.IsNullOrEmpty(lastGpuLog)
                            ? $"Auto vessel segmentation could not grow a viable region from {seedPatientPoints.Length} seed(s)."
                            : $"Auto vessel segmentation could not grow a viable region from {seedPatientPoints.Length} seed(s).\n{lastGpuLog}";
                        callback(new AutoSegmentResult(false, [], null, detail));
                    });
                    return;
                }

                SegmentationMask3D mask = CreateSegmentationMaskFromRegion(combinedRegion);
                VolumeRoiContour[] contours = BuildAutoOutlinedVolumeContours(combinedRegion);
                sw.Stop();

                if (contours.Length == 0)
                {
                    Console.Error.WriteLine($"[AutoSegForCenterline] {combinedRegion.Count} voxels but 0 contours in {sw.ElapsedMilliseconds}ms");
                    Dispatcher.UIThread.Post(() =>
                    {
                        _autoOutlineInProgress = false;
                        callback(new AutoSegmentResult(false, [], null, "Auto vessel segmentation produced voxels but no usable contours."));
                    });
                    return;
                }

                string gpuLog = lastGpuLog;
                Console.Error.WriteLine($"[AutoSegForCenterline] OK {succeededSeeds}/{seedPatientPoints.Length} seeds, {combinedRegion.Count} voxels, {contours.Length} slices in {sw.ElapsedMilliseconds}ms {gpuLog}");

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        // Directly create the finalized measurement + fire events — no draft involved.
                        StudyMeasurement measurement = StudyMeasurement.CreateVolumeRoi(FilePath, SpatialMetadata!, contours, mask.Id);
                        SegmentationMask3D finalMask = mask with
                        {
                            Metadata = mask.Metadata with
                            {
                                SourceMeasurementId = measurement.Id.ToString("D"),
                            }
                        };
                        SegmentationMaskCreated?.Invoke(finalMask);
                        MeasurementCreated?.Invoke(measurement);
                        SetSelectedMeasurement(measurement.Id);
                        UpdateMeasurementPresentation();

                        string toastMsg = string.IsNullOrEmpty(gpuLog)
                            ? $"Auto vessel mask: {contours.Length} slice(s), {combinedRegion.Count} voxels from {succeededSeeds} seed(s) in {sw.ElapsedMilliseconds}ms."
                            : $"Auto vessel mask: {contours.Length} slice(s), {combinedRegion.Count} voxels from {succeededSeeds} seed(s) in {sw.ElapsedMilliseconds}ms.\n{gpuLog}";
                        callback(new AutoSegmentResult(true, contours, finalMask, toastMsg));
                    }
                    finally
                    {
                        _autoOutlineInProgress = false;
                    }
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                Exception root = ex is AggregateException agg ? agg.Flatten().InnerExceptions.FirstOrDefault() ?? ex : ex;
                string detail = $"{root.GetType().Name}: {root.Message}";
                Console.Error.WriteLine($"[AutoSegForCenterline] EXCEPTION in {sw.ElapsedMilliseconds}ms: {detail}");
                Dispatcher.UIThread.Post(() =>
                {
                    _autoOutlineInProgress = false;
                    callback(new AutoSegmentResult(false, [], null, detail));
                });
            }
        });

        return true;
    }
    private AutoOutlineDeveloperSettings _autoOutlineDeveloperSettings = new();
    private readonly AsyncLocal<AutoOutlineDeveloperSettings?> _autoOutlineDeveloperSettingsOverride = new();

    private AutoOutlineDeveloperSettings CurrentAutoOutlineDeveloperSettings => _autoOutlineDeveloperSettingsOverride.Value ?? _autoOutlineDeveloperSettings;

    public AutoOutlineDeveloperSettings GetAutoOutlineDeveloperSettings() => _autoOutlineDeveloperSettings with { };

    public bool TryGetCurrentVolumeRoiAutoOutlineParameters(out Point imagePoint, out int sensitivityLevel)
    {
        if (_volumeRoiDraft?.AutoOutlineState is not { } autoOutlineState)
        {
            imagePoint = default;
            sensitivityLevel = 0;
            return false;
        }

        imagePoint = autoOutlineState.ImagePoint;
        sensitivityLevel = autoOutlineState.SensitivityLevel;
        return true;
    }

    public AutoOutlinePreviewResult PreviewAutoOutlinedVolumeContours(Point imagePoint, int sensitivityLevel, AutoOutlineDeveloperSettings settings)
    {
        AutoOutlineDeveloperSettings normalized = NormalizeAutoOutlineDeveloperSettings(settings);
        if (_volume is null || SpatialMetadata is null)
        {
            return new AutoOutlinePreviewResult(false, normalized, [], null, "A loaded 3D volume is required to preview ROI settings.", 0);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using AutoOutlineDeveloperSettingsOverrideScope _ = PushAutoOutlineDeveloperSettingsOverride(normalized);
        if (!TryBuildAutoOutlinedVolumeContours(imagePoint, sensitivityLevel, out VolumeRoiContour[] contours, out SegmentationMask3D? segmentationMask, out string failureReason))
        {
            sw.Stop();
            string gpuLog = _lastGpuSegmentLog;
            string detail = string.IsNullOrWhiteSpace(gpuLog)
                ? failureReason
                : $"{failureReason}\n{gpuLog}";
            return new AutoOutlinePreviewResult(false, normalized, [], null, detail, sw.ElapsedMilliseconds);
        }

        sw.Stop();
        string successGpuLog = _lastGpuSegmentLog;
        string message = string.IsNullOrWhiteSpace(successGpuLog)
            ? $"Preview generated {contours.Length} contour slice(s)."
            : $"Preview generated {contours.Length} contour slice(s).\n{successGpuLog}";
        return new AutoOutlinePreviewResult(true, normalized, contours, segmentationMask, message, sw.ElapsedMilliseconds);
    }

    public bool SetAutoOutlineDeveloperSettings(AutoOutlineDeveloperSettings settings, bool rerunVolumeDraft)
    {
        AutoOutlineDeveloperSettings normalized = NormalizeAutoOutlineDeveloperSettings(settings);
        bool changed = !_autoOutlineDeveloperSettings.Equals(normalized);
        _autoOutlineDeveloperSettings = normalized;

        if (rerunVolumeDraft && _volumeRoiDraft?.AutoOutlineState is { } autoOutlineState)
        {
            return TryCreateAutoOutlinedVolumeRoiDraft(autoOutlineState.ImagePoint, autoOutlineState.SensitivityLevel) || changed;
        }

        if (changed && _volumeRoiDraft is not null)
        {
            NotifyVolumeRoiDraftChanged();
        }

        return changed;
    }

    public bool TryRerunCurrentVolumeRoiAutoOutline()
    {
        return _volumeRoiDraft?.AutoOutlineState is { } autoOutlineState &&
            TryCreateAutoOutlinedVolumeRoiDraft(autoOutlineState.ImagePoint, autoOutlineState.SensitivityLevel);
    }

    private bool TryCreateAutoOutlinedPolygonMeasurement(Point imagePoint, int sensitivityLevel = 0)
    {
        if (!TryCreateAutoOutlinedPolygon(imagePoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel, false, "Auto-outline could not isolate a stable 2D region from this seed."));
            return false;
        }

        _measurementDraft = null;
        SetSelectedMeasurement(null);
        StudyMeasurement measurement = StudyMeasurement.Create(MeasurementKind.PolygonRoi, FilePath, SpatialMetadata, polygonPoints);
        SetSelectedMeasurement(measurement.Id);
        MeasurementCreated?.Invoke(measurement);
        AutoOutlinedMeasurementCreated?.Invoke(new AutoOutlinedMeasurementInfo(measurement.Id, MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel));
        AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel, true, "2D auto-outline created."));
        UpdateMeasurementPresentation();
        PushRoiHistory(
            "Create auto-outlined polygon ROI",
            () => ApplyMeasurementDeletedFromHistory(measurement.Id),
            () => ApplyMeasurementCreatedFromHistory(measurement));
        return true;
    }

    public bool TryRefineAutoOutlinedPolygonMeasurement(
        StudyMeasurement measurement,
        Point seedPoint,
        int sensitivityLevel,
        out StudyMeasurement updatedMeasurement)
    {
        updatedMeasurement = measurement;
        if (measurement.Kind != MeasurementKind.PolygonRoi || SpatialMetadata is null)
        {
            return false;
        }

        if (!TryCreateAutoOutlinedPolygon(seedPoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            return false;
        }

        updatedMeasurement = measurement.WithAnchors(SpatialMetadata, polygonPoints);
        return true;
    }

    private bool TryCreateAutoOutlinedPolygon(Point imagePoint, out Point[] polygonPoints, int sensitivityLevel = 0)
    {
        polygonPoints = [];

        int seedX = Math.Clamp((int)Math.Round(imagePoint.X), 0, _imageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, _imageHeight - 1);
        if (!TryBuildAutoOutlineMask(seedX, seedY, sensitivityLevel, out AutoOutlineMask mask))
        {
            return false;
        }

        polygonPoints = TraceAutoOutlineBoundary(mask, maxPointCount: CurrentAutoOutlineDeveloperSettings.PolygonPointBudget)
            .Select(point => new Point(point.X + mask.Left, point.Y + mask.Top))
            .ToArray();
        return polygonPoints.Length >= 3;
    }

    private bool TryCreateAutoOutlinedPolygon(
        AutoOutlineSliceSource source,
        Point imagePoint,
        out Point[] polygonPoints,
        int sensitivityLevel = 0)
    {
        polygonPoints = [];

        int seedX = Math.Clamp((int)Math.Round(imagePoint.X), 0, source.Width - 1);
        int seedY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, source.Height - 1);
        if (!TryBuildAutoOutlineMask(source, seedX, seedY, sensitivityLevel, out AutoOutlineMask mask))
        {
            return false;
        }

        polygonPoints = TraceAutoOutlineBoundary(mask, maxPointCount: CurrentAutoOutlineDeveloperSettings.PolygonPointBudget)
            .Select(point => new Point(point.X + mask.Left, point.Y + mask.Top))
            .ToArray();
        return polygonPoints.Length >= 3;
    }

    private bool TryCreateAutoOutlinedPolygon(
        SlicePropagationSliceData sliceData,
        Point imagePoint,
        out Point[] polygonPoints,
        int sensitivityLevel = 0)
    {
        polygonPoints = Array.Empty<Point>();

        int seedX = Math.Clamp((int)Math.Round(imagePoint.X), 0, sliceData.Source.Width - 1);
        int seedY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, sliceData.Source.Height - 1);
        if (!TryBuildAutoOutlineMask(sliceData, seedX, seedY, sensitivityLevel, out AutoOutlineMask mask))
        {
            return false;
        }

        polygonPoints = TraceAutoOutlineBoundary(mask, maxPointCount: CurrentAutoOutlineDeveloperSettings.PolygonPointBudget)
            .Select(point => new Point(point.X + mask.Left, point.Y + mask.Top))
            .ToArray();
        return polygonPoints.Length >= 3;
    }

    private bool TryCreateAutoOutlinedVolumeRoiDraft(Point imagePoint, int sensitivityLevel = 0)
    {
        if (_autoOutlineInProgress)
        {
            return false;
        }

        if (_volume is null || SpatialMetadata is null)
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, ClampImagePoint(imagePoint), sensitivityLevel, false, "Auto 3D ROI is unavailable because no 3D volume is loaded."));
            return false;
        }

        VolumeRoiDraft? previousDraft = CloneVolumeRoiDraftState();
        _autoOutlineInProgress = true;
        Point clampedPoint = ClampImagePoint(imagePoint);

        // Capture references needed on the background thread.
        SeriesVolume volume = _volume;
        DicomSpatialMetadata spatialMetadata = SpatialMetadata;

        _ = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!TryBuildAutoOutlinedVolumeContours(imagePoint, sensitivityLevel, out VolumeRoiContour[] contours, out SegmentationMask3D? segmentationMask, out string failureReason))
                {
                    sw.Stop();
                    string gpuLog = _lastGpuSegmentLog;
                    Console.Error.WriteLine($"[AutoOutline] FAILED in {sw.ElapsedMilliseconds}ms: {failureReason} {gpuLog}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        _autoOutlineInProgress = false;
                        string detail = string.IsNullOrEmpty(gpuLog) ? failureReason : $"{failureReason}\n{gpuLog}";
                        AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, false, detail));
                    });
                    return;
                }

                sw.Stop();
                string successGpuLog = _lastGpuSegmentLog;
                Console.Error.WriteLine($"[AutoOutline] OK in {sw.ElapsedMilliseconds}ms — {contours.Length} contours {successGpuLog}");
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        ApplyAutoOutlinedVolumeContours(clampedPoint, sensitivityLevel, contours, segmentationMask);
                        string toastMsg = string.IsNullOrEmpty(successGpuLog)
                            ? $"Auto 3D ROI created {contours.Length} contour slice(s) in {sw.ElapsedMilliseconds}ms."
                            : $"Auto 3D ROI created {contours.Length} slice(s) in {sw.ElapsedMilliseconds}ms.\n{successGpuLog}";
                        AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, true, toastMsg));
                        RecordVolumeRoiDraftTransition("Create auto-outlined volume ROI draft", previousDraft, _volumeRoiDraft);
                    }
                    finally
                    {
                        _autoOutlineInProgress = false;
                    }
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                Exception root = ex is AggregateException agg ? agg.Flatten().InnerExceptions.FirstOrDefault() ?? ex : ex;
                string detail = $"{root.GetType().Name}: {root.Message}";
                Console.Error.WriteLine($"[AutoOutline] EXCEPTION in {sw.ElapsedMilliseconds}ms: {detail}");
                Dispatcher.UIThread.Post(() =>
                {
                    _autoOutlineInProgress = false;
                    AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, false, $"Auto 3D ROI failed ({detail})"));
                });
            }
        });

        return true;
    }

    private bool TryBuildAutoOutlinedVolumeContours(
        Point imagePoint,
        int sensitivityLevel,
        out VolumeRoiContour[] contours,
        out SegmentationMask3D? segmentationMask,
        out string failureReason)
    {
        contours = [];
        segmentationMask = null;
        failureReason = "Auto 3D ROI could not isolate a stable volume around this seed.";

        // ── Fast path: GPU / CPU volume seed segmentation ────────────────────
        // Try the 3D region-growing path first — when a GPU is available this
        // completes in <200 ms.  Only fall back to the much slower slice-
        // propagation path if volume seed produces no usable contours.
        string regionFailureReason = string.Empty;
        if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, out HashSet<int> region, out regionFailureReason))
        {
            segmentationMask = CreateSegmentationMaskFromRegion(region);
            VolumeRoiContour[] regionContours = BuildAutoOutlinedVolumeContours(region);
            if (regionContours.Length > 0)
            {
                contours = regionContours;
                return true;
            }

            failureReason = "Auto 3D ROI segmented voxels, but no usable slice contours could be reconstructed.";
            // Fall through to slice-propagation if available.
        }

        // ── Slow fallback: 2D slice propagation ─────────────────────────────
        // Reslices the volume and runs 2D auto-outline per slice.  This is
        // expensive but may succeed in cases the 3D seed path cannot handle.
        if (CanUseSlicePropagatedAutoOutline())
        {
            if (TryBuildSlicePropagatedVolumeContours(imagePoint, sensitivityLevel, out VolumeRoiContour[] propagatedContours, out string slicePropagationFailureMessage))
            {
                if (propagatedContours.Length > 0)
                {
                    contours = propagatedContours;
                    return true;
                }
            }
            else
            {
                failureReason = string.IsNullOrEmpty(regionFailureReason)
                    ? slicePropagationFailureMessage
                    : $"{regionFailureReason} Slice propagation: {slicePropagationFailureMessage}";
            }
        }
        else if (!string.IsNullOrEmpty(regionFailureReason))
        {
            failureReason = regionFailureReason;
        }

        return contours.Length > 0;
    }

    private void ApplyAutoOutlinedVolumeContours(Point imagePoint, int sensitivityLevel, VolumeRoiContour[] contours, SegmentationMask3D? segmentationMask)
    {
        _measurementDraft = null;
        SetSelectedMeasurement(null);

        bool appendToExisting = _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice() && _volumeRoiDraft.AdditiveModeEnabled;
        VolumeRoiDraft draft;
        if (appendToExisting)
        {
            draft = _volumeRoiDraft!;
            draft.SegmentationMask = null;
            draft.PendingAddContour = null;
            draft.AutoOutlineState = new VolumeRoiAutoOutlineState(imagePoint, sensitivityLevel);
            Dictionary<int, int> componentMap = [];
            int? firstMappedComponentId = null;
            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
            {
                if (!componentMap.TryGetValue(contour.ComponentId, out int componentId))
                {
                    componentId = draft.NextComponentId++;
                    componentMap[contour.ComponentId] = componentId;
                    firstMappedComponentId ??= componentId;
                }

                string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
                string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
                draft.Contours[contourKey] = new VolumeRoiDraftContour(
                    sliceKey,
                    contourKey,
                    componentId,
                    contour.SourceFilePath,
                    contour.ReferencedSopInstanceUid,
                    contour.PlaneOrigin,
                    contour.RowDirection,
                    contour.ColumnDirection,
                    contour.Normal,
                    contour.PlanePosition,
                    contour.RowSpacing,
                    contour.ColumnSpacing,
                    [.. contour.Anchors],
                    contour.IsClosed);
            }

            draft.ActiveAddComponentId = componentMap.Count == 1 ? firstMappedComponentId : null;
        }
        else
        {
            draft = new VolumeRoiDraft(
                SpatialMetadata!.SeriesInstanceUid,
                SpatialMetadata.FrameOfReferenceUid,
                SpatialMetadata.AcquisitionNumber,
                SpatialMetadata.Normal.Normalize(),
                GetPlanePosition(SpatialMetadata),
                FilePath,
                SpatialMetadata.SopInstanceUid)
            {
                AutoOutlineState = new VolumeRoiAutoOutlineState(imagePoint, sensitivityLevel),
                AdditiveModeEnabled = _volumeRoiDraft?.AdditiveModeEnabled == true,
                SegmentationMask = segmentationMask,
            };

            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
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
                    [.. contour.Anchors],
                    contour.IsClosed);
            }

            draft.NextComponentId = Math.Max(1, contours.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);
            draft.ActiveAddComponentId = null;
            _volumeRoiDraft = draft;
        }

        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
    }

    private SegmentationMask3D CreateSegmentationMaskFromRegion(HashSet<int> region)
    {
        if (_volume is null)
        {
            throw new InvalidOperationException("A 3D volume is required to create a segmentation mask.");
        }

        VolumeGridGeometry geometry = new(
            _volume.SizeX,
            _volume.SizeY,
            _volume.SizeZ,
            _volume.SpacingX > 0 ? _volume.SpacingX : 1.0,
            _volume.SpacingY > 0 ? _volume.SpacingY : 1.0,
            _volume.SpacingZ > 0 ? _volume.SpacingZ : 1.0,
            _volume.Origin,
            _volume.RowDirection.Normalize(),
            _volume.ColumnDirection.Normalize(),
            _volume.Normal.Normalize(),
            _volume.FrameOfReferenceUid);

        SegmentationMaskBuffer buffer = new(geometry);
        foreach (int key in region)
        {
            DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
            buffer.Set(x, y, z, true);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SegmentationMask3D(
            Guid.NewGuid(),
            "Auto 3D ROI",
            _volume.SeriesInstanceUid,
            _volume.FrameOfReferenceUid,
            string.Empty,
            geometry,
            buffer.ToStorage(),
            new SegmentationMaskMetadata(
                SegmentationMaskSourceKind.AutoRoi,
                now,
                now,
                sourceMeasurementId: null,
                notes: "Created from auto 3D ROI volume segmentation.",
                revision: 0,
                buffer.ComputeStatistics()));
    }

    private bool TryBuildAutoOutlineMask(int seedX, int seedY, int sensitivityLevel, out AutoOutlineMask mask)
    {
        mask = default;
        if (!TryGetPixelValue(seedX, seedY, out double seedValue))
        {
            return false;
        }

        int radius = Math.Clamp(Math.Max(_imageWidth, _imageHeight) / 5, 36, 120);
        int left = Math.Max(0, seedX - radius);
        int top = Math.Max(0, seedY - radius);
        int right = Math.Min(_imageWidth - 1, seedX + radius);
        int bottom = Math.Min(_imageHeight - 1, seedY + radius);
        int width = right - left + 1;
        int height = bottom - top + 1;
        if (width <= 2 || height <= 2)
        {
            return false;
        }

        int localSeedX = seedX - left;
        int localSeedY = seedY - top;
        double[,] homogenizedValues = BuildHomogenizedPixelWindow(left, top, width, height);
        double homogenizedSeedValue = homogenizedValues[localSeedX, localSeedY];
        if (!TryComputeAutoOutlineTolerance(seedX, seedY, seedValue, homogenizedSeedValue, sensitivityLevel, out double localMean, out double tolerance))
        {
            return false;
        }

        bool[,] included = new bool[width, height];
        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((localSeedX, localSeedY));
        visited[localSeedX, localSeedY] = true;

        int count = 0;
        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            int imageX = x + left;
            int imageY = y + top;
            double homogenizedValue = homogenizedValues[x, y];
            if (!TryGetPixelValue(imageX, imageY, out double rawValue))
            {
                rawValue = homogenizedValue;
            }

            if (!IsAutoOutlinePixelAccepted(homogenizedValue, rawValue, homogenizedSeedValue, seedValue, localMean, tolerance))
            {
                continue;
            }

            included[x, y] = true;
            count++;
            if (count > AutoOutlineMaxPixels)
            {
                return false;
            }

            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        bool[,] cleanedMask = SmoothAutoOutlineMask(included, localSeedX, localSeedY, out count);
        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        mask = new AutoOutlineMask(left, top, cleanedMask, count);
        return true;
    }

    private bool TryBuildAutoOutlineMask(AutoOutlineSliceSource source, int seedX, int seedY, int sensitivityLevel, out AutoOutlineMask mask)
    {
        mask = default;
        if (!TryGetSlicePixelValue(source, seedX, seedY, out double seedValue))
        {
            return false;
        }

        int radius = Math.Clamp(Math.Max(source.Width, source.Height) / 5, 36, 120);
        int left = Math.Max(0, seedX - radius);
        int top = Math.Max(0, seedY - radius);
        int right = Math.Min(source.Width - 1, seedX + radius);
        int bottom = Math.Min(source.Height - 1, seedY + radius);
        int width = right - left + 1;
        int height = bottom - top + 1;
        if (width <= 2 || height <= 2)
        {
            return false;
        }

        int localSeedX = seedX - left;
        int localSeedY = seedY - top;
        double[,] homogenizedValues = BuildHomogenizedPixelWindow(source, left, top, width, height);
        double homogenizedSeedValue = homogenizedValues[localSeedX, localSeedY];
        if (!TryComputeAutoOutlineTolerance(source, seedX, seedY, seedValue, homogenizedSeedValue, sensitivityLevel, out double localMean, out double tolerance))
        {
            return false;
        }

        bool[,] included = new bool[width, height];
        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((localSeedX, localSeedY));
        visited[localSeedX, localSeedY] = true;

        int count = 0;
        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            int imageX = x + left;
            int imageY = y + top;
            double homogenizedValue = homogenizedValues[x, y];
            if (!TryGetSlicePixelValue(source, imageX, imageY, out double rawValue))
            {
                rawValue = homogenizedValue;
            }

            if (!IsAutoOutlinePixelAccepted(homogenizedValue, rawValue, homogenizedSeedValue, seedValue, localMean, tolerance))
            {
                continue;
            }

            included[x, y] = true;
            count++;
            if (count > AutoOutlineMaxPixels)
            {
                return false;
            }

            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        bool[,] cleanedMask = SmoothAutoOutlineMask(included, localSeedX, localSeedY, out count);
        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        mask = new AutoOutlineMask(left, top, cleanedMask, count);
        return true;
    }

    private bool TryBuildAutoOutlineMask(SlicePropagationSliceData sliceData, int seedX, int seedY, int sensitivityLevel, out AutoOutlineMask mask)
    {
        return TryBuildAutoOutlineMask(sliceData.Source, seedX, seedY, sensitivityLevel, out mask);
    }

    private bool TryComputeAutoOutlineTolerance(int seedX, int seedY, double seedValue, double homogenizedSeedValue, int sensitivityLevel, out double mean, out double tolerance)
    {
        List<double> values = [];
        for (int y = Math.Max(0, seedY - 3); y <= Math.Min(_imageHeight - 1, seedY + 3); y++)
        {
            for (int x = Math.Max(0, seedX - 3); x <= Math.Min(_imageWidth - 1, seedX + 3); x++)
            {
                if (TryGetHomogenizedPixelValue(x, y, out double value))
                {
                    values.Add(value);
                }
            }
        }

        if (values.Count == 0)
        {
            mean = 0;
            tolerance = 0;
            return false;
        }

        double[] orderedValues = [.. values.OrderBy(value => value)];
        mean = InterpolatePercentile(orderedValues, 0.5);
        double averageValue = mean;
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double min = values.Min();
        double max = values.Max();
        double interQuartileRange = ComputeInterQuartileRange(values);
        double medianAbsoluteDeviation = ComputeMedianAbsoluteDeviation(orderedValues);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        double baselineTolerance = modality == "CT"
            ? 18.0
            : Math.Max(6.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        double robustSpread = modality == "CT"
            ? Math.Max(interQuartileRange * 1.7, medianAbsoluteDeviation * 3.4)
            : Math.Max(interQuartileRange * 1.7, medianAbsoluteDeviation * 3.0);
        tolerance = ApplyAutoOutlineSensitivity(
            Math.Max(baselineTolerance, Math.Max(stdDev * 2.1, Math.Max((max - min) * 0.48, robustSpread))),
            sensitivityLevel,
            CurrentAutoOutlineDeveloperSettings.TwoDimensionalToleranceScale);
        return true;
    }

    private bool TryComputeAutoOutlineTolerance(AutoOutlineSliceSource source, int seedX, int seedY, double seedValue, double homogenizedSeedValue, int sensitivityLevel, out double mean, out double tolerance)
    {
        List<double> values = [];
        for (int y = Math.Max(0, seedY - 3); y <= Math.Min(source.Height - 1, seedY + 3); y++)
        {
            for (int x = Math.Max(0, seedX - 3); x <= Math.Min(source.Width - 1, seedX + 3); x++)
            {
                if (TryGetHomogenizedPixelValue(source, x, y, out double value))
                {
                    values.Add(value);
                }
            }
        }

        if (values.Count == 0)
        {
            mean = 0;
            tolerance = 0;
            return false;
        }

        double[] orderedValues = [.. values.OrderBy(value => value)];
        mean = InterpolatePercentile(orderedValues, 0.5);
        double averageValue = mean;
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double min = values.Min();
        double max = values.Max();
        double interQuartileRange = ComputeInterQuartileRange(values);
        double medianAbsoluteDeviation = ComputeMedianAbsoluteDeviation(orderedValues);
        string modality = source.Modality.Trim().ToUpperInvariant();
        double baselineTolerance = modality == "CT"
            ? 18.0
            : Math.Max(6.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        double robustSpread = modality == "CT"
            ? Math.Max(interQuartileRange * 1.7, medianAbsoluteDeviation * 3.4)
            : Math.Max(interQuartileRange * 1.7, medianAbsoluteDeviation * 3.0);
        tolerance = ApplyAutoOutlineSensitivity(
            Math.Max(baselineTolerance, Math.Max(stdDev * 2.1, Math.Max((max - min) * 0.48, robustSpread))),
            sensitivityLevel,
            CurrentAutoOutlineDeveloperSettings.TwoDimensionalToleranceScale);
        return true;
    }

    private static bool IsAutoOutlinePixelAccepted(double value, double rawValue, double seedValue, double rawSeedValue, double localMean, double tolerance)
    {
        double seedDelta = Math.Abs(value - seedValue);
        double meanDelta = Math.Abs(value - localMean);
        double rawSeedDelta = Math.Abs(rawValue - rawSeedValue);
        return seedDelta <= tolerance ||
            meanDelta <= tolerance * 0.72 ||
            (seedDelta <= tolerance * 1.12 && rawSeedDelta <= tolerance * 0.55);
    }

    // Contour-loop / marching-squares math now lives in the AutoOutlineMath seam (RoiDraft/).
    // These thin forwarders keep the existing call sites working unchanged.
    private static Point[] TraceAutoOutlineBoundary(AutoOutlineMask mask, int maxPointCount)
        => AutoOutlineMath.TraceBoundary(mask.Pixels, maxPointCount);

    private static double GetPointDistance(Point first, Point second) => AutoOutlineMath.GetPointDistance(first, second);

    private static double ComputeSignedPolygonArea(IReadOnlyList<Point> points) => AutoOutlineMath.ComputeSignedPolygonArea(points);

    private static Point[] ResampleContour(Point[] points, int maxPointCount) => AutoOutlineMath.ResampleContour(points, maxPointCount);

    private bool TrySegmentVolumeSeed(Point imagePoint, int sensitivityLevel, out HashSet<int> region, out string failureReason)
    {
        // ── GPU-accelerated path ─────────────────────────────────────────────
        // Runs the entire region growing on the Tesla/OpenCL GPU with a single
        // seed, parallel iterative flood-fill, and the same post-processing.
        if (VolumeComputeBackend.CanUseOpenCl &&
            TryGpuSegmentVolumeSeed(imagePoint, sensitivityLevel, out region, out failureReason))
        {
            return true;
        }

        // ── CPU fallback (normal + relaxed pass) ─────────────────────────────
        if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: false, out region, out failureReason))
        {
            return true;
        }

        string primaryFailureReason = failureReason;
        if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: true, out region, out failureReason))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(primaryFailureReason) && !string.Equals(primaryFailureReason, failureReason, StringComparison.Ordinal))
        {
            failureReason = $"{primaryFailureReason} Relaxed pass: {failureReason}";
        }

        return false;
    }

    /// <summary>
    /// Segment from a patient-space 3D point — used by the centerline pipeline
    /// where seeds are placed on different slices and the image-space round-trip
    /// through the current viewing plane would lose the off-plane Z coordinate.
    /// Converts the patient point directly to voxel space, then delegates to
    /// the same GPU/CPU paths as the image-point overload.
    /// </summary>
    private bool TrySegmentVolumeSeedFromPatient(SpatialVector3D seedPatientPoint, int sensitivityLevel, out HashSet<int> region, out string failureReason)
    {
        region = [];
        failureReason = string.Empty;

        if (_volume is null)
        {
            failureReason = "Auto 3D ROI needs a loaded 3D volume.";
            return false;
        }

        (double voxelX, double voxelY, double voxelZ) = _volume.PatientToVoxel(seedPatientPoint);
        int seedX = Math.Clamp((int)Math.Round(voxelX), 0, _volume.SizeX - 1);
        int seedY = Math.Clamp((int)Math.Round(voxelY), 0, _volume.SizeY - 1);
        int seedZ = Math.Clamp((int)Math.Round(voxelZ), 0, _volume.SizeZ - 1);
        short seedValue = _volume.GetVoxel(seedX, seedY, seedZ);
        Dictionary<int, double> homogenizedCache = [];
        double homogenizedSeedValue = GetCachedHomogenizedVoxelValue(seedX, seedY, seedZ, homogenizedCache);
        int seedKey = GetVoxelKey(seedX, seedY, seedZ, _volume.SizeX, _volume.SizeY);

        if (!TryComputeVolumeTolerance(seedX, seedY, seedZ, seedValue, homogenizedSeedValue,
                sensitivityLevel, relaxedPass: false, homogenizedCache,
                out _, out double baseTolerance))
        {
            failureReason = "The seed has no stable local intensity model for 3D growing.";
            return false;
        }

        // ── GPU-accelerated path ─────────────────────────────────────────────
        if (VolumeComputeBackend.CanUseOpenCl)
        {
            bool isMrLike = IsMrLikeModality();
            bool isCtLike = IsCtLikeModality();
            double gpuTolerance = baseTolerance * (isMrLike ? 1.8 : isCtLike ? 1.35 : 1.3);
            float gradientLimit = (float)(baseTolerance * (isMrLike ? 3.0 : 2.2));

            int maxRadiusX = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingX, 0.1)), 12, _volume.SizeX);
            int maxRadiusY = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingY, 0.1)), 12, _volume.SizeY);
            int maxRadiusZ = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingZ, 0.1)), 8, _volume.SizeZ);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            int iterations = 0;
            try
            {
                ok = VolumeComputeBackend.TryGpuSegmentRegion(
                    _volume,
                    seedX, seedY, seedZ,
                    (float)homogenizedSeedValue,
                    (float)gpuTolerance,
                    gradientLimit,
                    maxRadiusX, maxRadiusY, maxRadiusZ,
                    AutoOutlineMaxVoxelCount,
                    out region,
                    out iterations);
            }
            catch (Exception ex)
            {
                _lastGpuSegmentLog = $"[GPU] EXCEPTION {ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine($"[AutoOutline·GPU·Patient] EXCEPTION seed=({seedX},{seedY},{seedZ}): {ex.Message}");
                region = [];
                ok = false;
            }

            long gpuMs = sw.ElapsedMilliseconds;
            if (ok && region.Count >= AutoOutlineMinPixels * 2)
            {
                int rawCount = region.Count;
                region = RetainSeedConnectedVolumeRegion(region, seedKey);
                int ccCount = region.Count;
                if (region.Count >= AutoOutlineMinPixels * 2)
                {
                    region = SmoothVolumeRegion(region, seedKey);
                    if (region.Count >= AutoOutlineMinPixels * 2)
                    {
                        long totalMs = sw.ElapsedMilliseconds;
                        _lastGpuSegmentLog = $"[GPU] OK raw={rawCount} cc={ccCount} final={region.Count} iter={iterations} tol={gpuTolerance:F1} grad={gradientLimit:F1} gpu={gpuMs}ms total={totalMs}ms";
                        Console.Error.WriteLine($"[AutoOutline·GPU·Patient] OK seed=({seedX},{seedY},{seedZ}) raw={rawCount} final={region.Count} iter={iterations} {totalMs}ms");
                        return true;
                    }
                }
            }

            // GPU path didn't produce a viable result — fall through to CPU.
            _lastGpuSegmentLog = $"[GPU] raw={region.Count} iter={iterations} tol={gpuTolerance:F1} {gpuMs}ms → FALLBACK";
            region = [];
        }

        // ── CPU fallback — convert to image point for existing CPU path ──────
        if (SpatialMetadata is not null)
        {
            Point imagePoint = SpatialMetadata.PixelPointFromPatient(seedPatientPoint);
            imagePoint = ClampImagePoint(imagePoint);
            if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: false, out region, out failureReason))
            {
                return true;
            }

            if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: true, out region, out failureReason))
            {
                return true;
            }
        }

        failureReason = "Volume seed segmentation failed from patient-space coordinates.";
        return false;
    }

    /// <summary>
    /// Diagnostic log from the most recent GPU segmentation attempt.
    /// Surfaced in the toast to make GPU behaviour visible during development.
    /// </summary>
    private string _lastGpuSegmentLog = string.Empty;

    private bool TryGpuSegmentVolumeSeed(Point imagePoint, int sensitivityLevel, out HashSet<int> region, out string failureReason)
    {
        region = [];
        failureReason = string.Empty;
        _lastGpuSegmentLog = string.Empty;

        if (_volume is null || SpatialMetadata is null)
        {
            failureReason = "Auto 3D ROI needs a loaded 3D volume.";
            return false;
        }

        // Reuse the same seed-position and tolerance computation as the CPU path.
        SpatialVector3D seedPatientPoint = SpatialMetadata.PatientPointFromPixel(imagePoint);
        (double voxelX, double voxelY, double voxelZ) = _volume.PatientToVoxel(seedPatientPoint);
        int seedX = Math.Clamp((int)Math.Round(voxelX), 0, _volume.SizeX - 1);
        int seedY = Math.Clamp((int)Math.Round(voxelY), 0, _volume.SizeY - 1);
        int seedZ = Math.Clamp((int)Math.Round(voxelZ), 0, _volume.SizeZ - 1);
        short seedValue = _volume.GetVoxel(seedX, seedY, seedZ);
        Dictionary<int, double> homogenizedCache = [];
        double homogenizedSeedValue = GetCachedHomogenizedVoxelValue(seedX, seedY, seedZ, homogenizedCache);
        int seedKey = GetVoxelKey(seedX, seedY, seedZ, _volume.SizeX, _volume.SizeY);

        if (!TryComputeVolumeTolerance(seedX, seedY, seedZ, seedValue, homogenizedSeedValue,
                sensitivityLevel, relaxedPass: false, homogenizedCache,
                out _, out double baseTolerance))
        {
            _lastGpuSegmentLog = "[GPU] tolerance computation failed — no stable seed model";
            failureReason = "The clicked seed has no stable local intensity model for GPU growing.";
            return false;
        }

        int maxRadiusX = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingX, 0.1)), 12, _volume.SizeX);
        int maxRadiusY = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingY, 0.1)), 12, _volume.SizeY);
        int maxRadiusZ = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingZ, 0.1)), 8, _volume.SizeZ);

        // The GPU uses a single flat tolerance+gradient check per voxel, unlike the
        // CPU which has adaptive tolerance, parent-delta, local-median, neighbourhood
        // support, and priority-queue ordering.  To compensate, the GPU tolerance must
        // be more generous than the CPU base tolerance.  The CPU effectively accepts
        // voxels up to ~1.7× baseTolerance (MR) / ~1.2× baseTolerance (CT) via the
        // seed-delta path alone, and the adaptive tolerance widens further as the
        // region grows.  Apply a comparable widening here.
        bool isMrLike = IsMrLikeModality();
        bool isCtLike = IsCtLikeModality();
        double gpuTolerance = baseTolerance * (isMrLike ? 1.8 : isCtLike ? 1.35 : 1.3);
        float gradientLimit = (float)(baseTolerance * (isMrLike ? 3.0 : 2.2));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok;
        int iterations = 0;
        try
        {
            ok = VolumeComputeBackend.TryGpuSegmentRegion(
                _volume,
                seedX, seedY, seedZ,
                (float)homogenizedSeedValue,
                (float)gpuTolerance,
                gradientLimit,
                maxRadiusX, maxRadiusY, maxRadiusZ,
                AutoOutlineMaxVoxelCount,
                out region,
                out iterations);
        }
        catch (Exception ex)
        {
            _lastGpuSegmentLog = $"[GPU] EXCEPTION {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[AutoOutline·GPU] EXCEPTION seed=({seedX},{seedY},{seedZ}) tol={gpuTolerance:F1} grad={gradientLimit:F1}: {ex.Message}");
            region = [];
            failureReason = "GPU region growing threw an exception.";
            return false;
        }

        long gpuMs = sw.ElapsedMilliseconds;

        if (!ok || region.Count < AutoOutlineMinPixels * 2)
        {
            _lastGpuSegmentLog = $"[GPU] raw={region.Count} iter={iterations} tol={gpuTolerance:F1} grad={gradientLimit:F1} {gpuMs}ms → FALLBACK";
            Console.Error.WriteLine($"[AutoOutline·GPU] FAILED seed=({seedX},{seedY},{seedZ}) raw={region.Count} iter={iterations} tol={gpuTolerance:F1} grad={gradientLimit:F1} {gpuMs}ms");
            failureReason = "GPU region growing did not produce a viable region. Falling back to CPU.";
            region = [];
            return false;
        }

        int rawCount = region.Count;

        // Same post-processing as the CPU path.
        region = RetainSeedConnectedVolumeRegion(region, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            _lastGpuSegmentLog = $"[GPU] raw={rawCount} cc={region.Count} iter={iterations} tol={gpuTolerance:F1} {gpuMs}ms → CC too small";
            Console.Error.WriteLine($"[AutoOutline·GPU] FRAGMENTED after CC: raw={rawCount} cc={region.Count} {sw.ElapsedMilliseconds}ms");
            failureReason = "GPU segmented region was too fragmented after connected-component filtering.";
            region = [];
            return false;
        }

        int ccCount = region.Count;
        region = SmoothVolumeRegion(region, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            _lastGpuSegmentLog = $"[GPU] raw={rawCount} cc={ccCount} smooth={region.Count} {sw.ElapsedMilliseconds}ms → smooth collapsed";
            Console.Error.WriteLine($"[AutoOutline·GPU] COLLAPSED after smooth: {region.Count} voxels {sw.ElapsedMilliseconds}ms");
            failureReason = "GPU segmented region collapsed during smoothing.";
            region = [];
            return false;
        }

        long totalMs = sw.ElapsedMilliseconds;
        _lastGpuSegmentLog = $"[GPU] OK raw={rawCount} cc={ccCount} final={region.Count} iter={iterations} tol={gpuTolerance:F1} grad={gradientLimit:F1} gpu={gpuMs}ms total={totalMs}ms";
        Console.Error.WriteLine($"[AutoOutline·GPU] OK seed=({seedX},{seedY},{seedZ}) raw={rawCount} final={region.Count} iter={iterations} {totalMs}ms");
        return true;
    }

    private bool TrySegmentVolumeSeed(Point imagePoint, int sensitivityLevel, bool relaxedPass, out HashSet<int> region, out string failureReason)
    {
        region = [];
        failureReason = "Auto 3D ROI could not start.";
        if (_volume is null || SpatialMetadata is null)
        {
            failureReason = "Auto 3D ROI needs a loaded 3D volume.";
            return false;
        }

        SpatialVector3D seedPatientPoint = SpatialMetadata.PatientPointFromPixel(imagePoint);
        (double voxelX, double voxelY, double voxelZ) = _volume.PatientToVoxel(seedPatientPoint);
        int seedX = Math.Clamp((int)Math.Round(voxelX), 0, _volume.SizeX - 1);
        int seedY = Math.Clamp((int)Math.Round(voxelY), 0, _volume.SizeY - 1);
        int seedZ = Math.Clamp((int)Math.Round(voxelZ), 0, _volume.SizeZ - 1);
        short seedValue = _volume.GetVoxel(seedX, seedY, seedZ);
        Dictionary<int, double> homogenizedCache = [];
        Dictionary<int, double> gradientCache = [];
        double homogenizedSeedValue = GetCachedHomogenizedVoxelValue(seedX, seedY, seedZ, homogenizedCache);
        int seedKey = GetVoxelKey(seedX, seedY, seedZ, _volume.SizeX, _volume.SizeY);

        if (!TryComputeVolumeTolerance(seedX, seedY, seedZ, seedValue, homogenizedSeedValue, sensitivityLevel, relaxedPass, homogenizedCache, out double localMean, out double tolerance))
        {
            failureReason = "The clicked seed has no stable local intensity model for 3D growing.";
            return false;
        }

        VolumeSeedCandidate[] seedCandidates = BuildVolumeSeedCandidates(seedX, seedY, seedZ, seedValue, homogenizedSeedValue, tolerance, relaxedPass, homogenizedCache, gradientCache);
        if (seedCandidates.Length == 0)
        {
            failureReason = "No valid seed candidates were found around the clicked point.";
            return false;
        }

        Dictionary<int, int> voteCounts = [];
        int successfulSeedCount = 0;
        IEnumerable<HashSet<int>> grownSeedRegions = seedCandidates.Length >= AutoOutlineParallelCandidateThreshold &&
            Environment.ProcessorCount >= AutoOutlineDirectionParallelismMinCpuCount
            ? seedCandidates
                .AsParallel()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
                .Select(seedCandidate =>
                {
                    Dictionary<int, double> localHomogenizedCache = [];
                    Dictionary<int, double> localGradientCache = [];
                    return TryGrowVolumeSeedRegion(seedCandidate, sensitivityLevel, relaxedPass, localHomogenizedCache, localGradientCache, out HashSet<int> seedRegion)
                        ? seedRegion
                        : null;
                })
                .Where(seedRegion => seedRegion is not null)
                .Select(seedRegion => seedRegion!)
            : GrowVolumeSeedRegionsSequentially(seedCandidates, sensitivityLevel, relaxedPass, homogenizedCache, gradientCache);

        foreach (HashSet<int> seedRegion in grownSeedRegions)
        {
            successfulSeedCount++;
            foreach (int key in seedRegion)
            {
                voteCounts[key] = voteCounts.TryGetValue(key, out int votes) ? votes + 1 : 1;
            }
        }

        if (successfulSeedCount == 0 || voteCounts.Count == 0)
        {
            failureReason = "Region growing was attempted, but no connected voxel region matched the clicked structure.";
            return false;
        }

        HashSet<int> fusedRegion = FuseVolumeSeedRegions(voteCounts, successfulSeedCount, seedKey);
        if (fusedRegion.Count == 0)
        {
            failureReason = "Candidate regions were found, but fusion rejected them as inconsistent.";
            return false;
        }

        region = RetainSeedConnectedVolumeRegion(fusedRegion, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            failureReason = "The segmented 3D region was too small or too fragmented.";
            return false;
        }

        region = SmoothVolumeRegion(region, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            failureReason = "The segmented 3D region collapsed during cleanup and is not reliable enough yet.";
            return false;
        }

        return true;
    }

    private bool TryGrowVolumeSeedRegion(
        VolumeSeedCandidate seedCandidate,
        int sensitivityLevel,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        Dictionary<int, double> gradientCache,
        out HashSet<int> region)
    {
        region = [];
        if (_volume is null)
        {
            return false;
        }

        if (!TryComputeVolumeTolerance(
                seedCandidate.X,
                seedCandidate.Y,
                seedCandidate.Z,
                seedCandidate.RawValue,
                seedCandidate.HomogenizedValue,
                sensitivityLevel,
                relaxedPass,
                homogenizedCache,
                out double localMean,
                out double tolerance))
        {
            return false;
        }

        int maxRadiusX = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingX, 0.1)), 12, _volume.SizeX);
        int maxRadiusY = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingY, 0.1)), 12, _volume.SizeY);
        int maxRadiusZ = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingZ, 0.1)), 8, _volume.SizeZ);

        HashSet<int> visited = [];
        PriorityQueue<VolumeGrowNode, double> queue = new();
        queue.Enqueue(new VolumeGrowNode(seedCandidate.X, seedCandidate.Y, seedCandidate.Z, seedCandidate.RawValue, seedCandidate.HomogenizedValue), 0);
        visited.Add(GetVoxelKey(seedCandidate.X, seedCandidate.Y, seedCandidate.Z, _volume.SizeX, _volume.SizeY));

        VolumeGrowthStatistics stats = new(seedCandidate.HomogenizedValue, localMean, tolerance);
        while (queue.Count > 0)
        {
            VolumeGrowNode node = queue.Dequeue();
            short value = _volume.GetVoxel(node.X, node.Y, node.Z);
            double homogenizedValue = GetCachedHomogenizedVoxelValue(node.X, node.Y, node.Z, homogenizedCache);
            if (!IsAdaptiveVolumeVoxelAccepted(node, value, homogenizedValue, seedCandidate, stats, relaxedPass, gradientCache, homogenizedCache))
            {
                continue;
            }

            int key = GetVoxelKey(node.X, node.Y, node.Z, _volume.SizeX, _volume.SizeY);
            if (!region.Add(key))
            {
                continue;
            }

            stats.Include(homogenizedValue);
            if (region.Count > AutoOutlineMaxVoxelCount)
            {
                region.Clear();
                return false;
            }

            if (visited.Count > AutoOutlineMaxVisitedVoxelsPerSeed)
            {
                region.Clear();
                return false;
            }

            Span<(int X, int Y, int Z)> neighbors =
            [
                (node.X - 1, node.Y, node.Z), (node.X + 1, node.Y, node.Z),
                (node.X, node.Y - 1, node.Z), (node.X, node.Y + 1, node.Z),
                (node.X, node.Y, node.Z - 1), (node.X, node.Y, node.Z + 1),
            ];

            foreach ((int nextX, int nextY, int nextZ) in neighbors)
            {
                if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                {
                    continue;
                }

                if (Math.Abs(nextX - seedCandidate.X) > maxRadiusX || Math.Abs(nextY - seedCandidate.Y) > maxRadiusY || Math.Abs(nextZ - seedCandidate.Z) > maxRadiusZ)
                {
                    continue;
                }

                int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                if (!visited.Add(nextKey))
                {
                    continue;
                }

                short nextValue = _volume.GetVoxel(nextX, nextY, nextZ);
                double nextHomogenized = GetCachedHomogenizedVoxelValue(nextX, nextY, nextZ, homogenizedCache);
                queue.Enqueue(
                    new VolumeGrowNode(nextX, nextY, nextZ, value, homogenizedValue),
                    ComputeVolumeGrowthPriority(seedCandidate, stats, nextValue, nextHomogenized, value, homogenizedValue));
            }
        }

        return region.Count >= AutoOutlineMinPixels * 2;
    }

    private IEnumerable<HashSet<int>> GrowVolumeSeedRegionsSequentially(
        IEnumerable<VolumeSeedCandidate> seedCandidates,
        int sensitivityLevel,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        Dictionary<int, double> gradientCache)
    {
        foreach (VolumeSeedCandidate seedCandidate in seedCandidates)
        {
            if (TryGrowVolumeSeedRegion(seedCandidate, sensitivityLevel, relaxedPass, homogenizedCache, gradientCache, out HashSet<int> seedRegion))
            {
                yield return seedRegion;
            }
        }
    }

    private bool TryComputeVolumeTolerance(
        int seedX,
        int seedY,
        int seedZ,
        short seedValue,
        double homogenizedSeedValue,
        int sensitivityLevel,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        out double mean,
        out double tolerance)
    {
        mean = 0;
        tolerance = 0;
        if (_volume is null)
        {
            return false;
        }

        List<double> values = [];
        for (int z = Math.Max(0, seedZ - 1); z <= Math.Min(_volume.SizeZ - 1, seedZ + 1); z++)
        {
            for (int y = Math.Max(0, seedY - 2); y <= Math.Min(_volume.SizeY - 1, seedY + 2); y++)
            {
                for (int x = Math.Max(0, seedX - 2); x <= Math.Min(_volume.SizeX - 1, seedX + 2); x++)
                {
                    values.Add(GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache));
                }
            }
        }

        if (values.Count == 0)
        {
            return false;
        }

        double[] orderedValues = [.. values.OrderBy(value => value)];
        mean = InterpolatePercentile(orderedValues, 0.5);
        double averageValue = values.Average();
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double range = values.Max() - values.Min();
        double interQuartileRange = ComputeInterQuartileRange(values);
        double medianAbsoluteDeviation = ComputeMedianAbsoluteDeviation(orderedValues);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        bool isMrLike = IsMrLikeModality();
        double baselineTolerance = modality == "CT"
            ? 22.0
            : isMrLike
                ? Math.Max(12.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.12)
                : Math.Max(8.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        double robustSpread = Math.Max(
            range * (isMrLike ? 0.68 : 0.54),
            Math.Max(
                interQuartileRange * (isMrLike ? 2.2 : 1.85),
                medianAbsoluteDeviation * (isMrLike ? 3.4 : 2.9)));
        double toleranceCore = Math.Max(
            baselineTolerance,
            Math.Max(
                stdDev * (isMrLike ? 2.7 : 2.2),
                robustSpread));
        tolerance = ApplyAutoOutlineSensitivity(toleranceCore * GetVolumeToleranceMultiplier(relaxedPass), sensitivityLevel, 1.0);
        return true;
    }

    private static double ApplyAutoOutlineSensitivity(double tolerance, int sensitivityLevel, double developerScale)
    {
        double scaled = tolerance * Math.Pow(1.14, sensitivityLevel) * Math.Max(0.2, developerScale);
        return Math.Max(1.0, scaled);
    }

    private bool TryGetHomogenizedPixelValue(int x, int y, out double value)
    {
        value = 0;
        if (_imageWidth <= 0 || _imageHeight <= 0 || (uint)x >= (uint)_imageWidth || (uint)y >= (uint)_imageHeight)
        {
            return false;
        }

        int radius = s_autoOutlineKernel.Length / 2;
        List<double> rawSamples = [];
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, _imageHeight - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, _imageWidth - 1);
                if (!TryGetPixelValue(sampleX, sampleY, out double sample))
                {
                    continue;
                }

                rawSamples.Add(sample);
            }
        }

        if (rawSamples.Count == 0)
        {
            return TryGetPixelValue(x, y, out value);
        }

        (double clampLower, double clampUpper) = CurrentAutoOutlineDeveloperSettings.UseRobustHomogenization
            ? ComputeRobustHomogenizationBounds(rawSamples, (_modality ?? string.Empty).Trim().ToUpperInvariant())
            : (double.NegativeInfinity, double.PositiveInfinity);
        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, _imageHeight - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, _imageWidth - 1);
                if (!TryGetPixelValue(sampleX, sampleY, out double sample))
                {
                    continue;
                }

                int weight = weightY * s_autoOutlineKernel[offsetX + radius];
                weightedSum += Math.Clamp(sample, clampLower, clampUpper) * weight;
                weightTotal += weight;
            }
        }

        value = weightTotal <= 0 ? rawSamples.Average() : weightedSum / weightTotal;
        return true;
    }

    private bool TryGetHomogenizedPixelValue(AutoOutlineSliceSource source, int x, int y, out double value)
    {
        value = 0;
        if (source.Width <= 0 || source.Height <= 0 || (uint)x >= (uint)source.Width || (uint)y >= (uint)source.Height)
        {
            return false;
        }

        int radius = s_autoOutlineKernel.Length / 2;
        List<double> rawSamples = [];
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, source.Height - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, source.Width - 1);
                if (!TryGetSlicePixelValue(source, sampleX, sampleY, out double sample))
                {
                    continue;
                }

                rawSamples.Add(sample);
            }
        }

        if (rawSamples.Count == 0)
        {
            return TryGetSlicePixelValue(source, x, y, out value);
        }

        (double clampLower, double clampUpper) = CurrentAutoOutlineDeveloperSettings.UseRobustHomogenization
            ? ComputeRobustHomogenizationBounds(rawSamples, source.Modality.Trim().ToUpperInvariant())
            : (double.NegativeInfinity, double.PositiveInfinity);
        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, source.Height - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, source.Width - 1);
                if (!TryGetSlicePixelValue(source, sampleX, sampleY, out double sample))
                {
                    continue;
                }

                int weight = weightY * s_autoOutlineKernel[offsetX + radius];
                weightedSum += Math.Clamp(sample, clampLower, clampUpper) * weight;
                weightTotal += weight;
            }
        }

        value = weightTotal <= 0 ? rawSamples.Average() : weightedSum / weightTotal;
        return true;
    }

    private double[,] BuildHomogenizedPixelWindow(int left, int top, int width, int height)
    {
        double[,] values = new double[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                TryGetHomogenizedPixelValue(left + x, top + y, out values[x, y]);
            }
        }

        return values;
    }

    private double[,] BuildHomogenizedPixelWindow(AutoOutlineSliceSource source, int left, int top, int width, int height)
    {
        double[,] values = new double[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                TryGetHomogenizedPixelValue(source, left + x, top + y, out values[x, y]);
            }
        }

        return values;
    }

    private bool CanUseSlicePropagatedAutoOutline()
    {
        return _volume is not null &&
            SpatialMetadata is not null &&
            _projectionMode == VolumeProjectionMode.Mpr &&
            !HasTiltedPlane;
    }

    private bool TryBuildSlicePropagatedVolumeContours(
        Point imagePoint,
        int sensitivityLevel,
        out VolumeRoiContour[] contours,
        out string failureReason)
    {
        contours = [];
        failureReason = "Slice-propagated auto-outline could not establish a stable 3D contour stack.";
        if (_volume is null || SpatialMetadata is null || _volumeSlicePixels is null || _imageWidth <= 0 || _imageHeight <= 0)
        {
            failureReason = "Slice-propagated auto-outline requires the current MPR slice buffer.";
            return false;
        }

        AutoOutlineSliceSource seedSource = new(_imageWidth, _imageHeight, _volumeSlicePixels, _modality ?? string.Empty);
        SlicePropagationSliceData seedSlice = CreateSlicePropagationSliceData(_volumeSliceIndex, SpatialMetadata, seedSource);
        Point clampedPoint = new(
            Math.Clamp(imagePoint.X, 0, _imageWidth - 1),
            Math.Clamp(imagePoint.Y, 0, _imageHeight - 1));

        if (!TryCreateAutoOutlinedPolygon(seedSlice, clampedPoint, out Point[] seedPolygon, sensitivityLevel) || seedPolygon.Length < 3)
        {
            failureReason = "The seed slice could not be isolated reliably with the 2D auto-outline.";
            return false;
        }

        if (!TryComputeAutoOutlineIntensitySignature(seedSource, seedPolygon, out AutoOutlineIntensitySignature seedSignature))
        {
            failureReason = "The seed slice was outlined, but its intensity signature was not stable enough for 3D propagation.";
            return false;
        }

        List<(int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature)> sliceContours =
        [
            (_volumeSliceIndex, SpatialMetadata, seedPolygon, seedSignature)
        ];

        System.Collections.Concurrent.ConcurrentDictionary<int, SlicePropagationSliceData> sliceCache = new();
        sliceCache.TryAdd(seedSlice.SliceIndex, seedSlice);

        List<(int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature)> backwardContours = [];
        List<(int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature)> forwardContours = [];

        if (Environment.ProcessorCount >= AutoOutlineDirectionParallelismMinCpuCount)
        {
            Parallel.Invoke(
                () => backwardContours = PropagateAutoOutlineAcrossSlices(sliceContours[0], seedSignature, -1, sensitivityLevel, sliceCache),
                () => forwardContours = PropagateAutoOutlineAcrossSlices(sliceContours[0], seedSignature, 1, sensitivityLevel, sliceCache));
        }
        else
        {
            backwardContours = PropagateAutoOutlineAcrossSlices(sliceContours[0], seedSignature, -1, sensitivityLevel, sliceCache);
            forwardContours = PropagateAutoOutlineAcrossSlices(sliceContours[0], seedSignature, 1, sensitivityLevel, sliceCache);
        }

        sliceContours.AddRange(backwardContours);
        sliceContours.AddRange(forwardContours);

        contours = sliceContours
            .OrderBy(entry => entry.SliceIndex)
            .Select(entry => CreateVolumeRoiContour(entry.Metadata, entry.Polygon, 0))
            .ToArray();

        if (contours.Length == 0)
        {
            failureReason = "The seed slice was found, but no valid 3D contour stack could be assembled.";
            return false;
        }

        return true;
    }

    private List<(int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature)> PropagateAutoOutlineAcrossSlices(
        (int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature) seedContour,
        AutoOutlineIntensitySignature seedSignature,
        int direction,
        int sensitivityLevel,
        System.Collections.Concurrent.ConcurrentDictionary<int, SlicePropagationSliceData> sliceCache)
    {
        List<(int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature)> sliceContours = [];
        if (_volume is null)
        {
            return sliceContours;
        }

        int sliceCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
        (int SliceIndex, DicomSpatialMetadata Metadata, Point[] Polygon, AutoOutlineIntensitySignature Signature) current = seedContour;
        int consecutiveMisses = 0;

        for (int sliceIndex = current.SliceIndex + direction; sliceIndex >= 0 && sliceIndex < sliceCount; sliceIndex += direction)
        {
            SlicePropagationSliceData sliceData = GetOrCreateSlicePropagationSliceData(sliceIndex, sliceCache);
            DicomSpatialMetadata? sliceMetadata = sliceData.Metadata;
            if (!sliceData.IsValid || sliceMetadata is null)
            {
                break;
            }

            if (!TryFindBestSlicePropagatedPolygon(sliceData, current.Metadata, current.Polygon, current.Signature, seedSignature, sensitivityLevel, out Point[] polygon, out AutoOutlineIntensitySignature polygonSignature))
            {
                consecutiveMisses++;
                if (consecutiveMisses >= AutoOutlineSlicePropagationMaxMisses)
                {
                    break;
                }

                continue;
            }

            current = (sliceIndex, sliceMetadata, polygon, polygonSignature);
            sliceContours.Add(current);
            consecutiveMisses = 0;
        }

        return sliceContours;
    }

    private bool TryFindBestSlicePropagatedPolygon(
        SlicePropagationSliceData sliceData,
        DicomSpatialMetadata referenceMetadata,
        Point[] referencePolygon,
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature seedSignature,
        int sensitivityLevel,
        out Point[] polygon,
        out AutoOutlineIntensitySignature polygonSignature)
    {
        polygon = [];
        polygonSignature = default;
        DicomSpatialMetadata? targetMetadata = sliceData.Metadata;
        if (targetMetadata is null)
        {
            return false;
        }

        Point[] projectedReferencePolygon = ProjectPolygon(referenceMetadata, referencePolygon, targetMetadata);
        if (projectedReferencePolygon.Length < 3)
        {
            return false;
        }

        List<Point> seedCandidates = BuildSlicePropagationSeedCandidates(projectedReferencePolygon, sliceData.Source.Width, sliceData.Source.Height);
        if (seedCandidates.Count == 0)
        {
            return false;
        }

        foreach (int candidateSensitivity in EnumerateSlicePropagationSensitivityLevels(sensitivityLevel))
        {
            int maxCandidateParallelism = Math.Max(1, Environment.ProcessorCount >= AutoOutlineDirectionParallelismMinCpuCount
                ? Environment.ProcessorCount / 2
                : Environment.ProcessorCount);
            IEnumerable<SlicePropagationCandidateResult> candidateResults = seedCandidates.Count >= AutoOutlineParallelCandidateThreshold && maxCandidateParallelism > 1
                ? seedCandidates
                    .AsParallel()
                    .WithDegreeOfParallelism(Math.Min(seedCandidates.Count, maxCandidateParallelism))
                    .Select(seedCandidate => EvaluateSlicePropagationCandidate(sliceData, seedCandidate, candidateSensitivity, projectedReferencePolygon, referenceSignature, seedSignature))
                : seedCandidates
                    .Select(seedCandidate => EvaluateSlicePropagationCandidate(sliceData, seedCandidate, candidateSensitivity, projectedReferencePolygon, referenceSignature, seedSignature));

            SlicePropagationCandidateResult bestCandidate = candidateResults
                .Where(candidate => candidate.IsValid)
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();

            if (bestCandidate.IsValid)
            {
                polygon = bestCandidate.Polygon;
                polygonSignature = bestCandidate.Signature;
                return true;
            }
        }

        return false;
    }

    private SlicePropagationCandidateResult EvaluateSlicePropagationCandidate(
        SlicePropagationSliceData sliceData,
        Point seedCandidate,
        int candidateSensitivity,
        Point[] projectedReferencePolygon,
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature seedSignature)
    {
        if (!TryCreateAutoOutlinedPolygon(sliceData, seedCandidate, out Point[] candidatePolygon, candidateSensitivity) ||
            candidatePolygon.Length < 3)
        {
            return default;
        }

        if (!TryComputeAutoOutlineIntensitySignature(sliceData.Source, candidatePolygon, out AutoOutlineIntensitySignature candidateSignature) ||
            !IsSlicePropagationContourStable(projectedReferencePolygon, candidatePolygon, referenceSignature, candidateSignature, seedSignature))
        {
            return default;
        }

        double score = ComputeSlicePropagationContourScore(projectedReferencePolygon, candidatePolygon, referenceSignature, candidateSignature, seedSignature);
        return new SlicePropagationCandidateResult(candidatePolygon, candidateSignature, score);
    }

    private SlicePropagationSliceData GetOrCreateSlicePropagationSliceData(
        int sliceIndex,
        System.Collections.Concurrent.ConcurrentDictionary<int, SlicePropagationSliceData> sliceCache)
    {
        return sliceCache.GetOrAdd(sliceIndex, CreateSlicePropagationSliceData);
    }

    private SlicePropagationSliceData CreateSlicePropagationSliceData(int sliceIndex)
    {
        if (_volume is null)
        {
            return default;
        }

        ReslicedImage resliced = VolumeReslicer.ExtractSlice(_volume, _volumeOrientation, sliceIndex);
        if (resliced.SpatialMetadata is null || resliced.Pixels.Length == 0 || resliced.Width <= 0 || resliced.Height <= 0)
        {
            return default;
        }

        AutoOutlineSliceSource source = new(resliced.Width, resliced.Height, resliced.Pixels, _modality ?? string.Empty);
        return CreateSlicePropagationSliceData(sliceIndex, resliced.SpatialMetadata, source);
    }

    private static SlicePropagationSliceData CreateSlicePropagationSliceData(int sliceIndex, DicomSpatialMetadata metadata, AutoOutlineSliceSource source)
    {
        return new SlicePropagationSliceData(sliceIndex, metadata, source);
    }

    private bool IsSlicePropagationContourStable(
        DicomSpatialMetadata previousMetadata,
        Point[] previousPolygon,
        DicomSpatialMetadata currentMetadata,
        Point[] currentPolygon)
    {
        Point[] projectedPreviousPolygon = ProjectPolygon(previousMetadata, previousPolygon, currentMetadata);
        return IsSlicePropagationContourStable(projectedPreviousPolygon, currentPolygon, default, default, default);
    }

    private bool IsSlicePropagationContourStable(
        Point[] projectedReferencePolygon,
        Point[] currentPolygon,
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature candidateSignature,
        AutoOutlineIntensitySignature seedSignature)
    {
        if (projectedReferencePolygon.Length < 3 || currentPolygon.Length < 3)
        {
            return false;
        }

        Point previousProjectedCenter = GetPolygonCenter(projectedReferencePolygon);
        Point currentCenter = GetPolygonCenter(currentPolygon);
        double centroidDistance = GetPointDistance(previousProjectedCenter, currentCenter);
        double previousArea = Math.Abs(ComputeSignedPolygonArea(projectedReferencePolygon));
        double currentArea = Math.Abs(ComputeSignedPolygonArea(currentPolygon));
        if (previousArea <= double.Epsilon || currentArea <= double.Epsilon)
        {
            return false;
        }

        double areaRatio = currentArea / previousArea;
        double centroidLimit = Math.Max(18.0, Math.Sqrt(previousArea) * 0.9);
        double boundsOverlap = ComputePolygonBoundsOverlap(projectedReferencePolygon, currentPolygon);
        bool geometryStable = areaRatio is >= 0.05 and <= 6.5 && centroidDistance <= centroidLimit && boundsOverlap >= 0.12;
        if (!geometryStable)
        {
            return false;
        }

        if (!referenceSignature.IsValid || !candidateSignature.IsValid || !seedSignature.IsValid)
        {
            return true;
        }

        return IsIntensitySignatureCompatible(referenceSignature, candidateSignature, seedSignature);
    }

    private double ComputeSlicePropagationContourScore(
        Point[] projectedReferencePolygon,
        Point[] currentPolygon,
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature candidateSignature,
        AutoOutlineIntensitySignature seedSignature)
    {
        double previousArea = Math.Max(1.0, Math.Abs(ComputeSignedPolygonArea(projectedReferencePolygon)));
        double currentArea = Math.Max(1.0, Math.Abs(ComputeSignedPolygonArea(currentPolygon)));
        double areaRatio = currentArea / previousArea;
        double areaPenalty = Math.Abs(Math.Log(areaRatio));
        double centroidDistance = GetPointDistance(GetPolygonCenter(projectedReferencePolygon), GetPolygonCenter(currentPolygon));
        double boundsOverlap = ComputePolygonBoundsOverlap(projectedReferencePolygon, currentPolygon);
        double intensityScore = referenceSignature.IsValid && candidateSignature.IsValid && seedSignature.IsValid
            ? ComputeIntensitySignatureScore(referenceSignature, candidateSignature, seedSignature)
            : 0;
        return (boundsOverlap * 3.0) - areaPenalty - (centroidDistance * 0.035) + intensityScore;
    }

    private static List<Point> BuildSlicePropagationSeedCandidates(Point[] projectedReferencePolygon, int width, int height)
    {
        List<Point> candidates = [];
        HashSet<(int X, int Y)> seen = [];

        void AddCandidate(Point candidate)
        {
            Point clamped = new(
                Math.Clamp(candidate.X, 0, Math.Max(0, width - 1)),
                Math.Clamp(candidate.Y, 0, Math.Max(0, height - 1)));
            (int X, int Y) key = ((int)Math.Round(clamped.X), (int)Math.Round(clamped.Y));
            if (seen.Add(key))
            {
                candidates.Add(clamped);
            }
        }

        Point center = GetPolygonCenter(projectedReferencePolygon);
        Point seedCenter = FindInteriorSeedPoint(projectedReferencePolygon, center);
        AddCandidate(seedCenter);

        Rect bounds = GetPolygonBounds(projectedReferencePolygon);
        AddCandidate(FindInteriorSeedPoint(projectedReferencePolygon, new Point(bounds.X + (bounds.Width * 0.5), bounds.Y + (bounds.Height * 0.5))));

        Point[] samples = ResampleContour(projectedReferencePolygon.ToArray(), AutoOutlineSlicePropagationSeedSampleCount);
        foreach (Point sample in samples)
        {
            Point inward = new(
                center.X + ((sample.X - center.X) * 0.45),
                center.Y + ((sample.Y - center.Y) * 0.45));
            AddCandidate(FindInteriorSeedPoint(projectedReferencePolygon, inward));
        }

        return candidates;
    }

    private static Point ProjectContourCenterToSlice(DicomSpatialMetadata sourceMetadata, Point[] sourcePolygon, DicomSpatialMetadata targetMetadata)
    {
        return ProjectPolygonCenter(sourceMetadata, sourcePolygon, targetMetadata);
    }

    private static Point ProjectPolygonCenter(DicomSpatialMetadata sourceMetadata, Point[] sourcePolygon, DicomSpatialMetadata targetMetadata)
    {
        if (sourcePolygon.Length == 0)
        {
            return default;
        }

        SpatialVector3D sum = default;
        int count = 0;
        foreach (Point point in sourcePolygon)
        {
            sum += sourceMetadata.PatientPointFromPixel(point);
            count++;
        }

        SpatialVector3D center = count == 0 ? default : sum / count;
        return targetMetadata.PixelPointFromPatient(center);
    }

    private static Point[] ProjectPolygon(DicomSpatialMetadata sourceMetadata, Point[] sourcePolygon, DicomSpatialMetadata targetMetadata)
    {
        return sourcePolygon
            .Select(point => targetMetadata.PixelPointFromPatient(sourceMetadata.PatientPointFromPixel(point)))
            .ToArray();
    }

    private static IEnumerable<int> EnumerateSlicePropagationSensitivityLevels(int sensitivityLevel)
    {
        yield return sensitivityLevel;

        int expanded = Math.Clamp(sensitivityLevel + 1, AutoOutlineMinSensitivityLevel, AutoOutlineMaxSensitivityLevel);
        if (expanded != sensitivityLevel)
        {
            yield return expanded;
        }

        int contracted = Math.Clamp(sensitivityLevel - 1, AutoOutlineMinSensitivityLevel, AutoOutlineMaxSensitivityLevel);
        if (contracted != sensitivityLevel && contracted != expanded)
        {
            yield return contracted;
        }
    }

    private static Point FindInteriorSeedPoint(Point[] polygon, Point candidate)
    {
        if (polygon.Length < 3)
        {
            return candidate;
        }

        if (IsPointInsideOutlinePolygon(candidate, polygon))
        {
            return candidate;
        }

        Point center = GetPolygonCenter(polygon);
        if (IsPointInsideOutlinePolygon(center, polygon))
        {
            return center;
        }

        for (int step = 1; step <= 8; step++)
        {
            double t = step / 8.0;
            Point probe = new(
                candidate.X + ((center.X - candidate.X) * t),
                candidate.Y + ((center.Y - candidate.Y) * t));
            if (IsPointInsideOutlinePolygon(probe, polygon))
            {
                return probe;
            }
        }

        return center;
    }

    private static bool IsPointInsideOutlinePolygon(Point point, Point[] polygon)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Length - 1; index < polygon.Length; previous = index++)
        {
            Point currentPoint = polygon[index];
            Point previousPoint = polygon[previous];
            bool intersects = ((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y)) &&
                              (point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / Math.Max(double.Epsilon, previousPoint.Y - currentPoint.Y)) + currentPoint.X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static Rect GetPolygonBounds(Point[] polygon)
    {
        if (polygon.Length == 0)
        {
            return default;
        }

        double minX = polygon.Min(point => point.X);
        double maxX = polygon.Max(point => point.X);
        double minY = polygon.Min(point => point.Y);
        double maxY = polygon.Max(point => point.Y);
        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    private static double ComputePolygonBoundsOverlap(Point[] firstPolygon, Point[] secondPolygon)
    {
        Rect first = GetPolygonBounds(firstPolygon);
        Rect second = GetPolygonBounds(secondPolygon);
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        double intersection = (right - left) * (bottom - top);
        double union = Math.Max(1.0, first.Width * first.Height + second.Width * second.Height - intersection);
        return intersection / union;
    }

    private static bool IsVolumeContourCandidatePreferred(VolumeRoiContour[] candidateContours, VolumeRoiContour[] currentContours)
    {
        int candidateSliceCount = candidateContours.Select(contour => contour.PlanePosition).Distinct().Count();
        int currentSliceCount = currentContours.Select(contour => contour.PlanePosition).Distinct().Count();
        if (currentSliceCount > 0 && candidateSliceCount > Math.Max(currentSliceCount + 6, (int)Math.Round(currentSliceCount * 1.6)))
        {
            return false;
        }

        if (candidateSliceCount != currentSliceCount)
        {
            return candidateSliceCount > currentSliceCount;
        }

        if (candidateContours.Length != currentContours.Length)
        {
            return candidateContours.Length > currentContours.Length;
        }

        double candidateAnchorCount = candidateContours.Sum(contour => contour.Anchors.Length);
        double currentAnchorCount = currentContours.Sum(contour => contour.Anchors.Length);
        return candidateAnchorCount > currentAnchorCount;
    }

    private bool TryComputeAutoOutlineIntensitySignature(AutoOutlineSliceSource source, Point[] polygon, out AutoOutlineIntensitySignature signature)
    {
        signature = default;
        if (polygon.Length < 3)
        {
            return false;
        }

        Rect bounds = GetPolygonBounds(polygon);
        int left = Math.Max(0, (int)Math.Floor(bounds.X));
        int top = Math.Max(0, (int)Math.Floor(bounds.Y));
        int right = Math.Min(source.Width - 1, (int)Math.Ceiling(bounds.Right));
        int bottom = Math.Min(source.Height - 1, (int)Math.Ceiling(bounds.Bottom));
        if (right < left || bottom < top)
        {
            return false;
        }

        int boundsWidth = right - left + 1;
        int boundsHeight = bottom - top + 1;
        int approximateBoundsPixelCount = boundsWidth * boundsHeight;
        int stride = approximateBoundsPixelCount <= AutoOutlineIntensitySignatureTargetSamples
            ? 1
            : Math.Max(1, (int)Math.Floor(Math.Sqrt(approximateBoundsPixelCount / (double)AutoOutlineIntensitySignatureTargetSamples)));
        if (!TryCollectIntensitySignatureValues(source, polygon, left, top, right, bottom, stride, out List<double> values) &&
            (stride <= 1 || !TryCollectIntensitySignatureValues(source, polygon, left, top, right, bottom, 1, out values)))
        {
            return false;
        }

        values.Sort();
        double mean = values.Average();
        double variance = values.Average(value => (value - mean) * (value - mean));
        double median = values.Count % 2 == 0
            ? (values[(values.Count / 2) - 1] + values[values.Count / 2]) * 0.5
            : values[values.Count / 2];
        double p10 = InterpolatePercentile(values, 0.10);
        double p90 = InterpolatePercentile(values, 0.90);
        signature = new AutoOutlineIntensitySignature(mean, median, Math.Sqrt(Math.Max(variance, 0)), values[0], values[^1], p10, p90, values.Count);
        return true;
    }

    private bool TryCollectIntensitySignatureValues(
        AutoOutlineSliceSource source,
        Point[] polygon,
        int left,
        int top,
        int right,
        int bottom,
        int stride,
        out List<double> values)
    {
        values = [];
        int effectiveStride = Math.Max(1, stride);
        int startYOffset = effectiveStride > 1 ? effectiveStride / 2 : 0;
        int startXOffset = effectiveStride > 1 ? effectiveStride / 2 : 0;

        for (int y = top + startYOffset; y <= bottom; y += effectiveStride)
        {
            for (int x = left + startXOffset; x <= right; x += effectiveStride)
            {
                if (!IsPointInsidePolygon(new Point(x + 0.5, y + 0.5), polygon))
                {
                    continue;
                }

                if (!TryGetHomogenizedPixelValue(source, x, y, out double value) &&
                    !TryGetSlicePixelValue(source, x, y, out value))
                {
                    continue;
                }

                values.Add(value);
            }
        }

        return values.Count >= AutoOutlineMinPixels;
    }

    private bool IsIntensitySignatureCompatible(
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature candidateSignature,
        AutoOutlineIntensitySignature seedSignature)
    {
        double meanTolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.35, 14.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double medianTolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.15, 12.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double p10Tolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.55, 16.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double p90Tolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.55, 16.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);

        return Math.Abs(candidateSignature.Mean - referenceSignature.Mean) <= meanTolerance &&
            Math.Abs(candidateSignature.Median - referenceSignature.Median) <= medianTolerance &&
            Math.Abs(candidateSignature.Percentile10 - seedSignature.Percentile10) <= p10Tolerance &&
            Math.Abs(candidateSignature.Percentile90 - seedSignature.Percentile90) <= p90Tolerance;
    }

    private double ComputeIntensitySignatureScore(
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature candidateSignature,
        AutoOutlineIntensitySignature seedSignature)
    {
        double meanTolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.35, 14.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double medianTolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.15, 12.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double p10Tolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.55, 16.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);
        double p90Tolerance = ComputeSignatureTolerance(referenceSignature, seedSignature, 1.55, 16.0, CurrentAutoOutlineDeveloperSettings.SliceSignatureToleranceScale);

        double meanPenalty = Math.Abs(candidateSignature.Mean - referenceSignature.Mean) / Math.Max(1.0, meanTolerance);
        double medianPenalty = Math.Abs(candidateSignature.Median - referenceSignature.Median) / Math.Max(1.0, medianTolerance);
        double lowerPenalty = Math.Abs(candidateSignature.Percentile10 - seedSignature.Percentile10) / Math.Max(1.0, p10Tolerance);
        double upperPenalty = Math.Abs(candidateSignature.Percentile90 - seedSignature.Percentile90) / Math.Max(1.0, p90Tolerance);
        return 2.4 - (meanPenalty * 0.9) - (medianPenalty * 0.8) - (lowerPenalty * 0.35) - (upperPenalty * 0.45);
    }

    private static double ComputeSignatureTolerance(
        AutoOutlineIntensitySignature referenceSignature,
        AutoOutlineIntensitySignature seedSignature,
        double stdDevWeight,
        double minimumTolerance,
        double developerScale)
    {
        double dynamicRange = Math.Max(
            referenceSignature.Percentile90 - referenceSignature.Percentile10,
            seedSignature.Percentile90 - seedSignature.Percentile10);
        double stdDev = Math.Max(referenceSignature.StandardDeviation, seedSignature.StandardDeviation);
        return Math.Max(1.0, Math.Max(minimumTolerance, Math.Max(stdDev * stdDevWeight, dynamicRange * 0.55)) * Math.Max(0.2, developerScale));
    }

    private static VolumeRoiContour CreateVolumeRoiContour(DicomSpatialMetadata metadata, Point[] polygon, int componentId)
    {
        MeasurementAnchor[] anchors = polygon
            .Select(point => new MeasurementAnchor(point, metadata.PatientPointFromPixel(point)))
            .ToArray();
        return new VolumeRoiContour(
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
            componentId);
    }

    private static bool TryGetSlicePixelValue(AutoOutlineSliceSource source, int x, int y, out double value)
    {
        value = 0;
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height)
        {
            return false;
        }

        int index = (y * source.Width) + x;
        if ((uint)index >= (uint)source.Pixels.Length)
        {
            return false;
        }

        value = source.Pixels.Span[index];
        return true;
    }

    private bool[,] SmoothAutoOutlineMask(bool[,] included, int seedX, int seedY, out int pixelCount)
    {
        int width = included.GetLength(0);
        int height = included.GetLength(1);
        bool[,] smoothed = new bool[width, height];
        pixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int neighbors = 0;
                int active = 0;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int nextX = x + offsetX;
                        int nextY = y + offsetY;
                        if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height)
                        {
                            continue;
                        }

                        neighbors++;
                        if (included[nextX, nextY])
                        {
                            active++;
                        }
                    }
                }

                smoothed[x, y] = included[x, y]
                    ? active >= Math.Min(4, neighbors)
                    : active >= Math.Min(6, neighbors);
            }
        }

        if ((uint)seedX < (uint)width && (uint)seedY < (uint)height)
        {
            smoothed[seedX, seedY] = true;
        }

        bool[,] connected = RetainSeedConnectedMask(smoothed, seedX, seedY, out pixelCount);
        return connected;
    }

    private static bool[,] RetainSeedConnectedMask(bool[,] mask, int seedX, int seedY, out int pixelCount)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] connected = new bool[width, height];
        pixelCount = 0;
        if ((uint)seedX >= (uint)width || (uint)seedY >= (uint)height || !mask[seedX, seedY])
        {
            return connected;
        }

        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((seedX, seedY));
        visited[seedX, seedY] = true;

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            if (!mask[x, y])
            {
                continue;
            }

            connected[x, y] = true;
            pixelCount++;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        return connected;
    }

    private double GetHomogenizedVoxelValue(int x, int y, int z)
    {
        if (_volume is null)
        {
            return 0;
        }

        // Sample the 3×3×3 neighborhood once (27 voxels) into a stack-allocated buffer.
        Span<short> rawSamples = stackalloc short[27];
        int sampleCount = 0;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            int sampleZ = Math.Clamp(z + offsetZ, 0, _volume.SizeZ - 1);
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int sampleY = Math.Clamp(y + offsetY, 0, _volume.SizeY - 1);
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int sampleX = Math.Clamp(x + offsetX, 0, _volume.SizeX - 1);
                    rawSamples[sampleCount++] = _volume.GetVoxel(sampleX, sampleY, sampleZ);
                }
            }
        }

        if (sampleCount == 0)
        {
            return 0;
        }

        Span<short> sorted = rawSamples[..sampleCount];
        sorted.Sort();

        // Compute robust clamp bounds (inline, no allocation).
        double median = sorted[sampleCount / 2];
        double q1 = sorted[sampleCount / 4];
        double q3 = sorted[(sampleCount * 3) / 4];
        double iqr = Math.Max(0, q3 - q1);

        // Compute MAD in-place using a stack-allocated scratch buffer.
        Span<double> deviations = stackalloc double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            deviations[i] = Math.Abs(sorted[i] - median);
        deviations.Sort();
        double mad = deviations[sampleCount / 2];

        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        bool isCtLike = modality is "CT" or "CTA";
        double positiveRange = isCtLike
            ? Math.Max(8.0, Math.Max(mad * AutoOutlineCtPositiveOutlierMadMultiplier, iqr * 0.55))
            : Math.Max(6.0, Math.Max(mad * AutoOutlineGenericOutlierMadMultiplier, iqr * 0.70));
        double negativeRange = isCtLike
            ? Math.Max(10.0, Math.Max(mad * AutoOutlineCtNegativeOutlierMadMultiplier, iqr * 0.85))
            : Math.Max(6.0, Math.Max(mad * AutoOutlineGenericOutlierMadMultiplier, iqr * 0.70));
        double clampLower = median - negativeRange;
        double clampUpper = median + positiveRange;

        // Weighted average with distance-from-center weighting (reuses rawSamples, no re-read).
        double weightedSum = 0;
        double weightTotal = 0;
        int idx = 0;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            int weightZ = offsetZ == 0 ? 2 : 1;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int weightY = offsetY == 0 ? 2 : 1;
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int weightX = offsetX == 0 ? 2 : 1;
                    int weight = weightX * weightY * weightZ;
                    weightedSum += Math.Clamp((double)rawSamples[idx++], clampLower, clampUpper) * weight;
                    weightTotal += weight;
                }
            }
        }

        return weightTotal <= 0 ? median : weightedSum / weightTotal;
    }

    private static double ComputeInterQuartileRange(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. values.OrderBy(value => value)];
        double q1 = InterpolatePercentile(ordered, 0.25);
        double q3 = InterpolatePercentile(ordered, 0.75);
        return Math.Max(0, q3 - q1);
    }

    private static double ComputeMedianAbsoluteDeviation(IReadOnlyList<double> orderedValues)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        double median = InterpolatePercentile(orderedValues, 0.5);
        double[] deviations = new double[orderedValues.Count];
        for (int index = 0; index < orderedValues.Count; index++)
        {
            deviations[index] = Math.Abs(orderedValues[index] - median);
        }

        Array.Sort(deviations);
        return InterpolatePercentile(deviations, 0.5);
    }

    private static (double Lower, double Upper) ComputeRobustHomogenizationBounds(List<double> values, string modality)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        double[] orderedValues = [.. values.OrderBy(value => value)];
        double median = InterpolatePercentile(orderedValues, 0.5);
        double interQuartileRange = Math.Max(0, InterpolatePercentile(orderedValues, 0.75) - InterpolatePercentile(orderedValues, 0.25));
        double medianAbsoluteDeviation = ComputeMedianAbsoluteDeviation(orderedValues);
        bool isCtLike = modality is "CT" or "CTA";

        double positiveRange = isCtLike
            ? Math.Max(8.0, Math.Max(medianAbsoluteDeviation * AutoOutlineCtPositiveOutlierMadMultiplier, interQuartileRange * 0.55))
            : Math.Max(6.0, Math.Max(medianAbsoluteDeviation * AutoOutlineGenericOutlierMadMultiplier, interQuartileRange * 0.70));
        double negativeRange = isCtLike
            ? Math.Max(10.0, Math.Max(medianAbsoluteDeviation * AutoOutlineCtNegativeOutlierMadMultiplier, interQuartileRange * 0.85))
            : Math.Max(6.0, Math.Max(medianAbsoluteDeviation * AutoOutlineGenericOutlierMadMultiplier, interQuartileRange * 0.70));

        return (median - negativeRange, median + positiveRange);
    }

    private static double InterpolatePercentile(IReadOnlyList<double> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        double position = clamped * (orderedValues.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return orderedValues[lower];
        }

        double t = position - lower;
        return orderedValues[lower] + ((orderedValues[upper] - orderedValues[lower]) * t);
    }

    /// <summary>
    /// Per-slice voxel index built by <see cref="PreIndexRegionBySlice"/>.
    /// Stores the 2-D bounding box and per-voxel (col, row) coordinates for
    /// a single slice so that contour extraction can skip empty slices entirely
    /// and only iterate the tight bounding box.
    /// </summary>
    private readonly struct SliceVoxelIndex(int minCol, int maxCol, int minRow, int maxRow, List<(int Col, int Row)> pixels)
    {
        public readonly int MinCol = minCol;
        public readonly int MaxCol = maxCol;
        public readonly int MinRow = minRow;
        public readonly int MaxRow = maxRow;
        public readonly List<(int Col, int Row)> Pixels = pixels;
    }

    /// <summary>
    /// Pre-indexes the voxel-key region into per-slice buckets in a single O(N) pass.
    /// For axial orientation the slice key is Z, column is X, row is Y.
    /// For coronal it is Y/X/Z; for sagittal it is X/Y/Z.
    /// Returns a dictionary mapping slice index → <see cref="SliceVoxelIndex"/>.
    /// </summary>
    private Dictionary<int, SliceVoxelIndex> PreIndexRegionBySlice(HashSet<int> region)
    {
        if (_volume is null)
        {
            return [];
        }

        int sizeX = _volume.SizeX;
        int sizeY = _volume.SizeY;

        // Temporary mutable accumulators per slice.
        Dictionary<int, (int minCol, int maxCol, int minRow, int maxRow, List<(int, int)> pixels)> acc = [];

        foreach (int key in region)
        {
            DecodeVoxelKey(key, sizeX, sizeY, out int x, out int y, out int z);

            // Map (x, y, z) to (sliceIndex, col, row) depending on orientation.
            int sliceIndex, col, row;
            switch (_volumeOrientation)
            {
                case SliceOrientation.Axial:
                    sliceIndex = z; col = x; row = y;
                    break;
                case SliceOrientation.Coronal:
                    sliceIndex = y; col = x; row = z; // row will be remapped later
                    break;
                case SliceOrientation.Sagittal:
                    sliceIndex = x; col = y; row = z; // row will be remapped later
                    break;
                default:
                    continue;
            }

            if (!acc.TryGetValue(sliceIndex, out var bucket))
            {
                bucket = (col, col, row, row, []);
                acc[sliceIndex] = bucket;
            }
            else
            {
                bucket.minCol = Math.Min(bucket.minCol, col);
                bucket.maxCol = Math.Max(bucket.maxCol, col);
                bucket.minRow = Math.Min(bucket.minRow, row);
                bucket.maxRow = Math.Max(bucket.maxRow, row);
                acc[sliceIndex] = bucket;
            }

            bucket.pixels.Add((col, row));
        }

        // Convert to immutable index.
        Dictionary<int, SliceVoxelIndex> result = new(acc.Count);
        foreach ((int sliceIdx, var bucket) in acc)
        {
            result[sliceIdx] = new SliceVoxelIndex(bucket.minCol, bucket.maxCol, bucket.minRow, bucket.maxRow, bucket.pixels);
        }

        return result;
    }

    /// <summary>
    /// Builds a tight 2-D boolean mask from a pre-indexed slice.
    /// For coronal / sagittal orientations the row coordinate is the raw Z index,
    /// which needs remapping through <see cref="MapOutputRowToSourceZ"/>.
    /// Returns the mask in image-space coordinates (col, row) and the origin
    /// offset (left, top) so boundary points can be translated back.
    /// </summary>
    private bool TryBuildSliceMaskFromPreIndex(
        SliceVoxelIndex index,
        out bool[,] mask,
        out int left,
        out int top,
        out int setCount)
    {
        mask = new bool[1, 1];
        left = 0;
        top = 0;
        setCount = 0;

        if (_volume is null)
        {
            return false;
        }

        bool needsRowRemap = _volumeOrientation is SliceOrientation.Coronal or SliceOrientation.Sagittal;
        double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
        int outputHeight = needsRowRemap
            ? GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY)
            : 0; // not used for axial

        // For coronal/sagittal, the stored row is the raw Z.  We need to map
        // each Z → output row and recompute the bounding box in output space.
        int minCol = index.MinCol;
        int maxCol = index.MaxCol;
        int minRow, maxRow;

        if (needsRowRemap)
        {
            // Build a lookup table from source-Z → output-row.
            // The output row range is [0 .. outputHeight-1].
            minRow = int.MaxValue;
            maxRow = int.MinValue;
            foreach ((int _, int rawZ) in index.Pixels)
            {
                int outputRow = MapSourceZToOutputRow(rawZ, outputHeight, _volume.SizeZ);
                if (outputRow < minRow) minRow = outputRow;
                if (outputRow > maxRow) maxRow = outputRow;
            }

            if (minRow > maxRow)
            {
                return false;
            }
        }
        else
        {
            minRow = index.MinRow;
            maxRow = index.MaxRow;
        }

        left = minCol;
        top = minRow;
        int width = maxCol - minCol + 1;
        int height = maxRow - minRow + 1;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        mask = new bool[width, height];

        if (needsRowRemap)
        {
            foreach ((int col, int rawZ) in index.Pixels)
            {
                int outputRow = MapSourceZToOutputRow(rawZ, outputHeight, _volume.SizeZ);
                mask[col - left, outputRow - top] = true;
            }
        }
        else
        {
            foreach ((int col, int row) in index.Pixels)
            {
                mask[col - left, row - top] = true;
            }
        }

        setCount = index.Pixels.Count;
        return setCount >= AutoOutlineMinPixels;
    }

    /// <summary>Inverse of <see cref="MapOutputRowToSourceZ"/>.</summary>
    private static int MapSourceZToOutputRow(int sourceZ, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return 0;
        }

        // MapOutputRowToSourceZ: z = (outputHeight-1-row) * (sourceDepth-1) / (outputHeight-1)
        // Inverse: row = outputHeight-1 - z * (outputHeight-1) / (sourceDepth-1)
        return Math.Clamp((int)Math.Round(outputHeight - 1 - (sourceZ * (outputHeight - 1) / (double)(sourceDepth - 1))), 0, outputHeight - 1);
    }

    private VolumeRoiContour[] BuildAutoOutlinedVolumeContours(HashSet<int> region)
    {
        if (_volume is null || SpatialMetadata is null)
        {
            return [];
        }

        // ── Pre-index: one O(N) pass over the region ────────────────────────
        Dictionary<int, SliceVoxelIndex> sliceIndex = PreIndexRegionBySlice(region);
        if (sliceIndex.Count == 0)
        {
            return [];
        }

        int sliceCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
        List<VolumeRoiContour> contours = [];
        List<VolumeSliceComponentState> previousSliceComponents = [];
        int nextComponentId = 0;

        for (int si = 0; si < sliceCount; si++)
        {
            if (!sliceIndex.TryGetValue(si, out SliceVoxelIndex idx))
            {
                // No voxels on this slice — fast skip, reset component tracking.
                if (previousSliceComponents.Count > 0)
                    previousSliceComponents.Clear();
                continue;
            }

            if (!TryBuildSliceMaskFromPreIndex(idx, out bool[,] sliceMask, out int maskLeft, out int maskTop, out int setCount)
                || setCount < AutoOutlineMinPixels)
            {
                previousSliceComponents.Clear();
                continue;
            }

            AutoOutlineMask[] componentMasks = ExtractConnectedSliceMasks(sliceMask, maskLeft, maskTop);
            if (componentMasks.Length == 0)
            {
                previousSliceComponents.Clear();
                continue;
            }

            DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(_volume, _volumeOrientation, si);
            List<VolumeSliceContourCandidate> currentCandidates = [];
            foreach (AutoOutlineMask componentMask in componentMasks.OrderByDescending(mask => mask.Count))
            {
                Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(0, 0, componentMask.Pixels, componentMask.Count), maxPointCount: CurrentAutoOutlineDeveloperSettings.VolumeContourPointBudget)
                    .Select(point => new Point(point.X + componentMask.Left, point.Y + componentMask.Top))
                    .ToArray();
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
            foreach (VolumeSliceContourCandidate candidate in currentCandidates.OrderByDescending(candidate => candidate.Mask.Count))
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
                contours.Add(new VolumeRoiContour(
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

        return contours.ToArray();
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

    private static AutoOutlineMask[] ExtractConnectedSliceMasks(bool[,] mask, int offsetLeft, int offsetTop)
    {
        AutoOutlineMask[] locals = ExtractConnectedSliceMasks(mask);
        if (offsetLeft == 0 && offsetTop == 0)
        {
            return locals;
        }

        for (int i = 0; i < locals.Length; i++)
        {
            AutoOutlineMask m = locals[i];
            locals[i] = new AutoOutlineMask(m.Left + offsetLeft, m.Top + offsetTop, m.Pixels, m.Count);
        }

        return locals;
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

    private bool TryBuildSliceMaskFromRegion(HashSet<int> region, int sliceIndex, out bool[,] mask)
    {
        mask = new bool[1, 1];
        if (_volume is null)
        {
            return false;
        }

        switch (_volumeOrientation)
        {
            case SliceOrientation.Axial:
                mask = new bool[_volume.SizeX, _volume.SizeY];
                bool[,] axialMask = mask;
                if (_volume.SizeX * _volume.SizeY >= 65536)
                {
                    Parallel.For(0, _volume.SizeY, y =>
                    {
                        for (int x = 0; x < _volume.SizeX; x++)
                        {
                            axialMask[x, y] = region.Contains(GetVoxelKey(x, y, sliceIndex, _volume.SizeX, _volume.SizeY));
                        }
                    });
                }
                else
                {
                    for (int y = 0; y < _volume.SizeY; y++)
                    {
                        for (int x = 0; x < _volume.SizeX; x++)
                        {
                            axialMask[x, y] = region.Contains(GetVoxelKey(x, y, sliceIndex, _volume.SizeX, _volume.SizeY));
                        }
                    }
                }
                break;
            case SliceOrientation.Coronal:
            {
                int width = _volume.SizeX;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                bool[,] coronalMask = mask;
                if (width * height >= 65536)
                {
                    Parallel.For(0, height, row =>
                    {
                        int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                        for (int x = 0; x < width; x++)
                        {
                            coronalMask[x, row] = region.Contains(GetVoxelKey(x, sliceIndex, z, _volume.SizeX, _volume.SizeY));
                        }
                    });
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                        for (int x = 0; x < width; x++)
                        {
                            coronalMask[x, row] = region.Contains(GetVoxelKey(x, sliceIndex, z, _volume.SizeX, _volume.SizeY));
                        }
                    }
                }
                break;
            }
            case SliceOrientation.Sagittal:
            {
                int width = _volume.SizeY;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                bool[,] sagittalMask = mask;
                if (width * height >= 65536)
                {
                    Parallel.For(0, height, row =>
                    {
                        int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                        for (int y = 0; y < width; y++)
                        {
                            sagittalMask[y, row] = region.Contains(GetVoxelKey(sliceIndex, y, z, _volume.SizeX, _volume.SizeY));
                        }
                    });
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                        for (int y = 0; y < width; y++)
                        {
                            sagittalMask[y, row] = region.Contains(GetVoxelKey(sliceIndex, y, z, _volume.SizeX, _volume.SizeY));
                        }
                    }
                }
                break;
            }
            default:
                return false;
        }

        int setCount = 0;
        foreach (bool included in mask)
        {
            if (included)
            {
                setCount++;
            }
        }

        return setCount >= AutoOutlineMinPixels;
    }

    private static int GetResampledDepth(int sourceDepth, double sourceSpacing, double targetSpacing)
    {
        if (sourceDepth <= 1 || sourceSpacing <= 0 || targetSpacing <= 0)
        {
            return sourceDepth;
        }

        double physicalDepth = (sourceDepth - 1) * sourceSpacing;
        return Math.Max(1, (int)Math.Round(physicalDepth / targetSpacing) + 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return 0;
        }

        return (outputHeight - 1 - row) * (sourceDepth - 1) / (double)(outputHeight - 1);
    }

    private VolumeSeedCandidate[] BuildVolumeSeedCandidates(
        int seedX,
        int seedY,
        int seedZ,
        short seedValue,
        double homogenizedSeedValue,
        double tolerance,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        Dictionary<int, double> gradientCache)
    {
        if (_volume is null)
        {
            return [];
        }

        double radiusMultiplier = relaxedPass ? 1.2 : 1.0;
        double seedNeighborhoodRadiusMm = Math.Max(1.0, CurrentAutoOutlineDeveloperSettings.SeedNeighborhoodRadiusMm);
        int radiusX = Math.Max(1, (int)Math.Round((seedNeighborhoodRadiusMm * radiusMultiplier) / Math.Max(_volume.SpacingX, 0.1)));
        int radiusY = Math.Max(1, (int)Math.Round((seedNeighborhoodRadiusMm * radiusMultiplier) / Math.Max(_volume.SpacingY, 0.1)));
        int radiusZ = Math.Max(1, (int)Math.Round((seedNeighborhoodRadiusMm * 0.7 * radiusMultiplier) / Math.Max(_volume.SpacingZ, 0.1)));
        List<(VolumeSeedCandidate Candidate, double Score)> candidates = [];

        for (int z = Math.Max(0, seedZ - radiusZ); z <= Math.Min(_volume.SizeZ - 1, seedZ + radiusZ); z++)
        {
            for (int y = Math.Max(0, seedY - radiusY); y <= Math.Min(_volume.SizeY - 1, seedY + radiusY); y++)
            {
                for (int x = Math.Max(0, seedX - radiusX); x <= Math.Min(_volume.SizeX - 1, seedX + radiusX); x++)
                {
                    double normalizedDistance =
                        Math.Pow((x - seedX) / (double)Math.Max(radiusX, 1), 2) +
                        Math.Pow((y - seedY) / (double)Math.Max(radiusY, 1), 2) +
                        Math.Pow((z - seedZ) / (double)Math.Max(radiusZ, 1), 2);
                    if (normalizedDistance > 1.0)
                    {
                        continue;
                    }

                    short rawValue = _volume.GetVoxel(x, y, z);
                    double homogenizedValue = GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache);
                    double rawDelta = Math.Abs(rawValue - seedValue);
                    double homogenizedDelta = Math.Abs(homogenizedValue - homogenizedSeedValue);
                    double localGradient = EstimateVoxelGradientMagnitude(x, y, z, gradientCache, homogenizedCache);
                    int neighborhoodSupport = CountVolumeNeighborhoodSupport(x, y, z, homogenizedValue, homogenizedSeedValue, tolerance, homogenizedCache);
                    bool isMrLike = IsMrLikeModality();
                    bool isCtLike = IsCtLikeModality();
                    double deltaLimit = tolerance * (isMrLike ? (relaxedPass ? 1.55 : 1.25) : (relaxedPass ? 1.15 : 0.85));
                    if (rawDelta > deltaLimit && homogenizedDelta > deltaLimit)
                    {
                        continue;
                    }

                    double supportPenalty = Math.Max(0, (isMrLike ? 5 : 7) - neighborhoodSupport);
                    double ctHighDensityDelta = isCtLike
                        ? Math.Max(rawValue, homogenizedValue) - Math.Max(seedValue, homogenizedSeedValue)
                        : 0;
                    if (isCtLike &&
                        ctHighDensityDelta > Math.Max(28.0, tolerance * (relaxedPass ? 0.95 : 0.75)) &&
                        neighborhoodSupport < (relaxedPass ? 10 : 12))
                    {
                        continue;
                    }

                    double brightOutlierPenalty = isCtLike
                        ? ComputePositiveOutlierPenalty(homogenizedValue, homogenizedSeedValue, tolerance * (relaxedPass ? 0.42 : 0.32)) * 2.4
                        : 0;
                    double score = (homogenizedDelta * 0.50) + (rawDelta * 0.20) + (localGradient * (isMrLike ? 0.08 : 0.15)) + (normalizedDistance * tolerance * 0.18) + (supportPenalty * (isMrLike ? 0.9 : 1.6)) + brightOutlierPenalty;
                    candidates.Add((new VolumeSeedCandidate(x, y, z, rawValue, homogenizedValue), score));
                }
            }
        }

        if (candidates.Count == 0)
        {
            candidates.Add((new VolumeSeedCandidate(seedX, seedY, seedZ, seedValue, homogenizedSeedValue), 0));
        }

        List<VolumeSeedCandidate> selected = [];
        foreach ((VolumeSeedCandidate candidate, _) in candidates.OrderBy(entry => entry.Score))
        {
            if (selected.Count >= AutoOutlineMaxSeedCandidates)
            {
                break;
            }

            bool sufficientlySeparated = selected.Count == 0 || selected.All(existing =>
                Math.Abs(existing.X - candidate.X) + Math.Abs(existing.Y - candidate.Y) + Math.Abs(existing.Z - candidate.Z) >= 2);
            if (!sufficientlySeparated)
            {
                continue;
            }

            selected.Add(candidate);
        }

        if (selected.All(candidate => candidate.X != seedX || candidate.Y != seedY || candidate.Z != seedZ))
        {
            selected.Insert(0, new VolumeSeedCandidate(seedX, seedY, seedZ, seedValue, homogenizedSeedValue));
        }

        return [.. selected.Take(AutoOutlineMaxSeedCandidates)];
    }

    private bool IsAdaptiveVolumeVoxelAccepted(
        VolumeGrowNode node,
        short rawValue,
        double homogenizedValue,
        VolumeSeedCandidate seedCandidate,
        VolumeGrowthStatistics stats,
        bool relaxedPass,
        Dictionary<int, double> gradientCache,
        Dictionary<int, double> homogenizedCache)
    {
        bool isMrLike = IsMrLikeModality();
        bool isCtLike = IsCtLikeModality();
        double adaptiveTolerance = Math.Max(stats.BaseTolerance * 0.82, (stats.StdDev * AutoOutlineAdaptiveStdDevWeight) + 4.0);
        if (isMrLike)
        {
            adaptiveTolerance *= relaxedPass ? 1.45 : 1.22;
        }
        else if (relaxedPass)
        {
            adaptiveTolerance *= 1.15;
        }

        double seedDelta = Math.Abs(homogenizedValue - seedCandidate.HomogenizedValue);
        double rawSeedDelta = Math.Abs(rawValue - seedCandidate.RawValue);
        double regionDelta = Math.Abs(homogenizedValue - stats.Mean);
        double parentDelta = Math.Abs(homogenizedValue - node.ParentHomogenizedValue);
        double rawParentDelta = Math.Abs(rawValue - node.ParentRawValue);
        double localDelta = Math.Abs(homogenizedValue - stats.LocalMean);
        double boundaryPenalty = EstimateVoxelGradientMagnitude(node.X, node.Y, node.Z, gradientCache, homogenizedCache);
        int neighborhoodSupport = CountVolumeNeighborhoodSupport(node.X, node.Y, node.Z, homogenizedValue, seedCandidate.HomogenizedValue, stats.BaseTolerance, homogenizedCache);
        double brightOutlierPenalty = isCtLike
            ? ComputePositiveOutlierPenalty(homogenizedValue, Math.Max(stats.Mean, Math.Max(stats.LocalMean, seedCandidate.HomogenizedValue)), adaptiveTolerance * (relaxedPass ? 0.62 : 0.46))
            : 0;
        double boundaryLimit = Math.Max(adaptiveTolerance * (isMrLike ? 1.2 : 0.95), stats.BaseTolerance * (isMrLike ? 1.35 : 1.1));
        int minimumSupport = stats.Count < 8
            ? (isMrLike ? 3 : 4)
            : relaxedPass
                ? (isMrLike ? 4 : 5)
                : (isMrLike ? 5 : 7);
        bool hasNeighborhoodSupport = neighborhoodSupport >= minimumSupport;
        double ctHighDensityDelta = isCtLike
            ? Math.Max(rawValue, homogenizedValue) - Math.Max(seedCandidate.RawValue, Math.Max(stats.Mean, stats.LocalMean))
            : 0;

        if (boundaryPenalty > boundaryLimit * (isMrLike ? 2.2 : 1.8) && seedDelta > adaptiveTolerance * (isMrLike ? 0.9 : 0.55))
        {
            return false;
        }

        if (!hasNeighborhoodSupport && boundaryPenalty > boundaryLimit * (isMrLike ? 1.25 : 0.95))
        {
            return false;
        }

        if (isCtLike && brightOutlierPenalty > 0)
        {
            bool strongPositiveOutlier = brightOutlierPenalty > adaptiveTolerance * (relaxedPass ? 0.45 : 0.32);
            if (strongPositiveOutlier && (!hasNeighborhoodSupport || boundaryPenalty > boundaryLimit * 0.8))
            {
                return false;
            }

            if (ctHighDensityDelta > Math.Max(32.0, adaptiveTolerance * (relaxedPass ? 1.0 : 0.78)) &&
                (!hasNeighborhoodSupport || boundaryPenalty > boundaryLimit * 0.72))
            {
                return false;
            }
        }

        if (stats.Count < 8)
        {
            return (seedDelta <= stats.BaseTolerance * (isMrLike ? 1.5 : 1.1) || rawSeedDelta <= stats.BaseTolerance * (isMrLike ? 1.25 : 0.95))
                && (hasNeighborhoodSupport || boundaryPenalty <= boundaryLimit * 0.9)
                && (!isCtLike || brightOutlierPenalty <= adaptiveTolerance * 0.24);
        }

        bool fitsRegion = regionDelta <= adaptiveTolerance;
        bool fitsSeed = seedDelta <= stats.BaseTolerance * (isMrLike ? 1.7 : 1.2) || rawSeedDelta <= stats.BaseTolerance * (isMrLike ? 1.35 : 0.9);
        bool fitsParent = parentDelta <= Math.Max(adaptiveTolerance * (isMrLike ? 0.9 : 0.65), isMrLike ? 10.0 : 6.0) || rawParentDelta <= Math.Max(stats.BaseTolerance * (isMrLike ? 0.9 : 0.55), isMrLike ? 7.0 : 4.0);
        bool fitsLocalMedian = localDelta <= Math.Max(stats.BaseTolerance * (isMrLike ? 1.3 : 0.95), adaptiveTolerance * (isMrLike ? 1.05 : 0.8));

        return fitsRegion
            && (fitsSeed || fitsParent || fitsLocalMedian)
            && (hasNeighborhoodSupport || (fitsSeed && boundaryPenalty <= boundaryLimit * 0.9))
            && (!isCtLike || brightOutlierPenalty <= adaptiveTolerance * (relaxedPass ? 0.55 : 0.34) || (fitsParent && hasNeighborhoodSupport));
    }

    private int CountVolumeNeighborhoodSupport(
        int x,
        int y,
        int z,
        double targetValue,
        double seedValue,
        double tolerance,
        Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int support = 0;
        double supportTolerance = Math.Max(6.0, tolerance * 0.42);
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0 && offsetZ == 0)
                    {
                        continue;
                    }

                    int sampleX = x + offsetX;
                    int sampleY = y + offsetY;
                    int sampleZ = z + offsetZ;
                    if ((uint)sampleX >= (uint)_volume.SizeX || (uint)sampleY >= (uint)_volume.SizeY || (uint)sampleZ >= (uint)_volume.SizeZ)
                    {
                        continue;
                    }

                    double sample = GetCachedHomogenizedVoxelValue(sampleX, sampleY, sampleZ, homogenizedCache);
                    double targetDelta = Math.Abs(sample - targetValue);
                    double seedDelta = Math.Abs(sample - seedValue);
                    if (targetDelta <= supportTolerance || seedDelta <= supportTolerance * 0.9)
                    {
                        support++;
                    }
                }
            }
        }

        return support;
    }

    private double ComputeVolumeGrowthPriority(
        VolumeSeedCandidate seedCandidate,
        VolumeGrowthStatistics stats,
        short rawValue,
        double homogenizedValue,
        short parentRawValue,
        double parentHomogenizedValue)
    {
        double brightOutlierCost = IsCtLikeModality()
            ? ComputePositiveOutlierPenalty(homogenizedValue, Math.Max(stats.Mean, seedCandidate.HomogenizedValue), Math.Max(8.0, stats.BaseTolerance * 0.4)) * 0.55
            : 0;
        double seedCost = Math.Abs(homogenizedValue - seedCandidate.HomogenizedValue);
        double regionCost = Math.Abs(homogenizedValue - stats.Mean);
        double parentCost = Math.Abs(homogenizedValue - parentHomogenizedValue) + (Math.Abs(rawValue - parentRawValue) * 0.35);
        return (parentCost * 0.5) + (regionCost * 0.35) + (seedCost * 0.15) + brightOutlierCost;
    }

    private static double ComputePositiveOutlierPenalty(double value, double referenceValue, double freeRange)
    {
        return Math.Max(0, value - referenceValue - Math.Max(1.0, freeRange));
    }

    private bool IsMrLikeModality()
    {
        return _cachedIsMrLike ??= ComputeIsMrLikeModality();
    }

    private bool ComputeIsMrLikeModality()
    {
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        return modality is "MR" or "MRI";
    }

    private bool IsCtLikeModality()
    {
        return _cachedIsCtLike ??= ComputeIsCtLikeModality();
    }

    private bool ComputeIsCtLikeModality()
    {
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        return modality is "CT" or "CTA";
    }

    private double GetVolumeToleranceMultiplier(bool relaxedPass)
    {
        double multiplier = Math.Max(0.2, CurrentAutoOutlineDeveloperSettings.VolumeToleranceScale);
        if (IsMrLikeModality())
        {
            multiplier *= AutoOutlineMrToleranceMultiplier;
        }

        if (relaxedPass)
        {
            multiplier *= AutoOutlineRelaxedToleranceMultiplier;
        }

        return multiplier;
    }

    private static AutoOutlineDeveloperSettings NormalizeAutoOutlineDeveloperSettings(AutoOutlineDeveloperSettings settings)
    {
        return settings with
        {
            TwoDimensionalToleranceScale = Math.Clamp(settings.TwoDimensionalToleranceScale, 0.25, 4.0),
            VolumeToleranceScale = Math.Clamp(settings.VolumeToleranceScale, 0.25, 4.0),
            SliceSignatureToleranceScale = Math.Clamp(settings.SliceSignatureToleranceScale, 0.25, 4.0),
            SeedNeighborhoodRadiusMm = Math.Clamp(settings.SeedNeighborhoodRadiusMm, 1.0, 20.0),
            PolygonPointBudget = Math.Clamp(settings.PolygonPointBudget, 12, 128),
            VolumeContourPointBudget = Math.Clamp(settings.VolumeContourPointBudget, 12, 128),
        };
    }

    private AutoOutlineDeveloperSettingsOverrideScope PushAutoOutlineDeveloperSettingsOverride(AutoOutlineDeveloperSettings settings)
    {
        AutoOutlineDeveloperSettings? previous = _autoOutlineDeveloperSettingsOverride.Value;
        _autoOutlineDeveloperSettingsOverride.Value = settings;
        return new AutoOutlineDeveloperSettingsOverrideScope(_autoOutlineDeveloperSettingsOverride, previous);
    }

    private readonly struct AutoOutlineDeveloperSettingsOverrideScope : IDisposable
    {
        private readonly AsyncLocal<AutoOutlineDeveloperSettings?> _overrideSlot;
        private readonly AutoOutlineDeveloperSettings? _previous;

        public AutoOutlineDeveloperSettingsOverrideScope(AsyncLocal<AutoOutlineDeveloperSettings?> overrideSlot, AutoOutlineDeveloperSettings? previous)
        {
            _overrideSlot = overrideSlot;
            _previous = previous;
        }

        public void Dispose()
        {
            _overrideSlot.Value = _previous;
        }
    }

    private HashSet<int> FuseVolumeSeedRegions(Dictionary<int, int> voteCounts, int successfulSeedCount, int seedKey)
    {
        HashSet<int> fused = [];
        int minVotes = successfulSeedCount switch
        {
            <= 2 => 1,
            <= 5 => 2,
            <= 9 => 3,
            _ => Math.Max(3, (int)Math.Ceiling(successfulSeedCount * 0.35)),
        };

        foreach ((int key, int votes) in voteCounts)
        {
            if (votes >= minVotes)
            {
                fused.Add(key);
            }
        }

        if (!fused.Contains(seedKey) && voteCounts.TryGetValue(seedKey, out int seedVotes) && seedVotes > 0)
        {
            fused.Add(seedKey);
        }

        if (fused.Count == 0)
        {
            int fallbackMinVotes = Math.Max(1, minVotes - 1);
            foreach ((int key, int votes) in voteCounts)
            {
                if (votes >= fallbackMinVotes)
                {
                    fused.Add(key);
                }
            }
        }

        if (_volume is not null && fused.Count > 0)
        {
            Queue<int> queue = new(fused);
            HashSet<int> expanded = [.. fused];
            while (queue.Count > 0)
            {
                int key = queue.Dequeue();
                DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
                Span<(int X, int Y, int Z)> neighbors =
                [
                    (x - 1, y, z), (x + 1, y, z),
                    (x, y - 1, z), (x, y + 1, z),
                    (x, y, z - 1), (x, y, z + 1),
                ];

                foreach ((int nextX, int nextY, int nextZ) in neighbors)
                {
                    if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                    {
                        continue;
                    }

                    int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                    if (expanded.Contains(nextKey) || !voteCounts.TryGetValue(nextKey, out int nextVotes) || nextVotes <= 0)
                    {
                        continue;
                    }

                    expanded.Add(nextKey);
                    queue.Enqueue(nextKey);
                }
            }

            fused = RetainSeedConnectedVolumeRegion(expanded, seedKey);
        }

        return fused;
    }

    private HashSet<int> RetainSeedConnectedVolumeRegion(HashSet<int> region, int seedKey)
    {
        if (_volume is null || region.Count == 0)
        {
            return [];
        }

        HashSet<int> connected = [];
        if (!region.Contains(seedKey))
        {
            seedKey = FindNearestRegionKey(region, seedKey, _volume.SizeX, _volume.SizeY);
            if (seedKey < 0)
            {
                return connected;
            }
        }

        Queue<int> queue = new();
        queue.Enqueue(seedKey);
        connected.Add(seedKey);

        while (queue.Count > 0)
        {
            int key = queue.Dequeue();
            DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
            Span<(int X, int Y, int Z)> neighbors =
            [
                (x - 1, y, z), (x + 1, y, z),
                (x, y - 1, z), (x, y + 1, z),
                (x, y, z - 1), (x, y, z + 1),
            ];

            foreach ((int nextX, int nextY, int nextZ) in neighbors)
            {
                if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                {
                    continue;
                }

                int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                if (!region.Contains(nextKey) || !connected.Add(nextKey))
                {
                    continue;
                }

                queue.Enqueue(nextKey);
            }
        }

        return connected;
    }

    private HashSet<int> SmoothVolumeRegion(HashSet<int> region, int seedKey)
    {
        if (_volume is null || region.Count == 0)
        {
            return region;
        }

        HashSet<int> smoothed = [.. region];
        List<int> toRemove = [];
        foreach (int key in region)
        {
            DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
            int activeNeighbors = CountActiveVolumeNeighbors(region, x, y, z);
            if (activeNeighbors == 0)
            {
                toRemove.Add(key);
            }
        }

        foreach (int key in toRemove)
        {
            smoothed.Remove(key);
        }

        return RetainSeedConnectedVolumeRegion(smoothed, seedKey);
    }

    private int CountActiveVolumeNeighbors(HashSet<int> region, int x, int y, int z)
    {
        if (_volume is null)
        {
            return 0;
        }

        int active = 0;
        Span<(int X, int Y, int Z)> neighbors =
        [
            (x - 1, y, z), (x + 1, y, z),
            (x, y - 1, z), (x, y + 1, z),
            (x, y, z - 1), (x, y, z + 1),
        ];

        foreach ((int nextX, int nextY, int nextZ) in neighbors)
        {
            if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
            {
                continue;
            }

            if (region.Contains(GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY)))
            {
                active++;
            }
        }

        return active;
    }

    private double GetCachedHomogenizedVoxelValue(int x, int y, int z, Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int key = GetVoxelKey(x, y, z, _volume.SizeX, _volume.SizeY);
        if (homogenizedCache.TryGetValue(key, out double cachedValue))
        {
            return cachedValue;
        }

        double computedValue = GetHomogenizedVoxelValue(x, y, z);
        homogenizedCache[key] = computedValue;
        return computedValue;
    }

    private double EstimateVoxelGradientMagnitude(int x, int y, int z, Dictionary<int, double> gradientCache, Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int key = GetVoxelKey(x, y, z, _volume.SizeX, _volume.SizeY);
        if (gradientCache.TryGetValue(key, out double cachedGradient))
        {
            return cachedGradient;
        }

        double center = GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache);
        double dx = Math.Abs(GetCachedHomogenizedVoxelValue(Math.Clamp(x + 1, 0, _volume.SizeX - 1), y, z, homogenizedCache) - GetCachedHomogenizedVoxelValue(Math.Clamp(x - 1, 0, _volume.SizeX - 1), y, z, homogenizedCache));
        double dy = Math.Abs(GetCachedHomogenizedVoxelValue(x, Math.Clamp(y + 1, 0, _volume.SizeY - 1), z, homogenizedCache) - GetCachedHomogenizedVoxelValue(x, Math.Clamp(y - 1, 0, _volume.SizeY - 1), z, homogenizedCache));
        double dz = Math.Abs(GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z + 1, 0, _volume.SizeZ - 1), homogenizedCache) - GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z - 1, 0, _volume.SizeZ - 1), homogenizedCache));
        double centerPenalty =
            Math.Abs(center - GetCachedHomogenizedVoxelValue(Math.Clamp(x + 1, 0, _volume.SizeX - 1), y, z, homogenizedCache)) +
            Math.Abs(center - GetCachedHomogenizedVoxelValue(x, Math.Clamp(y + 1, 0, _volume.SizeY - 1), z, homogenizedCache)) +
            Math.Abs(center - GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z + 1, 0, _volume.SizeZ - 1), homogenizedCache));
        double gradient = ((dx + dy + dz) * 0.5) + (centerPenalty / 3.0);
        gradientCache[key] = gradient;
        return gradient;
    }

    private static int FindNearestRegionKey(HashSet<int> region, int seedKey, int sizeX, int sizeY)
    {
        DecodeVoxelKey(seedKey, sizeX, sizeY, out int seedX, out int seedY, out int seedZ);
        return region
            .OrderBy(key =>
            {
                DecodeVoxelKey(key, sizeX, sizeY, out int x, out int y, out int z);
                int dx = x - seedX;
                int dy = y - seedY;
                int dz = z - seedZ;
                return (dx * dx) + (dy * dy) + (dz * dz);
            })
            .FirstOrDefault(-1);
    }

    private static void DecodeVoxelKey(int key, int sizeX, int sizeY, out int x, out int y, out int z)
    {
        int sliceSize = sizeX * sizeY;
        z = key / sliceSize;
        int withinSlice = key % sliceSize;
        y = withinSlice / sizeX;
        x = withinSlice % sizeX;
    }

    private static int GetVoxelKey(int x, int y, int z, int sizeX, int sizeY) => (z * sizeY * sizeX) + (y * sizeX) + x;

    private readonly record struct AutoOutlineMask(int Left, int Top, bool[,] Pixels, int Count);
    private readonly record struct AutoOutlineSliceSource(int Width, int Height, ReadOnlyMemory<short> Pixels, string Modality);

    private readonly record struct VolumeSeedCandidate(int X, int Y, int Z, short RawValue, double HomogenizedValue);

    private readonly record struct AutoOutlineIntensitySignature(
        double Mean,
        double Median,
        double StandardDeviation,
        double Min,
        double Max,
        double Percentile10,
        double Percentile90,
        int PixelCount)
    {
        public bool IsValid => PixelCount > 0;
    }

    private readonly record struct SlicePropagationSliceData(int SliceIndex, DicomSpatialMetadata? Metadata, AutoOutlineSliceSource Source)
    {
        public bool IsValid => Metadata is not null && Source.Width > 0 && Source.Height > 0;
    }

    private readonly record struct SlicePropagationCandidateResult(Point[] Polygon, AutoOutlineIntensitySignature Signature, double Score)
    {
        public bool IsValid => Polygon is { Length: >= 3 } && Signature.IsValid;
    }

    private readonly record struct VolumeSliceContourCandidate(AutoOutlineMask Mask, Point[] ImagePoints, Point Centroid);

    private readonly record struct VolumeSliceComponentState(int ComponentId, AutoOutlineMask Mask, Point Centroid);

    private readonly record struct VolumeGrowNode(int X, int Y, int Z, short ParentRawValue, double ParentHomogenizedValue);

    private struct VolumeGrowthStatistics
    {
        private double _sum;
        private double _sumSquares;

        public VolumeGrowthStatistics(double initialHomogenizedValue, double localMean, double baseTolerance)
        {
            _sum = initialHomogenizedValue;
            _sumSquares = initialHomogenizedValue * initialHomogenizedValue;
            Count = 1;
            LocalMean = localMean;
            BaseTolerance = baseTolerance;
        }

        public int Count { get; private set; }

        public double LocalMean { get; }

        public double BaseTolerance { get; }

        public double Mean => Count <= 0 ? 0 : _sum / Count;

        public double StdDev
        {
            get
            {
                if (Count <= 1)
                {
                    return 0;
                }

                double mean = Mean;
                double variance = Math.Max(0, (_sumSquares / Count) - (mean * mean));
                return Math.Sqrt(variance);
            }
        }

        public void Include(double homogenizedValue)
        {
            Count++;
            _sum += homogenizedValue;
            _sumSquares += homogenizedValue * homogenizedValue;
        }
    }
}