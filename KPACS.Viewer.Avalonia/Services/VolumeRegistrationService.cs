using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Services;

public readonly record struct VolumeTranslationRegistration(
    Vector3D Translation,
    double Confidence,
    int OverlapSamples);

public static class VolumeRegistrationService
{
    private static readonly Lock SyncRoot = new();
    private static readonly Dictionary<string, VolumeTranslationRegistration?> RegistrationCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, VolumeProfile> ProfileCache = new(StringComparer.Ordinal);

    public static bool TryTransformPatientPoint(
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        Vector3D sourcePatientPoint,
        out Vector3D targetPatientPoint,
        out VolumeTranslationRegistration registration)
    {
        if (TryGetRegistration(sourceVolume, targetVolume, out registration))
        {
            targetPatientPoint = sourcePatientPoint + registration.Translation;
            return true;
        }

        targetPatientPoint = sourcePatientPoint;
        return false;
    }

    public static bool TryGetRegistration(
        SeriesVolume sourceVolume,
        SeriesVolume targetVolume,
        out VolumeTranslationRegistration registration)
    {
        if (!HasVoxelData(sourceVolume) || !HasVoxelData(targetVolume))
        {
            registration = default;
            return false;
        }

        string cacheKey = $"{sourceVolume.SeriesInstanceUid}->{targetVolume.SeriesInstanceUid}";
        lock (SyncRoot)
        {
            if (RegistrationCache.TryGetValue(cacheKey, out VolumeTranslationRegistration? cached))
            {
                registration = cached ?? default;
                return cached is not null;
            }
        }

        VolumeTranslationRegistration? computed = ComputeRegistration(sourceVolume, targetVolume);
        lock (SyncRoot)
        {
            RegistrationCache[cacheKey] = computed;
        }

        registration = computed ?? default;
        return computed is not null;
    }

    private static VolumeTranslationRegistration? ComputeRegistration(SeriesVolume sourceVolume, SeriesVolume targetVolume)
    {
        VolumeProfile sourceProfile = GetOrBuildProfile(sourceVolume);
        VolumeProfile targetProfile = GetOrBuildProfile(targetVolume);

        if (sourceProfile.SampleCount < 12 || targetProfile.SampleCount < 12)
        {
            return null;
        }

        double stepMm = Math.Max(3.0, Math.Min(sourceProfile.StepMm, targetProfile.StepMm));
        double maxShiftMm = Math.Min(600.0, Math.Max(sourceProfile.RangeMm, targetProfile.RangeMm));
        int maxShiftSteps = Math.Max(1, (int)Math.Round(maxShiftMm / stepMm));

        double bestScore = double.NegativeInfinity;
        int bestShiftSteps = 0;
        int bestOverlapSamples = 0;

        for (int shiftSteps = -maxShiftSteps; shiftSteps <= maxShiftSteps; shiftSteps++)
        {
            double shiftMm = shiftSteps * stepMm;
            if (!TryScoreShift(sourceProfile, targetProfile, shiftMm, out double score, out int overlapSamples))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestShiftSteps = shiftSteps;
                bestOverlapSamples = overlapSamples;
            }
        }

        if (bestOverlapSamples < 10 || bestScore < 0.30)
        {
            return null;
        }

        double bestShiftMmFinal = bestShiftSteps * stepMm;
        if (!TryEstimateInPlaneTranslation(sourceProfile, targetProfile, bestShiftMmFinal, out double deltaX, out double deltaY, out int translationSamples))
        {
            return null;
        }

        int overlap = Math.Min(bestOverlapSamples, translationSamples);
        if (overlap < 8)
        {
            return null;
        }

        return new VolumeTranslationRegistration(
            new Vector3D(deltaX, deltaY, bestShiftMmFinal),
            Math.Clamp(bestScore, -1.0, 1.0),
            overlap);
    }

    private static bool TryScoreShift(VolumeProfile source, VolumeProfile target, double shiftMm, out double score, out int overlapSamples)
    {
        score = 0;
        overlapSamples = 0;

        double sumSource = 0;
        double sumTarget = 0;
        double sumSourceSq = 0;
        double sumTargetSq = 0;
        double sumCross = 0;
        double sumDerivativeCross = 0;
        double sumDerivativeSourceSq = 0;
        double sumDerivativeTargetSq = 0;

        foreach (double z in source.SamplePositions)
        {
            if (!source.TryInterpolate(z, out SliceProfileSample sourceSample) ||
                !target.TryInterpolate(z + shiftMm, out SliceProfileSample targetSample))
            {
                continue;
            }

            if (sourceSample.BodyFraction < 0.015 && targetSample.BodyFraction < 0.015)
            {
                continue;
            }

            overlapSamples++;
            sumSource += sourceSample.BodyFraction;
            sumTarget += targetSample.BodyFraction;
            sumSourceSq += sourceSample.BodyFraction * sourceSample.BodyFraction;
            sumTargetSq += targetSample.BodyFraction * targetSample.BodyFraction;
            sumCross += sourceSample.BodyFraction * targetSample.BodyFraction;

            sumDerivativeCross += sourceSample.Derivative * targetSample.Derivative;
            sumDerivativeSourceSq += sourceSample.Derivative * sourceSample.Derivative;
            sumDerivativeTargetSq += targetSample.Derivative * targetSample.Derivative;
        }

        if (overlapSamples < 8)
        {
            return false;
        }

        double corr = ComputeCorrelation(sumSource, sumTarget, sumSourceSq, sumTargetSq, sumCross, overlapSamples);
        double derivativeCorr = (sumDerivativeSourceSq > 1e-6 && sumDerivativeTargetSq > 1e-6)
            ? sumDerivativeCross / Math.Sqrt(sumDerivativeSourceSq * sumDerivativeTargetSq)
            : 0;

        score = (corr * 0.72) + (derivativeCorr * 0.28);
        return true;
    }

    private static double ComputeCorrelation(
        double sumSource,
        double sumTarget,
        double sumSourceSq,
        double sumTargetSq,
        double sumCross,
        int sampleCount)
    {
        double numerator = (sampleCount * sumCross) - (sumSource * sumTarget);
        double denominatorLeft = (sampleCount * sumSourceSq) - (sumSource * sumSource);
        double denominatorRight = (sampleCount * sumTargetSq) - (sumTarget * sumTarget);
        if (denominatorLeft <= 1e-6 || denominatorRight <= 1e-6)
        {
            return 0;
        }

        return numerator / Math.Sqrt(denominatorLeft * denominatorRight);
    }

    private static bool TryEstimateInPlaneTranslation(
        VolumeProfile source,
        VolumeProfile target,
        double shiftMm,
        out double deltaX,
        out double deltaY,
        out int overlapSamples)
    {
        deltaX = 0;
        deltaY = 0;
        overlapSamples = 0;

        double sumWeight = 0;
        double sumDx = 0;
        double sumDy = 0;

        foreach (double z in source.SamplePositions)
        {
            if (!source.TryInterpolate(z, out SliceProfileSample sourceSample) ||
                !target.TryInterpolate(z + shiftMm, out SliceProfileSample targetSample))
            {
                continue;
            }

            double weight = Math.Min(sourceSample.BodyFraction, targetSample.BodyFraction);
            if (weight < 0.03)
            {
                continue;
            }

            overlapSamples++;
            sumWeight += weight;
            sumDx += (targetSample.CentroidX - sourceSample.CentroidX) * weight;
            sumDy += (targetSample.CentroidY - sourceSample.CentroidY) * weight;
        }

        if (sumWeight <= 1e-6 || overlapSamples < 8)
        {
            return false;
        }

        deltaX = sumDx / sumWeight;
        deltaY = sumDy / sumWeight;
        return true;
    }

    private static VolumeProfile GetOrBuildProfile(SeriesVolume volume)
    {
        lock (SyncRoot)
        {
            if (ProfileCache.TryGetValue(volume.SeriesInstanceUid, out VolumeProfile? cached))
            {
                return cached;
            }
        }

        VolumeProfile profile = BuildProfile(volume);
        lock (SyncRoot)
        {
            ProfileCache[volume.SeriesInstanceUid] = profile;
        }

        return profile;
    }

    private static bool HasVoxelData(SeriesVolume volume)
    {
        int expectedLength;
        try
        {
            checked
            {
                expectedLength = volume.SizeX * volume.SizeY * volume.SizeZ;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return expectedLength > 0
            && volume.VoxelCount >= expectedLength;
    }

    private static VolumeProfile BuildProfile(SeriesVolume volume)
    {
        int width = volume.SizeX;
        int height = volume.SizeY;
        int stepX = Math.Max(1, width / 96);
        int stepY = Math.Max(1, height / 96);
        int sampledPixels = ((width + stepX - 1) / stepX) * ((height + stepY - 1) / stepY);
        double threshold = volume.MinValue + ((volume.MaxValue - volume.MinValue) * 0.12);

        List<double> slicePositions = new(volume.SizeZ);
        List<SliceProfileSample> samples = new(volume.SizeZ);

        for (int sliceIndex = 0; sliceIndex < volume.SizeZ; sliceIndex++)
        {
            long tissueCount = 0;
            double sumX = 0;
            double sumY = 0;

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    if (volume.GetVoxel(x, y, sliceIndex) <= threshold)
                    {
                        continue;
                    }

                    tissueCount++;
                    sumX += x;
                    sumY += y;
                }
            }

            double centroidVoxelX = tissueCount > 0 ? sumX / tissueCount : (width - 1) / 2.0;
            double centroidVoxelY = tissueCount > 0 ? sumY / tissueCount : (height - 1) / 2.0;
            Vector3D sliceCenter = volume.VoxelToPatient(centroidVoxelX, centroidVoxelY, sliceIndex);
            double bodyFraction = sampledPixels > 0 ? tissueCount / (double)sampledPixels : 0;

            slicePositions.Add(sliceCenter.Z);
            samples.Add(new SliceProfileSample(sliceCenter.Z, bodyFraction, sliceCenter.X, sliceCenter.Y, 0));
        }

        if (slicePositions.Count == 0)
        {
            return new VolumeProfile([], [], 1.0);
        }

        List<(double Position, SliceProfileSample Sample)> zipped = slicePositions
            .Select((position, index) => (position, samples[index]))
            .OrderBy(item => item.position)
            .ToList();

        double stepMm = zipped.Count > 1
            ? Math.Max(1.0, zipped.Zip(zipped.Skip(1), (left, right) => right.Position - left.Position).Where(delta => delta > 0.1).DefaultIfEmpty(volume.SpacingZ).Average())
            : Math.Max(1.0, volume.SpacingZ);

        List<double> orderedPositions = zipped.Select(item => item.Position).ToList();
        List<SliceProfileSample> orderedSamples = zipped.Select(item => item.Sample).ToList();

        for (int index = 0; index < orderedSamples.Count; index++)
        {
            double derivative = 0;
            if (index > 0 && index < orderedSamples.Count - 1)
            {
                double dz = orderedPositions[index + 1] - orderedPositions[index - 1];
                if (Math.Abs(dz) > 1e-6)
                {
                    derivative = (orderedSamples[index + 1].BodyFraction - orderedSamples[index - 1].BodyFraction) / dz;
                }
            }

            orderedSamples[index] = orderedSamples[index] with { Derivative = derivative };
        }

        return new VolumeProfile(orderedPositions, orderedSamples, stepMm);
    }

    private sealed class VolumeProfile(List<double> samplePositions, List<SliceProfileSample> samples, double stepMm)
    {
        public List<double> SamplePositions { get; } = samplePositions;
        public List<SliceProfileSample> Samples { get; } = samples;
        public double StepMm { get; } = stepMm;
        public int SampleCount => Samples.Count;
        public double RangeMm => SamplePositions.Count > 1 ? SamplePositions[^1] - SamplePositions[0] : 0;

        public bool TryInterpolate(double position, out SliceProfileSample sample)
        {
            sample = default;
            if (SamplePositions.Count == 0 || position < SamplePositions[0] || position > SamplePositions[^1])
            {
                return false;
            }

            int upperIndex = SamplePositions.BinarySearch(position);
            if (upperIndex >= 0)
            {
                sample = Samples[upperIndex];
                return true;
            }

            upperIndex = ~upperIndex;
            if (upperIndex <= 0 || upperIndex >= SamplePositions.Count)
            {
                return false;
            }

            int lowerIndex = upperIndex - 1;
            double lowerPosition = SamplePositions[lowerIndex];
            double upperPosition = SamplePositions[upperIndex];
            double span = upperPosition - lowerPosition;
            double t = span > 1e-6 ? (position - lowerPosition) / span : 0;

            SliceProfileSample lower = Samples[lowerIndex];
            SliceProfileSample upper = Samples[upperIndex];
            sample = new SliceProfileSample(
                position,
                Lerp(lower.BodyFraction, upper.BodyFraction, t),
                Lerp(lower.CentroidX, upper.CentroidX, t),
                Lerp(lower.CentroidY, upper.CentroidY, t),
                Lerp(lower.Derivative, upper.Derivative, t));
            return true;
        }
    }

    private readonly record struct SliceProfileSample(
        double PositionZ,
        double BodyFraction,
        double CentroidX,
        double CentroidY,
        double Derivative);

    private static double Lerp(double start, double end, double t) => start + ((end - start) * t);
}