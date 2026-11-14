namespace Bogware.Bql;

/// <summary>One query to run, and the bounds the host puts on it.</summary>
public sealed class BqlExecutionContext
{
    /// <summary>The query text.</summary>
    public required string Query { get; set; }

    /// <summary>
    /// The language the text is in. Null or <c>bql</c> means BQL, which also reads SPL. Any other
    /// name must match a dialect registered in <see cref="BqlEngineOptions.Dialects"/>.
    /// </summary>
    public string? Dialect { get; set; }

    /// <summary>Earliest time to read, which overrides the query's own <c>earliest=</c>.</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>Latest time to read, which overrides the query's own <c>latest=</c>.</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>Rows to return at most. Null takes <see cref="BqlEngineOptions.DefaultResultLimit"/>.</summary>
    public int? Limit { get; set; }

    /// <summary>The dataset to scope the query to, when the source has datasets.</summary>
    public string? Dataset { get; set; }

    /// <summary>
    /// Datasets the caller may read: names, or prefixes ending in <c>*</c>. Null means all. The
    /// source enforces it; the engine passes it through.
    /// </summary>
    public IReadOnlyList<string>? AllowedDatasets { get; set; }

    /// <summary>Where a previous page stopped, as the source wrote it. Opaque to the engine.</summary>
    public string? After { get; set; }

    /// <summary>Longest the query may run. Null takes <see cref="BqlEngineOptions.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; set; }
}

/// <summary>What a query returned.</summary>
public sealed class BqlResult
{
    /// <summary>The rows. Each one is a <see cref="BqlRow"/>.</summary>
    public List<dynamic> Events { get; set; } = new();

    /// <summary>The first row's columns, in order, or null when there are no rows.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>How the query ran.</summary>
    public BqlStatistics? Statistics { get; set; }
}

/// <summary>How a query ran.</summary>
public sealed class BqlStatistics
{
    /// <summary>Rows read from the source.</summary>
    public long EventsScanned { get; set; }

    /// <summary>Rows returned.</summary>
    public long EventsMatched { get; set; }

    /// <summary>Time spent.</summary>
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>Distinct values per field, when a stage counted them.</summary>
    public Dictionary<string, long>? FieldCardinality { get; set; }

    /// <summary>
    /// True when more rows matched than the engine could read, so filters and aggregates
    /// only saw part of them and their results may be incomplete.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// A median or percentile came from a sample of its values, because there were too many to keep.
    /// </summary>
    public bool Approximate { get; set; }

    /// <summary>True when the source computed the aggregate instead of the pipeline.</summary>
    public bool PushedDown { get; set; }
}

/// <summary>The query text is not valid BQL, or uses something the engine can't do.</summary>
public class BqlParseException : Exception
{
    /// <summary>Where in the text, when known.</summary>
    public string? Position { get; set; }

    public BqlParseException(string message, string? position = null)
        : base(position != null ? $"{message} at {position}" : message)
    {
        Position = position;
    }

    /// <summary>A failure with the error behind it, for the host to log. The message stays generic.</summary>
    public BqlParseException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One stage of a running pipeline. The engine builds these from the parsed query; hosts don't
/// usually make them.
/// </summary>
public abstract class BqlOperator
{
    /// <summary>The stage's name, as written in a query.</summary>
    public abstract string Name { get; }

    /// <summary>Runs the stage over its input.</summary>
    public abstract Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages that work through all their rows at once check for cancellation this often, so a
    /// query's time limit stops them too.
    /// </summary>
    internal const int CancellationCheckInterval = 1024;

    /// <summary>The query's limits and schema, so a stage keeps to them.</summary>
    internal BqlBudget? Budget { get; set; }

    /// <summary>The schema of the query this stage runs in.</summary>
    internal BqlSchema Schema => Budget?.Schema ?? BqlSchema.Default;

    /// <summary>A new row that matches field names as the query's schema does.</summary>
    internal BqlRow NewRow() => Schema.NewRow();
}
