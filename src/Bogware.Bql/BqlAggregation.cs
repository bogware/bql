namespace Bogware.Bql;

/// <summary>What to aggregate: <c>stats ... by ...</c>, or <c>timechart</c> when <see cref="Bucket"/> is set.</summary>
public sealed class BqlAggregateRequest
{
    /// <summary>Which rows, with the filters the optimizer could push down.</summary>
    public required BqlPushdown Filter { get; init; }

    /// <summary>Fields to group by, as written in the query.</summary>
    public IReadOnlyList<string> GroupBy { get; init; } = [];

    /// <summary>
    /// For a timechart, the width of each time bucket, counted from the start of year 1. The
    /// bucket's start goes in the schema's time field.
    /// </summary>
    public TimeSpan? Bucket { get; init; }

    /// <summary>The values to compute for each group.</summary>
    public required IReadOnlyList<BqlAggregateSpec> Aggregates { get; init; }

    /// <summary>More groups than this fail the query, so the source can stop at one more.</summary>
    public int MaxGroups { get; init; }

    /// <summary>
    /// Whether groups must be told apart exactly as .NET writes their values as text, which is how
    /// <c>top</c> and <c>rare</c> count. A source that writes some values differently returns null
    /// when this is set.
    /// </summary>
    public bool ExactTextKeys { get; init; }

    /// <summary>The start of the bucket a time falls in, counting from the start of year 1 as the engine does.</summary>
    public static DateTime BucketStart(DateTime time, TimeSpan bucket) =>
        new(time.Ticks / bucket.Ticks * bucket.Ticks, DateTimeKind.Utc);
}

/// <summary>Functions a source can be asked for.</summary>
public enum BqlAggregateKind
{
    Count,
    DistinctCount,
    Sum,
    Average,
    Min,
    Max,
    Percentile,
    SampleDeviation,
    PopulationDeviation,
    SampleVariance,
    PopulationVariance,
}

/// <summary>One value to compute for each group.</summary>
/// <param name="Kind">The function.</param>
/// <param name="Field">Its field, or null for counting rows.</param>
/// <param name="Percentile">For <see cref="BqlAggregateKind.Percentile"/>, from 0 to 100.</param>
/// <param name="OutputName">The column it goes in.</param>
public sealed record BqlAggregateSpec(BqlAggregateKind Kind, string? Field, double Percentile, string OutputName);

/// <summary>The groups, in the order the engine would produce them.</summary>
public sealed class BqlAggregateResult
{
    /// <summary>
    /// One row per group: the group values under their names (and the time field for a bucket),
    /// then each aggregate under its output name.
    /// </summary>
    public required List<Dictionary<string, object?>> Rows { get; init; }

    /// <summary>Rows the aggregate covered.</summary>
    public long EventsScanned { get; init; }

    /// <summary>Whether some value was estimated.</summary>
    public bool Approximate { get; init; }
}
