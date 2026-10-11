using System.Diagnostics;

// Records every latency, not a sample. Values are Stopwatch ticks converted to
// 100 ns units. Values below 12.8 us are exact; above that each power of two has
// 64 linear sub-buckets, so a reported bound is within 1.6% of the recorded value.
internal sealed class LatencyHistogram
{
    private const int SubBucketBits = 7;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int Exponents = 40;

    private readonly long[] _counts = new long[Exponents * SubBuckets];

    internal long Count { get; private set; }

    internal long MaxUnits { get; private set; }

    internal void RecordTicks(long ticks)
    {
        var units = Math.Max(1, ticks * 10_000_000 / Stopwatch.Frequency);
        _counts[Index(units)]++;
        Count++;
        if (units > MaxUnits) MaxUnits = units;
    }

    internal void Add(LatencyHistogram other)
    {
        for (var index = 0; index < _counts.Length; index++) _counts[index] += other._counts[index];
        Count += other.Count;
        MaxUnits = Math.Max(MaxUnits, other.MaxUnits);
    }

    // Returns the upper bound of the bucket containing the percentile, in milliseconds.
    internal double PercentileMilliseconds(double percentile)
    {
        if (Count == 0) return 0;
        var target = (long)Math.Ceiling(Count * percentile);
        long seen = 0;
        for (var index = 0; index < _counts.Length; index++)
        {
            seen += _counts[index];
            if (seen >= target) return Math.Min(UpperBound(index), MaxUnits) / 10_000.0;
        }

        return MaxUnits / 10_000.0;
    }

    // Sparse bucket dump so raw distributions can be re-analyzed later.
    internal IEnumerable<long[]> NonEmptyBuckets()
    {
        for (var index = 0; index < _counts.Length; index++)
        {
            if (_counts[index] != 0) yield return [UpperBound(index), _counts[index]];
        }
    }

    private static int Index(long units)
    {
        if (units < SubBuckets) return (int)units;
        var exponent = 63 - (int)long.LeadingZeroCount(units) - (SubBucketBits - 1);
        var mantissa = (int)(units >> exponent) - (SubBuckets / 2);
        var index = (exponent * (SubBuckets / 2)) + SubBuckets + mantissa - (SubBuckets / 2);
        return Math.Min(index, (Exponents * SubBuckets) - 1);
    }

    private static long UpperBound(int index)
    {
        if (index < SubBuckets) return index;
        var relative = index - SubBuckets;
        var exponent = (relative / (SubBuckets / 2)) + 1;
        var mantissa = (relative % (SubBuckets / 2)) + (SubBuckets / 2);
        return ((long)(mantissa + 1) << exponent) - 1;
    }
}
