namespace KPACS.Viewer.Models;

public enum CenterlineSeedKind
{
    Start,
    End,
    Guide,
}

public enum CenterlinePathKind
{
    SeedPolylinePreview,
    Computed,
}

public enum CenterlineComputationStatus
{
    Preview,
    Success,
    Failed,
}

public sealed record CenterlineSeed
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public CenterlineSeedKind Kind { get; init; }
    public Vector3D PatientPoint { get; init; }
    public string SeriesInstanceUid { get; init; } = string.Empty;
    public string SopInstanceUid { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CenterlineSeedSet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = string.Empty;
    public Guid? SegmentationMaskId { get; init; }
    public CenterlineSeed? StartSeed { get; init; }
    public CenterlineSeed? EndSeed { get; init; }
    public List<CenterlineSeed> GuideSeeds { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool HasRequiredEndpoints => StartSeed is not null && EndSeed is not null;

    public int SeedCount =>
        (StartSeed is null ? 0 : 1) +
        (EndSeed is null ? 0 : 1) +
        GuideSeeds.Count;

    public string PendingSeedLabel => StartSeed is null
        ? "start"
        : EndSeed is null
            ? "end"
            : "guide";

    public IReadOnlyList<CenterlineSeed> GetOrderedSeeds()
    {
        List<CenterlineSeed> ordered = [];
        if (StartSeed is not null)
        {
            ordered.Add(StartSeed);
        }

        ordered.AddRange(GuideSeeds.OrderBy(seed => seed.CreatedUtc));

        if (EndSeed is not null)
        {
            ordered.Add(EndSeed);
        }

        return ordered;
    }

    public CenterlineSeedSet UpsertSeed(CenterlineSeed seed)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return seed.Kind switch
        {
            CenterlineSeedKind.Start => this with
            {
                StartSeed = seed,
                UpdatedUtc = now,
            },
            CenterlineSeedKind.End => this with
            {
                EndSeed = seed,
                UpdatedUtc = now,
            },
            CenterlineSeedKind.Guide => this with
            {
                GuideSeeds = [.. GuideSeeds, seed],
                UpdatedUtc = now,
            },
            _ => this,
        };
    }

    public CenterlineSeedSet BindSegmentationMask(Guid? segmentationMaskId) => this with
    {
        SegmentationMaskId = segmentationMaskId,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    public CenterlineSeedSet RemoveLastSeed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (GuideSeeds.Count > 0)
        {
            return this with
            {
                GuideSeeds = [.. GuideSeeds.Take(GuideSeeds.Count - 1)],
                UpdatedUtc = now,
            };
        }

        if (EndSeed is not null)
        {
            return this with
            {
                EndSeed = null,
                UpdatedUtc = now,
            };
        }

        if (StartSeed is not null)
        {
            return this with
            {
                StartSeed = null,
                UpdatedUtc = now,
            };
        }

        return this;
    }
}

public sealed record CenterlinePathPoint
{
    public Vector3D PatientPoint { get; init; }
    public double ArcLengthMm { get; init; }

    /// <summary>
    /// Max-inscribed-sphere radius (mm) at this point, when the producing service computed one.
    /// Null for legacy paths and seed previews.
    /// </summary>
    public double? RadiusMm { get; init; }

    /// <summary>
    /// Local curvature (1/mm) at this point, when available. Null for legacy paths.
    /// </summary>
    public double? CurvaturePerMm { get; init; }
}

public sealed record CenterlinePath
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SeedSetId { get; init; }
    public Guid? SegmentationMaskId { get; init; }
    public CenterlinePathKind Kind { get; init; } = CenterlinePathKind.SeedPolylinePreview;
    public CenterlineComputationStatus Status { get; init; } = CenterlineComputationStatus.Preview;
    public List<CenterlinePathPoint> Points { get; init; } = [];
    public double TotalLengthMm { get; init; }
    public double QualityScore { get; init; }
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Per-point max-inscribed-sphere radius (mm), parallel to <see cref="Points"/> when the
    /// producing service computed one. Null for legacy paths and seed previews.
    /// </summary>
    public double[]? RadiiMm { get; init; }

    /// <summary>
    /// Per-point curvature (1/mm), parallel to <see cref="Points"/> when available.
    /// </summary>
    public double[]? Curvatures { get; init; }

    /// <summary>
    /// Per-point tortuosity contribution (arc-length ratio), parallel to <see cref="Points"/> when available.
    /// </summary>
    public double[]? Tortuosities { get; init; }

    public bool HasRenderablePath => Points.Count >= 2;

    public static CenterlinePath CreateSeedPolylinePreview(
        Guid seedSetId,
        IEnumerable<CenterlineSeed> orderedSeeds,
        Guid? existingPathId = null,
        DateTimeOffset? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(orderedSeeds);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<CenterlinePathPoint> points = [];
        Vector3D? previousPoint = null;
        double totalLength = 0;

        foreach (CenterlineSeed seed in orderedSeeds)
        {
            if (previousPoint is Vector3D previous)
            {
                totalLength += (seed.PatientPoint - previous).Length;
            }

            points.Add(new CenterlinePathPoint
            {
                PatientPoint = seed.PatientPoint,
                ArcLengthMm = totalLength,
            });

            previousPoint = seed.PatientPoint;
        }

        return new CenterlinePath
        {
            Id = existingPathId ?? Guid.NewGuid(),
            SeedSetId = seedSetId,
            SegmentationMaskId = null,
            Kind = CenterlinePathKind.SeedPolylinePreview,
            Status = CenterlineComputationStatus.Preview,
            Points = points,
            TotalLengthMm = totalLength,
            QualityScore = 0,
            Summary = points.Count >= 2
                ? $"Seed preview with {points.Count} points ({totalLength:0.0} mm)."
                : "Seed preview awaiting more points.",
            CreatedUtc = createdUtc ?? now,
            UpdatedUtc = now,
        };
    }
}