using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

internal static class CenterlineCurvedMprRenderer
{
    public static CurvedMprRenderResult Render(
        SeriesVolume volume,
        CenterlinePath path,
        double fieldOfViewMm,
        int imageHeight,
        double slabThicknessMm,
        double axialRotationDegrees = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Points.Count == 0)
        {
            return CurvedMprRenderResult.Empty;
        }

        CurvedMprDisplayOrientation orientation = ResolveDisplayOrientation(path);

        int width = Math.Max(1, path.Points.Count);
        int height = Math.Max(32, imageHeight);
        double halfFieldOfView = Math.Max(5.0, fieldOfViewMm) * 0.5;
        double pixelSpacingMm = height <= 1 ? 1.0 : (halfFieldOfView * 2.0) / (height - 1);
        double slab = Math.Max(0, slabThicknessMm);
        int[] centerRows = new int[width];

        double axialRotationRadians = axialRotationDegrees * (Math.PI / 180.0);
        IReadOnlyList<CenterlineSampleFrame> frames = CenterlineFrameBuilder.BuildFrames(volume, path, axialRotationRadians);
        int slabSampleCount = slab > 0.25 ? Math.Max(3, (int)Math.Ceiling(slab / Math.Max(0.5, Math.Min(volume.SpacingX, volume.SpacingY)))) : 1;

        // Ensure the higher-Z (superior in LPS) end of the path is rendered
        // as station 0 so it appears at the top of the vertical display or the
        // left of the horizontal display. If the path runs from inferior to
        // superior, reverse the frame order.
        bool stationReversed = path.Points.Count >= 2
            && path.Points[0].PatientPoint.Z < path.Points[^1].PatientPoint.Z;
        if (stationReversed)
        {
            frames = frames.Reverse().ToList();
        }

        // --- GPU path: pack frames into a flat float buffer and dispatch to OpenCL ---
        short[] pixels = TryRenderOnGpu(volume, frames, width, height, pixelSpacingMm, slabSampleCount, slab);
        string backendLabel;

        if (pixels.Length == 0)
        {
            if (VolumeComputeBackend.CpuFallbackDisabled)
            {
                Console.Error.WriteLine($"[CPU·BLOCKED] Curved MPR fallback suppressed — returning blank image");
                pixels = new short[width * height];
                backendLabel = "NONE (CPU disabled)";
            }
            else
            {
                backendLabel = "CPU";
                // --- CPU fallback (original sequential loop, now parallelized) ---
                pixels = new short[width * height];
                Parallel.For(0, width, x =>
                {
                    CenterlineSampleFrame frame = frames[x];
                    for (int y = 0; y < height; y++)
                    {
                        double offsetMm = ((height - 1) * 0.5 - y) * pixelSpacingMm;
                        Vector3D sampleCenter = frame.PatientPoint + (frame.Normal * offsetMm);
                        double value = SampleSlab(volume, sampleCenter, frame.Binormal, slab, slabSampleCount);
                        pixels[(y * width) + x] = ClampToShort(value);
                    }
                });
            }
        }
        else
        {
            backendLabel = VolumeComputeBackend.CurrentStatus.DisplayName;
        }

        for (int x = 0; x < width; x++)
        {
            centerRows[x] = height / 2;
        }

        return orientation == CurvedMprDisplayOrientation.Vertical
            ? RotateVertical(width, height, pixelSpacingMm, pixels, backendLabel, stationReversed)
            : new CurvedMprRenderResult(width, height, pixelSpacingMm, pixels, centerRows, orientation, backendLabel, stationReversed);
    }

    /// <summary>
    /// Packs the per-station Frenet frames into a flat float array (12 floats per station)
    /// and dispatches to the GPU curved MPR kernel. Returns an empty array on failure.
    /// </summary>
    private static short[] TryRenderOnGpu(
        SeriesVolume volume,
        IReadOnlyList<CenterlineSampleFrame> frames,
        int width,
        int height,
        double pixelSpacingMm,
        int slabSampleCount,
        double slabThicknessMm)
    {
        if (!VolumeComputeBackend.CanUseOpenCl || frames.Count == 0)
        {
            return [];
        }

        // Pack frames: 12 floats per station (point, normal, binormal, padding)
        float[] frameData = new float[frames.Count * 12];
        for (int i = 0; i < frames.Count; i++)
        {
            int offset = i * 12;
            CenterlineSampleFrame f = frames[i];
            frameData[offset + 0] = (float)f.PatientPoint.X;
            frameData[offset + 1] = (float)f.PatientPoint.Y;
            frameData[offset + 2] = (float)f.PatientPoint.Z;
            frameData[offset + 3] = (float)f.Normal.X;
            frameData[offset + 4] = (float)f.Normal.Y;
            frameData[offset + 5] = (float)f.Normal.Z;
            frameData[offset + 6] = (float)f.Binormal.X;
            frameData[offset + 7] = (float)f.Binormal.Y;
            frameData[offset + 8] = (float)f.Binormal.Z;
            // [9..11] reserved
        }

        if (VolumeComputeBackend.TryRenderCurvedMpr(
                volume,
                frameData.AsSpan(),
                width,
                height,
                pixelSpacingMm,
                slabSampleCount,
                slabThicknessMm,
                out short[] gpuPixels))
        {
            return gpuPixels;
        }

        return [];
    }

    private static CurvedMprDisplayOrientation ResolveDisplayOrientation(CenterlinePath path)
    {
        if (path.Points.Count < 2)
        {
            return CurvedMprDisplayOrientation.Horizontal;
        }

        Vector3D first = path.Points[0].PatientPoint;
        Vector3D last = path.Points[^1].PatientPoint;
        Vector3D delta = last - first;
        double absX = Math.Abs(delta.X);
        double absY = Math.Abs(delta.Y);
        double absZ = Math.Abs(delta.Z);

        return absZ >= Math.Max(absX, absY)
            ? CurvedMprDisplayOrientation.Vertical
            : CurvedMprDisplayOrientation.Horizontal;
    }

    private static CurvedMprRenderResult RotateVertical(int width, int height, double pixelSpacingMm, short[] pixels, string backendLabel, bool stationReversed)
    {
        short[] rotated = new short[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int destinationX = height - 1 - y;
                int destinationY = x;
                rotated[(destinationY * height) + destinationX] = pixels[(y * width) + x];
            }
        }

        int[] centerRows = Enumerable.Repeat(height / 2, width).ToArray();
        return new CurvedMprRenderResult(height, width, pixelSpacingMm, rotated, centerRows, CurvedMprDisplayOrientation.Vertical, backendLabel, stationReversed);
    }

    private static double SampleSlab(SeriesVolume volume, Vector3D sampleCenter, Vector3D slabDirection, double slabThicknessMm, int sampleCount)
    {
        if (sampleCount <= 1 || slabThicknessMm <= 0.25 || slabDirection.Length <= 1e-6)
        {
            return Sample(volume, sampleCenter);
        }

        double maximum = double.MinValue;
        double halfThickness = slabThicknessMm * 0.5;
        for (int index = 0; index < sampleCount; index++)
        {
            double t = sampleCount == 1 ? 0 : index / (double)(sampleCount - 1);
            double offset = -halfThickness + (t * slabThicknessMm);
            Vector3D point = sampleCenter + (slabDirection * offset);
            double value = Sample(volume, point);
            if (value > maximum)
            {
                maximum = value;
            }
        }

        return maximum == double.MinValue ? 0 : maximum;
    }

    private static double Sample(SeriesVolume volume, Vector3D patientPoint)
    {
        (double vx, double vy, double vz) = volume.PatientToVoxel(patientPoint);
        return volume.TryGetVoxelInterpolated(vx, vy, vz, out double value) ? value : 0;
    }

    private static short ClampToShort(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        double rounded = Math.Round(value);
        return (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
    }
}

internal sealed class CurvedMprRenderResult
{
    public static CurvedMprRenderResult Empty { get; } = new(1, 1, 1.0, [0], [0], CurvedMprDisplayOrientation.Horizontal, "CPU");

    public CurvedMprRenderResult(int width, int height, double pixelSpacingMm, short[] pixels, int[] centerRows, CurvedMprDisplayOrientation orientation, string renderBackendLabel = "CPU", bool isStationReversed = false)
    {
        Width = width;
        Height = height;
        PixelSpacingMm = pixelSpacingMm;
        Pixels = pixels;
        CenterRows = centerRows;
        Orientation = orientation;
        RenderBackendLabel = renderBackendLabel;
        IsStationReversed = isStationReversed;
    }

    public int Width { get; }

    public int Height { get; }

    public double PixelSpacingMm { get; }

    public short[] Pixels { get; }

    public int[] CenterRows { get; }

    public CurvedMprDisplayOrientation Orientation { get; }

    public string RenderBackendLabel { get; }

    /// <summary>
    /// True when the station axis was reversed during rendering so that
    /// the higher-Z (superior) end of the path appears at the visual top.
    /// UI code must invert station-index ↔ position mappings when set.
    /// </summary>
    public bool IsStationReversed { get; }
}

internal enum CurvedMprDisplayOrientation
{
    Horizontal,
    Vertical,
}