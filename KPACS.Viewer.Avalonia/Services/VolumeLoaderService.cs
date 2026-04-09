// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Services/VolumeLoaderService.cs
// Builds a SeriesVolume from a DICOM series by loading all instances, sorting by
// ImagePositionPatient, extracting pixel data, applying rescale slope/intercept,
// and packing everything into a contiguous 16-bit signed voxel buffer.
//
// Uses fo-dicom for DICOM I/O — the volume geometry and voxel buffer are our own.
// ------------------------------------------------------------------------------------------------

using System.Globalization;
using System.Runtime.InteropServices;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

/// <summary>
/// Loads a DICOM series into a <see cref="SeriesVolume"/>.
/// Thread-safe: each call works on its own data.
/// </summary>
public sealed class VolumeLoaderService
{
    /// <summary>
    /// Minimum number of slices required to build a volume.
    /// Series with fewer slices keep the legacy per-file rendering path.
    /// </summary>
    public const int MinSlicesForVolume = 3;

    /// <summary>
    /// Minimum number of readable images required to build a renderable series volume.
    /// Remote render-server mode uses this lower threshold so single-image and two-image
    /// series can still be streamed through the common rendering path.
    /// </summary>
    public const int MinSlicesForRenderableSeries = 1;

    /// <summary>
    /// Tries to load a series into a 3D volume.
    /// Returns null if the series is not suitable (too few slices, RGB, inconsistent geometry, etc.).
    /// </summary>
    public async Task<SeriesVolume?> TryLoadVolumeAsync(
        SeriesRecord series,
        CancellationToken cancellationToken = default)
    {
        return await TryLoadVolumeAsync(series, MinSlicesForVolume, cancellationToken);
    }

    public async Task<SeriesVolume?> TryLoadVolumeAsync(
        SeriesRecord series,
        int minimumSliceCount,
        CancellationToken cancellationToken = default)
    {
        minimumSliceCount = Math.Max(MinSlicesForRenderableSeries, minimumSliceCount);

        if (series.Instances.Count < minimumSliceCount)
            return null;

        // Only consider instances that have readable files either in the imagebox
        // or at their original indexed source location.
        var readableInstances = series.Instances
            .Select(CreateReadableInstance)
            .Where(instance => instance is not null)
            .Select(instance => instance!)
            .ToList();

        if (readableInstances.Count < minimumSliceCount)
            return null;

        return await Task.Run(() => BuildVolume(readableInstances, minimumSliceCount, cancellationToken), cancellationToken);
    }

    public static string? ResolveReadableFilePath(InstanceRecord instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath))
            return instance.FilePath;

        if (!string.IsNullOrWhiteSpace(instance.SourceFilePath) && File.Exists(instance.SourceFilePath))
            return instance.SourceFilePath;

        return null;
    }

    private static InstanceRecord? CreateReadableInstance(InstanceRecord instance)
    {
        string? readablePath = ResolveReadableFilePath(instance);
        if (string.IsNullOrWhiteSpace(readablePath))
            return null;

        return new InstanceRecord
        {
            InstanceKey = instance.InstanceKey,
            SeriesKey = instance.SeriesKey,
            SopInstanceUid = instance.SopInstanceUid,
            SopClassUid = instance.SopClassUid,
            FilePath = readablePath,
            SourceFilePath = string.IsNullOrWhiteSpace(instance.SourceFilePath) ? readablePath : instance.SourceFilePath,
            InstanceNumber = instance.InstanceNumber,
            FrameCount = instance.FrameCount,
        };
    }

    private static SeriesVolume? BuildVolume(List<InstanceRecord> instances, int minimumSliceCount, CancellationToken ct)
    {
        // Phase 1: Load metadata + pixel data for each slice
        var slices = new List<SliceData>(instances.Count);

        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            var sliceData = LoadSlice(instance.FilePath);
            if (sliceData is null)
                return null; // Not volumetric (RGB, missing spatial data, etc.)

            slices.Add(sliceData);
        }

        if (slices.Count < minimumSliceCount)
            return null;

        // Verify all slices have the same dimensions and orientation
        var reference = slices[0];
        foreach (var slice in slices)
        {
            if (slice.Width != reference.Width || slice.Height != reference.Height)
                return null;

            if (!IsParallel(slice.Normal, reference.Normal))
                return null;

            if (!string.Equals(slice.FrameOfReferenceUid, reference.FrameOfReferenceUid, StringComparison.Ordinal))
                return null;
        }

        // Phase 2: Sort slices by position along the normal direction
        foreach (var slice in slices)
        {
            slice.SortPosition = slice.Origin.Dot(reference.Normal);
        }

        slices.Sort((a, b) => a.SortPosition.CompareTo(b.SortPosition));

        // Remove duplicate position slices (same position = same slice, keep first)
        for (int i = slices.Count - 1; i > 0; i--)
        {
            if (Math.Abs(slices[i].SortPosition - slices[i - 1].SortPosition) < 0.01)
                slices.RemoveAt(i);
        }

        if (slices.Count < minimumSliceCount)
            return null;

        double spacingZ;
        if (slices.Count <= 1)
        {
            spacingZ = DetermineFallbackSliceSpacing(reference);
        }
        else
        {
            double totalDistance = slices[^1].SortPosition - slices[0].SortPosition;
            spacingZ = totalDistance / (slices.Count - 1);

            if (spacingZ <= 0.001)
            {
                spacingZ = DetermineFallbackSliceSpacing(reference);
            }

            if (slices.Count > 2)
            {
                double tolerance = spacingZ * 0.2;
                for (int i = 1; i < slices.Count; i++)
                {
                    double gap = slices[i].SortPosition - slices[i - 1].SortPosition;
                    if (Math.Abs(gap - spacingZ) > tolerance)
                        return null; // Non-uniform spacing — not suitable for volume
                }
            }
        }

        // Phase 3: Build the voxel buffer
        int sizeX = reference.Width;
        int sizeY = reference.Height;
        int sizeZ = slices.Count;
        int slicePixels = sizeX * sizeY;
        long totalVoxels = (long)slicePixels * sizeZ;

        // Safety limit: don't allocate more than ~2 GB (1 billion voxels × 2 bytes)
        if (totalVoxels > 1_000_000_000L)
            return null;

        VolumeVoxelBuffer voxels = VolumeVoxelBuffer.CreatePreferred((int)totalVoxels);
        Span<short> voxelSpan = voxels.GetSpan();
        short globalMin = short.MaxValue;
        short globalMax = short.MinValue;

        for (int z = 0; z < sizeZ; z++)
        {
            ct.ThrowIfCancellationRequested();

            var slice = slices[z];
            int destOffset = z * slicePixels;

            if (slice.BitsAllocated >= 16)
            {
                var srcSpan = MemoryMarshal.Cast<byte, ushort>(slice.RawPixelData.AsSpan());
                int count = Math.Min(srcSpan.Length, slicePixels);

                for (int p = 0; p < count; p++)
                {
                    double raw = slice.IsSigned ? (double)(short)srcSpan[p] : srcSpan[p];
                    double rescaled = slice.RescaleSlope * raw + slice.RescaleIntercept;
                    short val = (short)Math.Clamp(rescaled, short.MinValue, short.MaxValue);
                    voxelSpan[destOffset + p] = val;
                    if (val < globalMin) globalMin = val;
                    if (val > globalMax) globalMax = val;
                }
            }
            else
            {
                int count = Math.Min(slice.RawPixelData.Length, slicePixels);

                for (int p = 0; p < count; p++)
                {
                    double rescaled = slice.RescaleSlope * slice.RawPixelData[p] + slice.RescaleIntercept;
                    short val = (short)Math.Clamp(rescaled, short.MinValue, short.MaxValue);
                    voxelSpan[destOffset + p] = val;
                    if (val < globalMin) globalMin = val;
                    if (val > globalMax) globalMax = val;
                }
            }
        }

        // Window preset from first slice
        double wc = reference.WindowCenter;
        double ww = reference.WindowWidth;
        if (ww <= 0)
        {
            // Auto window from volume range
            wc = (globalMin + globalMax) / 2.0;
            ww = Math.Max(1, globalMax - globalMin);
        }

        // Determine normal direction: if sort order is reversed relative to the
        // cross product of row × column, flip the normal
        Vector3D computedNormal = reference.RowDirection.Cross(reference.ColumnDirection).Normalize();
        Vector3D volumeNormal;
        if (slices.Count <= 1)
        {
            volumeNormal = computedNormal;
        }
        else
        {
            Vector3D actualDirection = (slices[^1].Origin - slices[0].Origin).Normalize();
            volumeNormal = actualDirection.Dot(computedNormal) >= 0 ? computedNormal : computedNormal * -1;
        }

        var sliceFilePaths = slices.Select(s => s.FilePath).ToList();
        var sliceSopInstanceUids = slices.Select(s => s.SopInstanceUid).ToList();

        return new SeriesVolume(
            voxels,
            sizeX, sizeY, sizeZ,
            reference.ColumnSpacing, reference.RowSpacing, spacingZ,
            slices[0].Origin,
            reference.RowDirection, reference.ColumnDirection, volumeNormal,
            wc, ww,
            globalMin, globalMax,
            reference.IsMonochrome1,
            reference.SeriesInstanceUid,
            reference.FrameOfReferenceUid,
            reference.AcquisitionNumber,
            sliceFilePaths,
            sliceSopInstanceUids);
    }

    private static double DetermineFallbackSliceSpacing(SliceData slice)
    {
        double fallback = Math.Min(
            slice.ColumnSpacing > 0 ? slice.ColumnSpacing : double.MaxValue,
            slice.RowSpacing > 0 ? slice.RowSpacing : double.MaxValue);

        return double.IsFinite(fallback) && fallback > 0.001 ? fallback : 1.0;
    }

    private static SliceData? LoadSlice(string filePath)
    {
        try
        {
            var file = DicomFile.Open(filePath, FileReadOption.ReadAll);
            var dataset = file.Dataset;

            int samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, 1);
            if (samplesPerPixel >= 3)
                return null; // RGB — not suitable for volume rendering

            int width = dataset.GetSingleValueOrDefault(DicomTag.Columns, 0);
            int height = dataset.GetSingleValueOrDefault(DicomTag.Rows, 0);
            if (width <= 0 || height <= 0)
                return null;

            int bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 8);
            int bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, bitsAllocated);
            bool isSigned = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0) == 1;
            double slope = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleSlope, 1.0);
            double intercept = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleIntercept, 0.0);
            if (slope == 0) slope = 1.0;

            string photometric = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2");
            bool isMonochrome1 = photometric.Contains("MONOCHROME1");

            // Spatial metadata
            var spatial = DicomSpatialMetadata.FromDataset(dataset, filePath);
            if (spatial is null)
                return null;

            // Pixel data
            if (!dataset.Contains(DicomTag.PixelData))
                return null;

            var syntax = file.Dataset.InternalTransferSyntax;
            if (syntax.IsEncapsulated)
            {
                file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
                dataset = file.Dataset;
            }

            var pixelData = DicomPixelData.Create(dataset);
            var frame = pixelData.GetFrame(0);

            // Window preset
            double wc = 0, ww = 0;
            if (dataset.Contains(DicomTag.WindowCenter) && dataset.Contains(DicomTag.WindowWidth))
            {
                wc = dataset.GetSingleValueOrDefault<double>(DicomTag.WindowCenter, 0);
                ww = dataset.GetSingleValueOrDefault<double>(DicomTag.WindowWidth, 0);
            }

            return new SliceData
            {
                FilePath = filePath,
                Width = width,
                Height = height,
                BitsAllocated = bitsAllocated,
                BitsStored = bitsStored,
                IsSigned = isSigned,
                RescaleSlope = slope,
                RescaleIntercept = intercept,
                IsMonochrome1 = isMonochrome1,
                Origin = spatial.Origin,
                RowDirection = spatial.RowDirection,
                ColumnDirection = spatial.ColumnDirection,
                Normal = spatial.Normal,
                RowSpacing = spatial.RowSpacing,
                ColumnSpacing = spatial.ColumnSpacing,
                RawPixelData = frame.Data,
                WindowCenter = wc,
                WindowWidth = ww,
                SeriesInstanceUid = spatial.SeriesInstanceUid,
                SopInstanceUid = spatial.SopInstanceUid,
                FrameOfReferenceUid = spatial.FrameOfReferenceUid,
                AcquisitionNumber = spatial.AcquisitionNumber,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsParallel(Vector3D a, Vector3D b)
    {
        double dot = Math.Abs(a.Dot(b));
        return dot > 0.99; // ~8° tolerance
    }

    private sealed class SliceData
    {
        public string FilePath { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public int BitsAllocated { get; init; }
        public int BitsStored { get; init; }
        public bool IsSigned { get; init; }
        public double RescaleSlope { get; init; }
        public double RescaleIntercept { get; init; }
        public bool IsMonochrome1 { get; init; }
        public Vector3D Origin { get; init; }
        public Vector3D RowDirection { get; init; }
        public Vector3D ColumnDirection { get; init; }
        public Vector3D Normal { get; init; }
        public double RowSpacing { get; init; }
        public double ColumnSpacing { get; init; }
        public byte[] RawPixelData { get; init; } = [];
        public double WindowCenter { get; init; }
        public double WindowWidth { get; init; }
        public string SeriesInstanceUid { get; init; } = "";
        public string SopInstanceUid { get; init; } = "";
        public string FrameOfReferenceUid { get; init; } = "";
        public string AcquisitionNumber { get; init; } = "";
        public double SortPosition { get; set; }
    }
}
