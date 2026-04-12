// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/VolumeReslicer.cs
// Extracts 2D slices from a SeriesVolume along any of the three orthogonal planes
// (axial, coronal, sagittal) or at an arbitrary oblique orientation.
//
// Output is a 16-bit signed buffer ready for windowing by DicomPixelRenderer.
// Uses trilinear interpolation for sub-voxel accuracy in coronal/sagittal/oblique views.
// ------------------------------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Standard orthogonal slice orientations relative to the volume.
/// </summary>
public enum SliceOrientation
{
    /// <summary>XY plane — the native acquisition plane (slice index = Z).</summary>
    Axial,

    /// <summary>XZ plane — front-to-back (slice index = Y).</summary>
    Coronal,

    /// <summary>YZ plane — left-to-right (slice index = X).</summary>
    Sagittal,
}

public enum VolumeProjectionMode
{
    Mpr,
    MipPr,
    MinPr,
    MpVrt,
    Dvr,
}

/// <summary>
/// Describes the geometry and pixel data of a resliced 2D image extracted from a volume.
/// </summary>
public sealed class ReslicedImage
{
    /// <summary>16-bit signed pixel data, row-major, ready for windowing.</summary>
    public short[] Pixels { get; init; } = [];

    /// <summary>Optional BGRA pixel data, row-major, used when rendering already-produced color frames.</summary>
    public byte[]? BgraPixels { get; init; }

    /// <summary>Width of the resliced image in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the resliced image in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Pixel spacing in mm along the horizontal axis of the output image.</summary>
    public double PixelSpacingX { get; init; }

    /// <summary>Pixel spacing in mm along the vertical axis of the output image.</summary>
    public double PixelSpacingY { get; init; }

    /// <summary>Spatial metadata for this slice (for linked-view synchronization).</summary>
    public DicomSpatialMetadata? SpatialMetadata { get; init; }

    /// <summary>Rendering backend that produced this image.</summary>
    public string RenderBackendLabel { get; init; } = "CPU";
}

/// <summary>
/// Extracts 2D slices from a <see cref="SeriesVolume"/>.
/// </summary>
public static class VolumeReslicer
{
    private static readonly ConditionalWeakTable<SeriesVolume, VolumeGradientVolume> GradientCache = new();

    public static VolumeSlicePlane CreateSlicePlane(
        SeriesVolume volume,
        SliceOrientation orientation,
        double tiltAroundColumnRadians,
        double tiltAroundRowRadians,
        double offsetMm)
    {
        (Vector3D baseRow, Vector3D baseColumn, Vector3D baseNormal, double pixelSpacingX, double pixelSpacingY) =
            GetPlaneBasis(volume, orientation);

        Vector3D row = RotateAroundAxis(baseRow, baseColumn, tiltAroundColumnRadians).Normalize();
        Vector3D normal = RotateAroundAxis(baseNormal, baseColumn, tiltAroundColumnRadians).Normalize();
        Vector3D column = RotateAroundAxis(baseColumn, row, tiltAroundRowRadians).Normalize();
        normal = RotateAroundAxis(normal, row, tiltAroundRowRadians).Normalize();

        Vector3D volumeCenter = volume.VoxelToPatient((volume.SizeX - 1) * 0.5, (volume.SizeY - 1) * 0.5, (volume.SizeZ - 1) * 0.5);
        (_, _, double minDepthMm, double maxDepthMm) =
            ComputeProjectedBounds(volume, volumeCenter, row, column, normal);

        double sliceSpacing = ComputeObliqueSliceSpacing(volume, normal);
        double scrollStep = Math.Max(0.1, Math.Min(sliceSpacing, GetMinimumVolumeSpacing(volume)));
        double depthRange = Math.Max(0, maxDepthMm - minDepthMm);
        int sliceCount = Math.Max(1, (int)Math.Floor(depthRange / scrollStep) + 1);
        scrollStep = sliceCount > 1 ? depthRange / (sliceCount - 1) : scrollStep;
        double halfExtentMm = GetVolumeHalfDiagonalMm(volume);
        int width = Math.Max(1, (int)Math.Ceiling((halfExtentMm * 2.0) / Math.Max(0.1, pixelSpacingX)) + 1);
        int height = Math.Max(1, (int)Math.Ceiling((halfExtentMm * 2.0) / Math.Max(0.1, pixelSpacingY)) + 1);

        return new VolumeSlicePlane
        {
            VolumeCenter = volumeCenter,
            RowDirection = row,
            ColumnDirection = column,
            Normal = normal,
            PixelSpacingX = Math.Max(0.1, pixelSpacingX),
            PixelSpacingY = Math.Max(0.1, pixelSpacingY),
            SliceSpacingMm = Math.Max(0.1, sliceSpacing),
            ScrollStepMm = Math.Max(0.1, scrollStep),
            MinOffsetMm = minDepthMm,
            MaxOffsetMm = maxDepthMm,
            CurrentOffsetMm = Math.Clamp(offsetMm, minDepthMm, maxDepthMm),
            SliceCount = sliceCount,
            Width = width,
            Height = height,
        };
    }

    public static ReslicedImage RenderSlab(
        SeriesVolume volume,
        SliceOrientation orientation,
        int centerSliceIndex,
        double thicknessMm,
        VolumeProjectionMode mode)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        if (sliceCount <= 0)
        {
            return new ReslicedImage();
        }

        centerSliceIndex = Math.Clamp(centerSliceIndex, 0, sliceCount - 1);
        double sliceSpacing = GetSliceSpacing(volume, orientation);
        int slabSliceCount = Math.Max(1, (int)Math.Round(Math.Max(thicknessMm, sliceSpacing) / sliceSpacing));
        if (slabSliceCount % 2 == 0)
        {
            slabSliceCount++;
        }

        int halfSpan = slabSliceCount / 2;
        int startSlice = Math.Max(0, centerSliceIndex - halfSpan);
        int endSlice = Math.Min(sliceCount - 1, centerSliceIndex + halfSpan);

        if (startSlice == endSlice && mode != VolumeProjectionMode.Dvr)
        {
            return ExtractSlice(volume, orientation, centerSliceIndex);
        }

        return mode switch
        {
            VolumeProjectionMode.Mpr => ComputeAverageProjection(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MipPr => ComputeMip(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MinPr => ComputeMinIp(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MpVrt => ComputeVolumeProjection(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.Dvr => ComputeDirectVolumeRendering(volume, orientation, startSlice, endSlice),
            _ => ExtractSlice(volume, orientation, centerSliceIndex),
        };
    }

    // LRU cache for orthogonal slice extraction, per-thread.
    // Multi-panel views (e.g. axial + coronal + sagittal + cross-section) request
    // different volume/orientation/slice combinations in rapid succession.
    // A single-entry cache thrashes constantly in this scenario, while an LRU
    // with a small capacity (8 entries) covers the typical panel count and avoids
    // redundant reslice + GPU round-trips. The pixel arrays in ReslicedImage are
    // treated as read-only by the rendering pipeline, so sharing instances is safe.
    private const int SliceCacheCapacity = 8;

    [ThreadStatic]
    private static SliceLruCache? t_sliceCache;

    /// <summary>
    /// Compact LRU cache keyed by (volume-identity, orientation, sliceIndex).
    /// Entries are evicted oldest-first when capacity is exceeded.
    /// WeakReference keeps the volume from being pinned in memory.
    /// </summary>
    private sealed class SliceLruCache
    {
        private readonly int _capacity;
        private readonly LinkedList<SliceCacheEntry> _entries = new();

        public SliceLruCache(int capacity) => _capacity = capacity;

        public ReslicedImage? TryGet(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                SliceCacheEntry e = node.Value;
                if (e.Orientation == orientation
                    && e.SliceIndex == sliceIndex
                    && e.VolumeRef.TryGetTarget(out SeriesVolume? cached)
                    && ReferenceEquals(cached, volume))
                {
                    // Move to front (most-recently-used).
                    _entries.Remove(node);
                    _entries.AddFirst(node);
                    return e.Result;
                }
            }

            return null;
        }

        public void Add(SeriesVolume volume, SliceOrientation orientation, int sliceIndex, ReslicedImage result)
        {
            // Evict oldest if at capacity.
            while (_entries.Count >= _capacity)
            {
                _entries.RemoveLast();
            }

            _entries.AddFirst(new SliceCacheEntry(
                new WeakReference<SeriesVolume>(volume),
                orientation,
                sliceIndex,
                result));
        }
    }

    private sealed record SliceCacheEntry(
        WeakReference<SeriesVolume> VolumeRef,
        SliceOrientation Orientation,
        int SliceIndex,
        ReslicedImage Result);

    /// <summary>
    /// Extracts an orthogonal slice from the volume.
    /// </summary>
    /// <param name="volume">Source volume.</param>
    /// <param name="orientation">Slice plane orientation.</param>
    /// <param name="sliceIndex">Index along the perpendicular axis (0-based).</param>
    /// <returns>Resliced 2D image.</returns>
    public static ReslicedImage ExtractSlice(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        t_sliceCache ??= new SliceLruCache(SliceCacheCapacity);

        ReslicedImage? cached = t_sliceCache.TryGet(volume, orientation, sliceIndex);
        if (cached is not null)
        {
            return cached;
        }

        ReslicedImage result = orientation switch
        {
            SliceOrientation.Axial => ExtractAxial(volume, sliceIndex),
            SliceOrientation.Coronal => ExtractCoronal(volume, sliceIndex),
            SliceOrientation.Sagittal => ExtractSagittal(volume, sliceIndex),
            _ => ExtractAxial(volume, sliceIndex),
        };

        t_sliceCache.Add(volume, orientation, sliceIndex, result);
        return result;
    }

    public static ReslicedImage ExtractSlice(SeriesVolume volume, VolumeSlicePlane plane) =>
        RenderObliqueSlab(volume, plane, 0, VolumeProjectionMode.Mpr);

    /// <summary>
    /// Returns the number of slices available along the given orientation.
    /// </summary>
    public static int GetSliceCount(SeriesVolume volume, SliceOrientation orientation)
    {
        return orientation switch
        {
            SliceOrientation.Axial => volume.SizeZ,
            SliceOrientation.Coronal => volume.SizeY,
            SliceOrientation.Sagittal => volume.SizeX,
            _ => volume.SizeZ,
        };
    }

    public static DicomSpatialMetadata GetSliceSpatialMetadata(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        return orientation switch
        {
            SliceOrientation.Axial => GetAxialSpatialMetadata(volume, sliceIndex),
            SliceOrientation.Coronal => GetCoronalSpatialMetadata(volume, sliceIndex),
            SliceOrientation.Sagittal => GetSagittalSpatialMetadata(volume, sliceIndex),
            _ => GetAxialSpatialMetadata(volume, sliceIndex),
        };
    }

    public static DicomSpatialMetadata GetSliceSpatialMetadata(SeriesVolume volume, VolumeSlicePlane plane) =>
        plane.CreateSpatialMetadata(volume);

    private static ReslicedImage ExtractAxial(SeriesVolume volume, int z)
    {
        z = Math.Clamp(z, 0, volume.SizeZ - 1);
        int width = volume.SizeX;
        int height = volume.SizeY;
        short[] pixels = new short[width * height];

        int srcOffset = z * volume.SizeY * volume.SizeX;
        volume.CopyVoxelsTo(srcOffset, pixels, 0, pixels.Length);
        DicomSpatialMetadata spatial = GetAxialSpatialMetadata(volume, z);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingX,
            PixelSpacingY = volume.SpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static ReslicedImage ExtractCoronal(SeriesVolume volume, int y)
    {
        y = Math.Clamp(y, 0, volume.SizeY - 1);
        if (TryGpuExtractOrthogonalSlice(volume, SliceOrientation.Coronal, y, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        int width = volume.SizeX;
        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        short[] pixels = new short[width * height];

        for (int row = 0; row < height; row++)
        {
            double sourceZ = MapOutputRowToSourceZ(row, height, volume.SizeZ);
            int dstRow = row * width;

            for (int x = 0; x < width; x++)
            {
                pixels[dstRow + x] = (short)Math.Round(volume.GetVoxelInterpolated(x, y, sourceZ));
            }
        }

        DicomSpatialMetadata spatial = GetCoronalSpatialMetadata(volume, y);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingX,
            PixelSpacingY = targetSpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static ReslicedImage ExtractSagittal(SeriesVolume volume, int x)
    {
        x = Math.Clamp(x, 0, volume.SizeX - 1);
        if (TryGpuExtractOrthogonalSlice(volume, SliceOrientation.Sagittal, x, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        int width = volume.SizeY;
        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        short[] pixels = new short[width * height];

        for (int row = 0; row < height; row++)
        {
            double sourceZ = MapOutputRowToSourceZ(row, height, volume.SizeZ);
            int dstRow = row * width;

            for (int y = 0; y < width; y++)
            {
                pixels[dstRow + y] = (short)Math.Round(volume.GetVoxelInterpolated(x, y, sourceZ));
            }
        }

        DicomSpatialMetadata spatial = GetSagittalSpatialMetadata(volume, x);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingY,
            PixelSpacingY = targetSpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static DicomSpatialMetadata GetAxialSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeZ - 1);
        Vector3D sliceOrigin = volume.Origin + volume.Normal * (sliceIndex * volume.SpacingZ);
        return new DicomSpatialMetadata(
            FilePath: sliceIndex < volume.SliceFilePaths.Count ? volume.SliceFilePaths[sliceIndex] : "",
            SopInstanceUid: sliceIndex < volume.SliceSopInstanceUids.Count ? volume.SliceSopInstanceUids[sliceIndex] : "",
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeX,
            volume.SizeY,
            RowSpacing: volume.SpacingY,
            ColumnSpacing: volume.SpacingX,
            sliceOrigin,
            volume.RowDirection,
            volume.ColumnDirection,
            volume.Normal);
    }

    private static DicomSpatialMetadata GetCoronalSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeY - 1);
        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        Vector3D sliceOrigin = volume.Origin
            + volume.ColumnDirection * (sliceIndex * volume.SpacingY)
            + volume.Normal * ((volume.SizeZ - 1) * volume.SpacingZ);

        return new DicomSpatialMetadata(
            FilePath: string.Empty,
            SopInstanceUid: string.Empty,
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeX,
            height,
            RowSpacing: targetSpacingY,
            ColumnSpacing: volume.SpacingX,
            sliceOrigin,
            volume.RowDirection,
            volume.Normal * -1,
            volume.ColumnDirection);
    }

    private static DicomSpatialMetadata GetSagittalSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeX - 1);
        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        Vector3D sliceOrigin = volume.Origin
            + volume.RowDirection * (sliceIndex * volume.SpacingX)
            + volume.Normal * ((volume.SizeZ - 1) * volume.SpacingZ);

        return new DicomSpatialMetadata(
            FilePath: string.Empty,
            SopInstanceUid: string.Empty,
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeY,
            height,
            RowSpacing: targetSpacingY,
            ColumnSpacing: volume.SpacingY,
            sliceOrigin,
            volume.ColumnDirection,
            volume.Normal * -1,
            volume.RowDirection);
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

    /// <summary>
    /// Computes a Maximum Intensity Projection (MIP) slab along the given orientation.
    /// </summary>
    /// <param name="volume">Source volume.</param>
    /// <param name="orientation">Projection direction.</param>
    /// <param name="startSlice">First slice index (inclusive).</param>
    /// <param name="endSlice">Last slice index (inclusive).</param>
    /// <returns>MIP image.</returns>
    public static ReslicedImage ComputeMip(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        if (VolumeComputeBackend.TryRenderProjection(volume, orientation, startSlice, endSlice, reference, VolumeProjectionMode.MipPr, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] MIP projection fallback suppressed — returning blank image");
            return reference;
        }

        short[] mipPixels = new short[reference.Pixels.Length];

        Parallel.For(0, reference.Height, row =>
        {
            for (int column = 0; column < reference.Width; column++)
            {
                GetOrthographicFixedCoordinates(volume, orientation, column, row, reference.Width, reference.Height,
                    out double fixedA, out double fixedB);

                double maxValue = double.NegativeInfinity;
                for (int s = startSlice; s <= endSlice; s++)
                {
                    (double x, double y, double z) = GetOrthographicSamplePosition(orientation, fixedA, fixedB, s);
                    maxValue = Math.Max(maxValue, volume.GetVoxelInterpolated(x, y, z));
                }

                mipPixels[row * reference.Width + column] = (short)Math.Clamp(maxValue, short.MinValue, short.MaxValue);
            }
        });

        return new ReslicedImage
        {
            Pixels = mipPixels,
            Width = reference.Width,
            Height = reference.Height,
            PixelSpacingX = reference.PixelSpacingX,
            PixelSpacingY = reference.PixelSpacingY,
            SpatialMetadata = reference.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeMinIp(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        if (VolumeComputeBackend.TryRenderProjection(volume, orientation, startSlice, endSlice, reference, VolumeProjectionMode.MinPr, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] MinIP projection fallback suppressed — returning blank image");
            return reference;
        }

        short[] minPixels = new short[reference.Pixels.Length];

        Parallel.For(0, reference.Height, row =>
        {
            for (int column = 0; column < reference.Width; column++)
            {
                GetOrthographicFixedCoordinates(volume, orientation, column, row, reference.Width, reference.Height,
                    out double fixedA, out double fixedB);

                double minValue = double.PositiveInfinity;
                for (int s = startSlice; s <= endSlice; s++)
                {
                    (double x, double y, double z) = GetOrthographicSamplePosition(orientation, fixedA, fixedB, s);
                    minValue = Math.Min(minValue, volume.GetVoxelInterpolated(x, y, z));
                }

                minPixels[row * reference.Width + column] = (short)Math.Clamp(minValue, short.MinValue, short.MaxValue);
            }
        });

        return new ReslicedImage
        {
            Pixels = minPixels,
            Width = reference.Width,
            Height = reference.Height,
            PixelSpacingX = reference.PixelSpacingX,
            PixelSpacingY = reference.PixelSpacingY,
            SpatialMetadata = reference.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeAverageProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        if (VolumeComputeBackend.TryRenderProjection(volume, orientation, startSlice, endSlice, reference, VolumeProjectionMode.Mpr, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] Average projection fallback suppressed — returning blank image");
            return reference;
        }

        short[] averaged = new short[reference.Pixels.Length];
        int projectionCount = Math.Max(1, endSlice - startSlice + 1);

        Parallel.For(0, reference.Height, row =>
        {
            for (int column = 0; column < reference.Width; column++)
            {
                GetOrthographicFixedCoordinates(volume, orientation, column, row, reference.Width, reference.Height,
                    out double fixedA, out double fixedB);

                double sum = 0;
                for (int s = startSlice; s <= endSlice; s++)
                {
                    (double x, double y, double z) = GetOrthographicSamplePosition(orientation, fixedA, fixedB, s);
                    sum += volume.GetVoxelInterpolated(x, y, z);
                }

                averaged[row * reference.Width + column] = (short)Math.Clamp(sum / projectionCount, short.MinValue, short.MaxValue);
            }
        });

        return new ReslicedImage
        {
            Pixels = averaged,
            Width = reference.Width,
            Height = reference.Height,
            PixelSpacingX = reference.PixelSpacingX,
            PixelSpacingY = reference.PixelSpacingY,
            SpatialMetadata = reference.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeVolumeProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        if (VolumeComputeBackend.TryRenderProjection(volume, orientation, startSlice, endSlice, reference, VolumeProjectionMode.MpVrt, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] MaxVariance projection fallback suppressed — returning blank image");
            return reference;
        }

        short[] projected = new short[reference.Pixels.Length];
        double range = Math.Max(1, volume.MaxValue - volume.MinValue);

        Parallel.For(0, reference.Height, row =>
        {
            for (int column = 0; column < reference.Width; column++)
            {
                GetOrthographicFixedCoordinates(volume, orientation, column, row, reference.Width, reference.Height,
                    out double fixedA, out double fixedB);

                double accumulatedValue = 0.0;
                double accumulatedAlpha = 0.0;
                for (int s = startSlice; s <= endSlice; s++)
                {
                    (double x, double y, double z) = GetOrthographicSamplePosition(orientation, fixedA, fixedB, s);
                    double sample = volume.GetVoxelInterpolated(x, y, z);
                    double normalized = Math.Clamp((sample - volume.MinValue) / range, 0.0, 1.0);
                    double opacity = normalized <= 0.05 ? 0.0 : Math.Min(0.85, Math.Pow(normalized, 1.6) * 0.35);
                    double contribution = opacity * (1.0 - accumulatedAlpha);
                    accumulatedValue += sample * contribution;
                    accumulatedAlpha += contribution;
                    if (accumulatedAlpha >= 0.995)
                    {
                        break;
                    }
                }

                double value = accumulatedAlpha > 0.0001
                    ? accumulatedValue / accumulatedAlpha
                    : 0;
                projected[row * reference.Width + column] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        });

        return new ReslicedImage
        {
            Pixels = projected,
            Width = reference.Width,
            Height = reference.Height,
            PixelSpacingX = reference.PixelSpacingX,
            PixelSpacingY = reference.PixelSpacingY,
            SpatialMetadata = reference.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeDirectVolumeRendering(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        VolumeRenderState state = VolumeRenderState.CreateOrthographicDefaults(
            orientation,
            reference.Width,
            reference.Height);
        if (VolumeComputeBackend.TryRenderDvrView(volume, state, VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue), out ReslicedImage gpuImage))
        {
            return new ReslicedImage
            {
                Pixels = gpuImage.Pixels,
                Width = gpuImage.Width,
                Height = gpuImage.Height,
                PixelSpacingX = reference.PixelSpacingX,
                PixelSpacingY = reference.PixelSpacingY,
                SpatialMetadata = reference.SpatialMetadata,
                RenderBackendLabel = gpuImage.RenderBackendLabel,
            };
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] DVR fallback suppressed — returning blank image");
            return reference;
        }

        VolumeGradientVolume gradients = GradientCache.GetValue(volume, static source => VolumeGradientVolume.Create(source));
        VolumeTransferFunction tf = VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue);

        return VolumeRayCaster.RenderOrthographicSlab(
            volume,
            gradients,
            orientation,
            startSlice,
            endSlice,
            state,
            tf);
    }

    /// <summary>
    /// Renders the full volume from an arbitrary camera using the given render state.
    /// Called directly by DicomViewPanel when 3D camera orbit is active.
    /// </summary>
    public static ReslicedImage ComputeDirectVolumeRenderingView(
        SeriesVolume volume,
        VolumeRenderState state,
        VolumeTransferFunction? transferFunction = null)
    {
        transferFunction ??= VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue);
        if (VolumeComputeBackend.TryRenderDvrView(volume, state, transferFunction, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] DVR view fallback suppressed — returning blank image");
            return new ReslicedImage();
        }

        VolumeGradientVolume gradients = GradientCache.GetValue(volume, static source => VolumeGradientVolume.Create(source));
        return VolumeRayCaster.RenderView(volume, gradients, transferFunction, state);
    }

    public static double GetSliceSpacing(SeriesVolume volume, SliceOrientation orientation)
    {
        return orientation switch
        {
            SliceOrientation.Axial => volume.SpacingZ,
            SliceOrientation.Coronal => volume.SpacingY,
            SliceOrientation.Sagittal => volume.SpacingX,
            _ => volume.SpacingZ,
        };
    }

    public static ReslicedImage RenderSlab(
        SeriesVolume volume,
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode)
    {
        return mode == VolumeProjectionMode.Dvr
            ? RenderObliqueSlab(volume, plane, Math.Max(plane.SliceSpacingMm, thicknessMm), VolumeProjectionMode.MpVrt)
            : RenderObliqueSlab(volume, plane, thicknessMm, mode);
    }

    private static ReslicedImage RenderObliqueSlab(
        SeriesVolume volume,
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode)
    {
        // Try GPU-accelerated oblique rendering first.
        if (VolumeComputeBackend.TryRenderObliqueProjection(volume, plane, thicknessMm, mode, out ReslicedImage gpuImage))
        {
            return gpuImage;
        }

        if (VolumeComputeBackend.CpuFallbackDisabled)
        {
            Console.Error.WriteLine($"[CPU·BLOCKED] Oblique slab fallback suppressed — returning blank image");
            return new ReslicedImage
            {
                Pixels = new short[Math.Max(1, plane.Width) * Math.Max(1, plane.Height)],
                Width = Math.Max(1, plane.Width),
                Height = Math.Max(1, plane.Height),
                PixelSpacingX = plane.PixelSpacingX,
                PixelSpacingY = plane.PixelSpacingY,
                SpatialMetadata = plane.CreateSpatialMetadata(volume),
            };
        }

        int width = Math.Max(1, plane.Width);
        int height = Math.Max(1, plane.Height);
        short[] pixels = new short[width * height];
        double[] accumulators = mode is VolumeProjectionMode.Mpr or VolumeProjectionMode.MpVrt ? new double[pixels.Length] : [];
        double[] alphas = mode == VolumeProjectionMode.MpVrt ? new double[pixels.Length] : [];
        double stepMm = Math.Max(0.1, plane.SliceSpacingMm);
        double safeThickness = Math.Max(0, thicknessMm);
        int sampleCount = safeThickness <= stepMm * 0.5
            ? 1
            : Math.Max(1, (int)Math.Round(safeThickness / stepMm) + 1);

        if (sampleCount % 2 == 0)
        {
            sampleCount++;
        }

        double range = Math.Max(1, volume.MaxValue - volume.MinValue);
        double halfWidthMm = (width - 1) * plane.PixelSpacingX * 0.5;
        double halfHeightMm = (height - 1) * plane.PixelSpacingY * 0.5;
        double halfThicknessMm = stepMm * (sampleCount - 1) * 0.5;

        Parallel.For(0, height, y =>
        {
            double vOffset = y * plane.PixelSpacingY - halfHeightMm;
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                double uOffset = x * plane.PixelSpacingX - halfWidthMm;
                Vector3D basePoint = plane.Center + plane.RowDirection * uOffset + plane.ColumnDirection * vOffset;
                int pixelIndex = rowOffset + x;
                double resultValue = mode == VolumeProjectionMode.MinPr ? double.PositiveInfinity : double.NegativeInfinity;
                int validSampleCount = 0;

                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    double depthOffset = sampleCount == 1
                        ? 0
                        : -halfThicknessMm + sampleIndex * stepMm;
                    Vector3D patientPoint = basePoint + plane.Normal * depthOffset;
                    (double vx, double vy, double vz) = volume.PatientToVoxel(patientPoint);
                    if (!volume.TryGetVoxelInterpolated(vx, vy, vz, out double value))
                    {
                        continue;
                    }

                    validSampleCount++;

                    switch (mode)
                    {
                        case VolumeProjectionMode.Mpr:
                            accumulators[pixelIndex] += value;
                            break;
                        case VolumeProjectionMode.MipPr:
                            resultValue = Math.Max(resultValue, value);
                            break;
                        case VolumeProjectionMode.MinPr:
                            resultValue = Math.Min(resultValue, value);
                            break;
                        case VolumeProjectionMode.MpVrt:
                            double normalized = Math.Clamp((value - volume.MinValue) / range, 0.0, 1.0);
                            double opacity = normalized <= 0.05 ? 0.0 : Math.Min(0.85, Math.Pow(normalized, 1.6) * 0.35);
                            double contribution = opacity * (1.0 - alphas[pixelIndex]);
                            accumulators[pixelIndex] += value * contribution;
                            alphas[pixelIndex] += contribution;
                            break;
                    }
                }

                if (validSampleCount == 0)
                {
                    pixels[pixelIndex] = volume.MinValue;
                    continue;
                }

                pixels[pixelIndex] = mode switch
                {
                    VolumeProjectionMode.Mpr => (short)Math.Clamp(accumulators[pixelIndex] / validSampleCount, short.MinValue, short.MaxValue),
                    VolumeProjectionMode.MipPr => (short)Math.Clamp(resultValue, short.MinValue, short.MaxValue),
                    VolumeProjectionMode.MinPr => (short)Math.Clamp(resultValue, short.MinValue, short.MaxValue),
                    VolumeProjectionMode.MpVrt => (short)Math.Clamp(
                        alphas[pixelIndex] > 0.0001 ? accumulators[pixelIndex] / alphas[pixelIndex] : 0,
                        short.MinValue,
                        short.MaxValue),
                    _ => (short)Math.Clamp(resultValue, short.MinValue, short.MaxValue),
                };
            }
        });

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = plane.PixelSpacingX,
            PixelSpacingY = plane.PixelSpacingY,
            SpatialMetadata = plane.CreateSpatialMetadata(volume),
        };
    }

    /// <summary>
    /// Attempts to extract a coronal or sagittal slice via the GPU oblique-slab kernel.
    /// Constructs the equivalent <see cref="VolumeSlicePlane"/> geometry and delegates to
    /// <see cref="VolumeComputeBackend.TryRenderObliqueProjection"/>. Returns <c>false</c>
    /// when OpenCL is unavailable or for axial slices (which are already O(1) via Array.Copy).
    /// </summary>
    private static bool TryGpuExtractOrthogonalSlice(
        SeriesVolume volume,
        SliceOrientation orientation,
        int sliceIndex,
        out ReslicedImage image)
    {
        image = new ReslicedImage();
        if (orientation == SliceOrientation.Axial)
        {
            return false;
        }

        (Vector3D row, Vector3D col, Vector3D normal, double spacingX, double spacingY) = GetPlaneBasis(volume, orientation);

        int width, height;
        double sliceSpacingMm, currentOffsetMm;
        switch (orientation)
        {
            case SliceOrientation.Coronal:
                sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeY - 1);
                width = volume.SizeX;
                height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, spacingY);
                sliceSpacingMm = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
                currentOffsetMm = (sliceIndex - (volume.SizeY - 1) * 0.5) * sliceSpacingMm;
                break;
            case SliceOrientation.Sagittal:
                sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeX - 1);
                width = volume.SizeY;
                height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, spacingY);
                sliceSpacingMm = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
                currentOffsetMm = (sliceIndex - (volume.SizeX - 1) * 0.5) * sliceSpacingMm;
                break;
            default:
                return false;
        }

        Vector3D volumeCenter = volume.VoxelToPatient(
            (volume.SizeX - 1) * 0.5,
            (volume.SizeY - 1) * 0.5,
            (volume.SizeZ - 1) * 0.5);

        var plane = new VolumeSlicePlane
        {
            VolumeCenter = volumeCenter,
            RowDirection = row,
            ColumnDirection = col,
            Normal = normal,
            PixelSpacingX = spacingX,
            PixelSpacingY = spacingY,
            SliceSpacingMm = sliceSpacingMm,
            ScrollStepMm = sliceSpacingMm,
            MinOffsetMm = currentOffsetMm,
            MaxOffsetMm = currentOffsetMm,
            CurrentOffsetMm = currentOffsetMm,
            SliceCount = 1,
            Width = width,
            Height = height,
        };

        return VolumeComputeBackend.TryRenderObliqueProjection(volume, plane, 0, VolumeProjectionMode.Mpr, out image);
    }

    private static (Vector3D Row, Vector3D Column, Vector3D Normal, double PixelSpacingX, double PixelSpacingY) GetPlaneBasis(
        SeriesVolume volume,
        SliceOrientation orientation)
    {
        double targetSpacingY = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        return orientation switch
        {
            SliceOrientation.Coronal => (volume.RowDirection, volume.Normal * -1, volume.ColumnDirection, volume.SpacingX, targetSpacingY),
            SliceOrientation.Sagittal => (volume.ColumnDirection, volume.Normal * -1, volume.RowDirection, volume.SpacingY, targetSpacingY),
            _ => (volume.RowDirection, volume.ColumnDirection, volume.Normal, volume.SpacingX, volume.SpacingY),
        };
    }

    private static (double HalfWidthMm, double HalfHeightMm, double MinDepthMm, double MaxDepthMm) ComputeProjectedBounds(
        SeriesVolume volume,
        Vector3D center,
        Vector3D row,
        Vector3D column,
        Vector3D normal)
    {
        double minDepth = double.PositiveInfinity;
        double maxDepth = double.NegativeInfinity;
        double maxAbsRow = 0;
        double maxAbsColumn = 0;

        foreach (Vector3D corner in GetVolumeCorners(volume))
        {
            Vector3D relative = corner - center;
            double u = relative.Dot(row);
            double v = relative.Dot(column);
            double w = relative.Dot(normal);
            maxAbsRow = Math.Max(maxAbsRow, Math.Abs(u));
            maxAbsColumn = Math.Max(maxAbsColumn, Math.Abs(v));
            minDepth = Math.Min(minDepth, w);
            maxDepth = Math.Max(maxDepth, w);
        }

        if (double.IsInfinity(minDepth) || double.IsInfinity(maxDepth))
        {
            minDepth = 0;
            maxDepth = 0;
        }

        return (maxAbsRow, maxAbsColumn, minDepth, maxDepth);
    }

    private static IEnumerable<Vector3D> GetVolumeCorners(SeriesVolume volume)
    {
        double[] xs = [0, Math.Max(0, volume.SizeX - 1)];
        double[] ys = [0, Math.Max(0, volume.SizeY - 1)];
        double[] zs = [0, Math.Max(0, volume.SizeZ - 1)];

        foreach (double z in zs)
        {
            foreach (double y in ys)
            {
                foreach (double x in xs)
                {
                    yield return volume.VoxelToPatient(x, y, z);
                }
            }
        }
    }

    private static double GetVolumeHalfDiagonalMm(SeriesVolume volume)
    {
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        double extentX = Math.Max(0, (volume.SizeX - 1) * spacingX);
        double extentY = Math.Max(0, (volume.SizeY - 1) * spacingY);
        double extentZ = Math.Max(0, (volume.SizeZ - 1) * spacingZ);
        return Math.Sqrt(extentX * extentX + extentY * extentY + extentZ * extentZ) * 0.5;
    }

    private static double GetMinimumVolumeSpacing(SeriesVolume volume)
    {
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;
        return Math.Max(0.1, Math.Min(spacingX, Math.Min(spacingY, spacingZ)));
    }

    private static double ComputeObliqueSliceSpacing(SeriesVolume volume, Vector3D normal)
    {
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;

        double voxelX = normal.Dot(volume.RowDirection) / spacingX;
        double voxelY = normal.Dot(volume.ColumnDirection) / spacingY;
        double voxelZ = normal.Dot(volume.Normal) / spacingZ;
        double voxelMagnitude = Math.Sqrt((voxelX * voxelX) + (voxelY * voxelY) + (voxelZ * voxelZ));
        return voxelMagnitude > 1e-6 ? 1.0 / voxelMagnitude : Math.Max(spacingX, Math.Max(spacingY, spacingZ));
    }

    private static void GetOrthographicFixedCoordinates(
        SeriesVolume volume,
        SliceOrientation orientation,
        int column,
        int row,
        int outputWidth,
        int outputHeight,
        out double fixedA,
        out double fixedB)
    {
        switch (orientation)
        {
            case SliceOrientation.Axial:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputIndexToSource(row, outputHeight, volume.SizeY);
                break;
            case SliceOrientation.Coronal:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputRowToSourceZ(row, outputHeight, volume.SizeZ);
                break;
            case SliceOrientation.Sagittal:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeY);
                fixedB = MapOutputRowToSourceZ(row, outputHeight, volume.SizeZ);
                break;
            default:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputIndexToSource(row, outputHeight, volume.SizeY);
                break;
        }
    }

    private static (double X, double Y, double Z) GetOrthographicSamplePosition(
        SliceOrientation orientation,
        double fixedA,
        double fixedB,
        double rayPosition) => orientation switch
    {
        SliceOrientation.Axial => (fixedA, fixedB, rayPosition),
        SliceOrientation.Coronal => (fixedA, rayPosition, fixedB),
        SliceOrientation.Sagittal => (rayPosition, fixedA, fixedB),
        _ => (fixedA, fixedB, rayPosition),
    };

    private static double MapOutputIndexToSource(int index, int outputSize, int sourceSize)
    {
        if (outputSize <= 1 || sourceSize <= 1)
        {
            return 0;
        }

        return index / (double)(outputSize - 1) * (sourceSize - 1);
    }

    private static Vector3D RotateAroundAxis(Vector3D value, Vector3D axis, double angleRadians)
    {
        Vector3D unitAxis = axis.Normalize();
        if (unitAxis.Length <= 1e-6 || Math.Abs(angleRadians) <= 1e-8)
        {
            return value;
        }

        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        return (value * cos)
            + (unitAxis.Cross(value) * sin)
            + (unitAxis * (unitAxis.Dot(value) * (1.0 - cos)));
    }
}
