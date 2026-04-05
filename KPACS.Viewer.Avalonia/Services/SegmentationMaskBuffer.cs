using System.Numerics;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class SegmentationMaskBuffer
{
    private readonly byte[] _bits;

    public SegmentationMaskBuffer(VolumeGridGeometry geometry)
        : this(geometry, CreateEmptyData(geometry))
    {
    }

    private SegmentationMaskBuffer(VolumeGridGeometry geometry, byte[] bits)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(bits);

        int expectedLength = GetRequiredByteCount(geometry.TotalVoxelCount);
        if (bits.Length != expectedLength)
        {
            throw new ArgumentException("Bit buffer length does not match geometry.", nameof(bits));
        }

        Geometry = geometry;
        _bits = bits;
    }

    public VolumeGridGeometry Geometry { get; }

    public int SizeX => Geometry.SizeX;

    public int SizeY => Geometry.SizeY;

    public int SizeZ => Geometry.SizeZ;

    public bool Get(int x, int y, int z)
    {
        int linearIndex = GetLinearIndex(x, y, z);
        (int byteIndex, byte bitMask) = GetByteIndex(linearIndex);
        return (_bits[byteIndex] & bitMask) != 0;
    }

    public void Set(int x, int y, int z, bool value)
    {
        int linearIndex = GetLinearIndex(x, y, z);
        (int byteIndex, byte bitMask) = GetByteIndex(linearIndex);
        if (value)
        {
            _bits[byteIndex] |= bitMask;
        }
        else
        {
            _bits[byteIndex] &= (byte)~bitMask;
        }
    }

    public int CountForeground()
    {
        int total = 0;
        foreach (byte value in _bits)
        {
            total += BitOperations.PopCount((uint)value);
        }

        return total;
    }

    public IEnumerable<int> EnumerateForegroundLinearIndices()
    {
        long totalVoxelCount = Geometry.TotalVoxelCount;
        for (int byteIndex = 0; byteIndex < _bits.Length; byteIndex++)
        {
            byte current = _bits[byteIndex];
            while (current != 0)
            {
                int bitOffset = BitOperations.TrailingZeroCount((uint)current);
                int linearIndex = (byteIndex << 3) + bitOffset;
                if (linearIndex >= totalVoxelCount)
                {
                    yield break;
                }

                yield return linearIndex;
                current = (byte)(current & (current - 1));
            }
        }
    }

    public SegmentationMaskStatistics? ComputeStatistics()
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int minZ = int.MaxValue;
        int maxX = -1;
        int maxY = -1;
        int maxZ = -1;
        int foregroundCount = 0;

        for (int z = 0; z < SizeZ; z++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int x = 0; x < SizeX; x++)
                {
                    if (!Get(x, y, z))
                    {
                        continue;
                    }

                    foregroundCount++;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    minZ = Math.Min(minZ, z);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    maxZ = Math.Max(maxZ, z);
                }
            }
        }

        if (foregroundCount == 0)
        {
            return null;
        }

        return new SegmentationMaskStatistics(
            foregroundCount * Geometry.VoxelVolumeCubicMillimeters,
            new VoxelIndex3D(minX, minY, minZ),
            new VoxelIndex3D(maxX, maxY, maxZ));
    }

    public SegmentationMaskStorage ToStorage(string encoding = "bit-packed") =>
        new(
            SegmentationMaskStorageKind.PackedBits,
            CountForeground(),
            encoding,
            [.. _bits]);

    /// <summary>
    /// Extract a single axial (Z-plane) slice from the packed-bit mask.
    /// Returns a flat boolean array of size SizeX × SizeY (x-fastest).
    /// </summary>
    public bool[] ExtractAxialSlice(int z)
    {
        if (z < 0 || z >= SizeZ)
        {
            return [];
        }

        int sliceSize = SizeX * SizeY;
        bool[] slice = new bool[sliceSize];
        int baseLinearIndex = z * sliceSize;

        for (int i = 0; i < sliceSize; i++)
        {
            int linearIndex = baseLinearIndex + i;
            int byteIndex = linearIndex >> 3;
            byte bitMask = (byte)(1 << (linearIndex & 7));
            slice[i] = (_bits[byteIndex] & bitMask) != 0;
        }

        return slice;
    }

    /// <summary>
    /// Extract a single axial (Z-plane) slice and write foreground voxels
    /// into a pre-allocated BGRA buffer with the specified overlay colour.
    /// Only foreground voxels are written; background pixels are untouched.
    /// </summary>
    public void CompositeAxialSliceIntoBgra(
        int z, byte[] bgra, int stride,
        int imageWidth, int imageHeight,
        byte overlayB, byte overlayG, byte overlayR, byte overlayA)
    {
        if (z < 0 || z >= SizeZ || bgra is null)
        {
            return;
        }

        int maskW = SizeX;
        int maskH = SizeY;
        int baseLinearIndex = z * maskW * maskH;

        for (int my = 0; my < maskH && my < imageHeight; my++)
        {
            int rowBase = baseLinearIndex + (my * maskW);
            int bgraRowBase = my * stride;

            for (int mx = 0; mx < maskW && mx < imageWidth; mx++)
            {
                int linearIndex = rowBase + mx;
                int byteIndex = linearIndex >> 3;
                byte bitMask = (byte)(1 << (linearIndex & 7));

                if ((_bits[byteIndex] & bitMask) == 0)
                {
                    continue;
                }

                int px = bgraRowBase + (mx * 4);
                // Alpha blend: overlay on top of existing pixel.
                double alpha = overlayA / 255.0;
                double invAlpha = 1.0 - alpha;
                bgra[px] = (byte)Math.Clamp(bgra[px] * invAlpha + overlayB * alpha, 0, 255);
                bgra[px + 1] = (byte)Math.Clamp(bgra[px + 1] * invAlpha + overlayG * alpha, 0, 255);
                bgra[px + 2] = (byte)Math.Clamp(bgra[px + 2] * invAlpha + overlayR * alpha, 0, 255);
                bgra[px + 3] = 255;
            }
        }
    }

    public static SegmentationMaskBuffer FromStorage(VolumeGridGeometry geometry, SegmentationMaskStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (storage.Kind != SegmentationMaskStorageKind.PackedBits)
        {
            throw new NotSupportedException($"Unsupported segmentation storage kind: {storage.Kind}.");
        }

        SegmentationMaskBuffer buffer = new(geometry, [.. storage.Data]);
        int actualForegroundCount = buffer.CountForeground();
        if (actualForegroundCount != storage.ForegroundVoxelCount)
        {
            throw new InvalidOperationException("Stored foreground voxel count does not match the payload.");
        }

        return buffer;
    }

    private int GetLinearIndex(int x, int y, int z)
    {
        if (!Geometry.ContainsVoxel(x, y, z))
        {
            throw new ArgumentOutOfRangeException($"Voxel coordinate ({x}, {y}, {z}) is outside the mask bounds.");
        }

        return x + (y * SizeX) + (z * SizeX * SizeY);
    }

    private static (int ByteIndex, byte BitMask) GetByteIndex(int linearIndex)
    {
        int byteIndex = linearIndex >> 3;
        byte bitMask = (byte)(1 << (linearIndex & 7));
        return (byteIndex, bitMask);
    }

    private static byte[] CreateEmptyData(VolumeGridGeometry geometry) =>
        new byte[GetRequiredByteCount(geometry.TotalVoxelCount)];

    private static int GetRequiredByteCount(long voxelCount)
    {
        checked
        {
            return (int)((voxelCount + 7L) / 8L);
        }
    }
}
