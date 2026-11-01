namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Turns raw progress (phase plus query and module counts) into an overall percentage and an
/// estimate of the remaining time. It is a pure function that lives in Core so it stays
/// deterministic, testable and independent of the UI.
///
/// Which of the two expensive phases dominates depends on the environment. Against a remote
/// database it is the extraction queries, locally it is module parsing. Both therefore get a
/// wide band that moves with their own progress instead of a fixed jump.
/// </summary>
public static class ProgressMath
{
    public const int ExtractStart = 5;   // connection and preflight finished
    public const int ExtractEnd = 62;    // every extraction query finished
    public const int BuildEnd = 96;      // snapshots built, modules normalized
    public const int CompareEnd = 98;    // comparison finished, only mapping is left

    public static int Percent(ProgressSnapshot s)
    {
        var percent = s.Phase switch
        {
            ComparePhase.Connecting => 2,
            ComparePhase.Extracting => s.QueriesTotal <= 0
                ? ExtractStart
                : ExtractStart + (int)((ExtractEnd - ExtractStart) * (double)s.QueriesDone / s.QueriesTotal),
            ComparePhase.Building => s.BuildTotal <= 0
                ? BuildEnd
                : ExtractEnd + (int)((BuildEnd - ExtractEnd) * (double)s.BuildDone / s.BuildTotal),
            ComparePhase.Comparing => CompareEnd,
            _ => 100,
        };
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Remaining seconds, extrapolated linearly from the elapsed time. No estimate is given while
    /// the percentage is still small and noisy, or once the work is done, because an early or
    /// meaningless number is misleading.
    /// </summary>
    public static int? EtaSeconds(int percent, TimeSpan elapsed)
    {
        if (percent is < 10 or >= 100 || elapsed.TotalSeconds < 1.0) return null;
        var remaining = elapsed.TotalSeconds * (100 - percent) / percent;
        return (int)Math.Ceiling(remaining);
    }

    /// <summary>Percentage and remaining time in one call.</summary>
    public static (int Percent, int? EtaSeconds) Compute(ProgressSnapshot s, TimeSpan elapsed)
    {
        var percent = Percent(s);
        return (percent, EtaSeconds(percent, elapsed));
    }
}
