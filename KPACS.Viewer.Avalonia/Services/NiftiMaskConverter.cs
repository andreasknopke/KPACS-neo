using KPACS.SDK.Models;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

/// <summary>
/// Converts NIfTI segmentation mask output from plugins into the K-PACS
/// <see cref="SegmentationMask3D"/> format for use in centerline planning,
/// cross-section analysis, and overlay rendering.
/// </summary>
public static class NiftiMaskConverter
{
    /// <summary>
    /// Convert a single-structure binary NIfTI mask (.nii.gz) to a
    /// <see cref="SegmentationMask3D"/>.
    /// </summary>
    public static SegmentationMask3D FromBinaryNifti(
        string niftiPath,
        string structureName,
        SeriesVolume volume,
        string studyInstanceUid,
        string? notes = null)
    {
        (byte[] packedBits, int foreground) = ReadNiftiToPackedBits(niftiPath, volume, labelFilter: null);

        return BuildMask(
            structureName, volume, studyInstanceUid, packedBits, foreground, notes);
    }

    /// <summary>
    /// Extract one label from a multilabel NIfTI volume and produce a
    /// <see cref="SegmentationMask3D"/> for that structure.
    /// </summary>
    public static SegmentationMask3D FromMultilabelNifti(
        string multilabelPath,
        int label,
        string structureName,
        SeriesVolume volume,
        string studyInstanceUid,
        string? notes = null)
    {
        (byte[] packedBits, int foreground) = ReadNiftiToPackedBits(multilabelPath, volume, label);

        return BuildMask(
            structureName, volume, studyInstanceUid, packedBits, foreground, notes);
    }

    /// <summary>
    /// Extract all labels from a multilabel NIfTI volume and produce one
    /// <see cref="SegmentationMask3D"/> per structure.
    /// </summary>
    /// <param name="multilabelPath">Path to the multilabel .nii.gz file.</param>
    /// <param name="structures">
    /// Plugin result structures — provides label, id, display name, region.
    /// </param>
    /// <param name="volume">The source volume for geometry alignment.</param>
    /// <param name="studyInstanceUid">Study instance UID for provenance.</param>
    public static IReadOnlyList<SegmentationMask3D> FromMultilabelNiftiAll(
        string multilabelPath,
        IReadOnlyList<SegmentedStructure> structures,
        SeriesVolume volume,
        string studyInstanceUid)
    {
        short[] voxelData = ReadNiftiInt16(multilabelPath);
        long totalVoxels = (long)volume.SizeX * volume.SizeY * volume.SizeZ;
        var results = new List<SegmentationMask3D>(structures.Count);

        foreach (SegmentedStructure structure in structures)
        {
            int byteCount = (int)((totalVoxels + 7) / 8);
            byte[] packed = new byte[byteCount];
            int foreground = 0;

            long count = Math.Min(totalVoxels, voxelData.Length);
            for (long i = 0; i < count; i++)
            {
                if (voxelData[i] == structure.Label)
                {
                    packed[i >> 3] |= (byte)(1 << ((int)i & 7));
                    foreground++;
                }
            }

            if (foreground == 0)
            {
                continue; // skip empty structures
            }

            results.Add(BuildMask(
                structure.DisplayName ?? structure.Id,
                volume,
                studyInstanceUid,
                packed,
                foreground,
                $"Label {structure.Label}, Region: {structure.Region}"));
        }

        return results;
    }

    /// <summary>
    /// Create a <see cref="SegmentationMask3D"/> from raw packed-bit data
    /// (e.g. received from a remote render server).
    /// </summary>
    public static SegmentationMask3D FromPackedBits(
        byte[] packedBits,
        int foregroundCount,
        string structureName,
        SeriesVolume volume,
        string studyInstanceUid,
        string? notes = null)
    {
        return BuildMask(structureName, volume, studyInstanceUid, packedBits, foregroundCount, notes);
    }

    // ── Private helpers ─────────────────────────────────────────

    private static SegmentationMask3D BuildMask(
        string name,
        SeriesVolume volume,
        string studyInstanceUid,
        byte[] packedBits,
        int foregroundCount,
        string? notes)
    {
        var geometry = new VolumeGridGeometry(
            volume.SizeX, volume.SizeY, volume.SizeZ,
            volume.SpacingX, volume.SpacingY, volume.SpacingZ,
            volume.Origin,
            volume.RowDirection, volume.ColumnDirection, volume.Normal,
            volume.FrameOfReferenceUid);

        var storage = new SegmentationMaskStorage(
            SegmentationMaskStorageKind.PackedBits,
            foregroundCount,
            "bit-packed",
            packedBits);

        // Compute statistics.
        var buffer = SegmentationMaskBuffer.FromStorage(geometry, storage);
        SegmentationMaskStatistics? stats = buffer.ComputeStatistics();

        var metadata = new SegmentationMaskMetadata(
            SegmentationMaskSourceKind.Imported,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            sourceMeasurementId: null,
            notes: notes,
            revision: 0,
            statistics: stats);

        return new SegmentationMask3D(
            Guid.NewGuid(),
            name,
            volume.SeriesInstanceUid,
            volume.FrameOfReferenceUid,
            studyInstanceUid,
            geometry,
            storage,
            metadata);
    }

    private static (byte[] PackedBits, int ForegroundCount) ReadNiftiToPackedBits(
        string path, SeriesVolume volume, int? labelFilter)
    {
        short[] data = ReadNiftiInt16(path);
        long totalVoxels = (long)volume.SizeX * volume.SizeY * volume.SizeZ;
        int byteCount = (int)((totalVoxels + 7) / 8);
        byte[] packed = new byte[byteCount];
        int foreground = 0;

        long count = Math.Min(totalVoxels, data.Length);
        for (long i = 0; i < count; i++)
        {
            bool set = labelFilter is null
                ? data[i] > 0
                : data[i] == labelFilter.Value;

            if (set)
            {
                packed[i >> 3] |= (byte)(1 << ((int)i & 7));
                foreground++;
            }
        }

        return (packed, foreground);
    }

    /// <summary>
    /// Minimal NIfTI-1 reader that extracts the voxel payload as INT16.
    /// Supports .nii and .nii.gz. Handles datatype INT16, UINT8, INT32, FLOAT32.
    /// </summary>
    private static short[] ReadNiftiInt16(string path)
    {
        using Stream fs = File.OpenRead(path);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress)
            : fs;

        using var br = new BinaryReader(stream);

        int headerSize = br.ReadInt32();
        if (headerSize != 348)
        {
            throw new InvalidDataException($"Unexpected NIfTI header size: {headerSize}");
        }

        br.ReadBytes(36);           // skip to dim[0] at offset 40
        short ndim = br.ReadInt16();
        short dimX = br.ReadInt16();
        short dimY = br.ReadInt16();
        short dimZ = br.ReadInt16();
        br.ReadBytes(8);            // skip remaining dims

        br.ReadBytes(12);           // intent_p1/2/3
        br.ReadInt16();             // intent_code
        short datatype = br.ReadInt16();
        short bitpix = br.ReadInt16();

        br.ReadInt16();             // slice_start
        br.ReadBytes(32);           // pixdim[0..7]
        float voxOffset = br.ReadSingle();
        float sclSlope = br.ReadSingle();
        float sclInter = br.ReadSingle();

        int headerBytesRead = 4 + 36 + 2 + 2 + 2 + 2 + 8 + 12 + 2 + 2 + 2 + 2 + 32 + 4 + 4 + 4;
        int skipBytes = (int)voxOffset - headerBytesRead;
        if (skipBytes > 0)
        {
            br.ReadBytes(skipBytes);
        }

        long voxelCount = (long)dimX * dimY * dimZ;
        short[] result = new short[voxelCount];

        switch (datatype)
        {
            case 4: // INT16
                for (long i = 0; i < voxelCount; i++)
                    result[i] = br.ReadInt16();
                break;
            case 2: // UINT8
                for (long i = 0; i < voxelCount; i++)
                    result[i] = br.ReadByte();
                break;
            case 8: // INT32
                for (long i = 0; i < voxelCount; i++)
                    result[i] = (short)Math.Clamp(br.ReadInt32(), short.MinValue, short.MaxValue);
                break;
            case 16: // FLOAT32
                for (long i = 0; i < voxelCount; i++)
                    result[i] = (short)Math.Clamp(br.ReadSingle(), short.MinValue, short.MaxValue);
                break;
            default:
                throw new NotSupportedException($"Unsupported NIfTI datatype: {datatype}");
        }

        if (sclSlope != 0 && sclSlope != 1)
        {
            for (long i = 0; i < voxelCount; i++)
            {
                result[i] = (short)Math.Clamp(result[i] * sclSlope + sclInter, short.MinValue, short.MaxValue);
            }
        }

        return result;
    }
}
