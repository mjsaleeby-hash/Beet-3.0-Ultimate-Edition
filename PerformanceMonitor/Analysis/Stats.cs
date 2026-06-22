namespace BeetsBackup.PerfMon.Analysis;

/// <summary>Small statistics helpers shared by the analysis reports.</summary>
public static class Stats
{
    /// <summary>Linear-interpolated percentile (p in [0,1]). Returns 0 for an empty set.</summary>
    public static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var rank = p * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        return sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
    }
}
