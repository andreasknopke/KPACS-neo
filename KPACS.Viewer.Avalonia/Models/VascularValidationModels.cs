namespace KPACS.Viewer.Models;

public enum ValidationCheckState
{
    Pending,
    Pass,
    Fail,
}

public sealed record VascularValidationChecklistItem
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ValidationCheckState State { get; init; } = ValidationCheckState.Pending;

    public string Notes { get; init; } = string.Empty;
}

public sealed record VascularPerformanceBudget
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public double TargetLatencyMs { get; init; }

    public double? LastObservedLatencyMs { get; init; }

    public double? WorstObservedLatencyMs { get; init; }

    public int SampleCount { get; init; }

    public int OverBudgetCount { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public VascularPerformanceBudget Record(double elapsedMs)
    {
        bool overBudget = elapsedMs > TargetLatencyMs;
        return this with
        {
            LastObservedLatencyMs = elapsedMs,
            WorstObservedLatencyMs = WorstObservedLatencyMs is double worst ? Math.Max(worst, elapsedMs) : elapsedMs,
            SampleCount = SampleCount + 1,
            OverBudgetCount = OverBudgetCount + (overBudget ? 1 : 0),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }
}

public sealed record VascularValidationSnapshot
{
    public string ChecklistVersion { get; init; } = "Phase1-AortoIliac-CTA";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<VascularValidationChecklistItem> ChecklistItems { get; init; } = [];

    public List<VascularPerformanceBudget> PerformanceBudgets { get; init; } = [];

    public static VascularValidationSnapshot CreateDefault()
    {
        return new VascularValidationSnapshot
        {
            ChecklistItems =
            [
                new VascularValidationChecklistItem { Key = "dataset-aorto-iliac-cta", Title = "Aorto-iliac CTA dataset", Description = "Case has contrast-enhanced abdominal aorta and iliac coverage suitable for Phase 1 planning." },
                new VascularValidationChecklistItem { Key = "coverage-renal-to-iliac", Title = "Coverage adequate", Description = "Volume covers renal neck region through distal iliac landing zones without major truncation." },
                new VascularValidationChecklistItem { Key = "centerline-inside-lumen", Title = "Centerline inside lumen", Description = "Computed centerline stays within the lumen in neck, aneurysm, and iliac bends." },
                new VascularValidationChecklistItem { Key = "cross-section-orthogonal", Title = "Cross-section orthogonal", Description = "Orthogonal vessel sections stay stable and visually perpendicular during scrubbing." },
                new VascularValidationChecklistItem { Key = "curved-mpr-aligned", Title = "Curved MPR aligned", Description = "Curved MPR stays aligned with the same centerline station as the orthogonal cross-section." },
                new VascularValidationChecklistItem { Key = "landing-zones-sensible", Title = "Landing metrics sensible", Description = "Neck and distal landing markers produce plausible lengths, diameters, and angulation." },
                new VascularValidationChecklistItem { Key = "failure-modes-reviewed", Title = "Failure modes reviewed", Description = "Known risks such as calcification, thrombus, metal artifacts, or incomplete coverage were reviewed." },
            ],
            PerformanceBudgets =
            [
                new VascularPerformanceBudget { Key = "mask-edit-commit", Title = "Mask edit commit", TargetLatencyMs = 250 },
                new VascularPerformanceBudget { Key = "centerline-calculation", Title = "Centerline calculation", TargetLatencyMs = 1500 },
                new VascularPerformanceBudget { Key = "cross-section-scrub", Title = "Cross-section scrub", TargetLatencyMs = 120 },
                new VascularPerformanceBudget { Key = "curved-mpr-update", Title = "Curved MPR update", TargetLatencyMs = 250 },
                new VascularPerformanceBudget { Key = "edt-calculation", Title = "EDT calculation", TargetLatencyMs = 800 },
                new VascularPerformanceBudget { Key = "lumen-segmentation", Title = "Lumen segmentation", TargetLatencyMs = 10_000 },
                new VascularPerformanceBudget { Key = "chart-update", Title = "Diameter chart update", TargetLatencyMs = 100 },
                new VascularPerformanceBudget { Key = "graft-overlay-rebuild", Title = "Graft overlay rebuild", TargetLatencyMs = 150 },
            ],
        };
    }

    public VascularValidationSnapshot EnsureDefaults()
    {
        VascularValidationSnapshot defaults = CreateDefault();
        List<VascularValidationChecklistItem> checklist = [];
        foreach (VascularValidationChecklistItem defaultItem in defaults.ChecklistItems)
        {
            VascularValidationChecklistItem? existing = ChecklistItems.FirstOrDefault(item => string.Equals(item.Key, defaultItem.Key, StringComparison.Ordinal));
            checklist.Add(existing ?? defaultItem);
        }

        List<VascularPerformanceBudget> budgets = [];
        foreach (VascularPerformanceBudget defaultBudget in defaults.PerformanceBudgets)
        {
            VascularPerformanceBudget? existing = PerformanceBudgets.FirstOrDefault(item => string.Equals(item.Key, defaultBudget.Key, StringComparison.Ordinal));
            budgets.Add(existing ?? defaultBudget);
        }

        return this with
        {
            ChecklistItems = checklist,
            PerformanceBudgets = budgets,
        };
    }

    public VascularValidationSnapshot RecordPerformance(string key, double elapsedMs)
    {
        VascularValidationSnapshot current = EnsureDefaults();
        List<VascularPerformanceBudget> budgets = [];
        foreach (VascularPerformanceBudget budget in current.PerformanceBudgets)
        {
            budgets.Add(string.Equals(budget.Key, key, StringComparison.Ordinal) ? budget.Record(elapsedMs) : budget);
        }

        return current with
        {
            PerformanceBudgets = budgets,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    public VascularValidationSnapshot CycleChecklistState(string key)
    {
        VascularValidationSnapshot current = EnsureDefaults();
        List<VascularValidationChecklistItem> items = [];
        foreach (VascularValidationChecklistItem item in current.ChecklistItems)
        {
            if (!string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                items.Add(item);
                continue;
            }

            ValidationCheckState nextState = item.State switch
            {
                ValidationCheckState.Pending => ValidationCheckState.Pass,
                ValidationCheckState.Pass => ValidationCheckState.Fail,
                _ => ValidationCheckState.Pending,
            };

            items.Add(item with { State = nextState });
        }

        return current with
        {
            ChecklistItems = items,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }
}
