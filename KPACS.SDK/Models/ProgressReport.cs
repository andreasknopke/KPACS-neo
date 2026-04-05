namespace KPACS.SDK.Models;

/// <summary>
/// Progress event emitted by long-running plugin operations.
/// </summary>
public sealed class ProgressReport
{
    /// <summary>Current step index (0-based). Used for multi-phase operations.</summary>
    public int Step { get; init; }

    /// <summary>Total number of steps (0 = indeterminate).</summary>
    public int TotalSteps { get; init; }

    /// <summary>Percentage complete within the current step (0–100, or -1 for indeterminate).</summary>
    public int PercentComplete { get; init; } = -1;

    /// <summary>Human-readable status message (e.g. "Loading model…", "Predicting part 2 of 5…").</summary>
    public string StatusMessage { get; init; } = string.Empty;
}
