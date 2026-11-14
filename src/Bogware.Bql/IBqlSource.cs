namespace Bogware.Bql;

/// <summary>
/// Where a query's rows come from. A host implements this over its own data: a table, an index,
/// a list in memory. The engine asks for rows with a <see cref="BqlPushdown"/> and stops reading
/// when it has what it needs.
/// </summary>
/// <remarks>
/// The source must apply what <see cref="BqlPushdown"/> asks for exactly when its
/// <see cref="Capabilities"/> say it does. Anything else in the pushdown is a hint the source may
/// ignore: the engine checks it again.
/// </remarks>
public interface IBqlSource
{
    /// <summary>What the rows look like.</summary>
    BqlSchema Schema { get; }

    /// <summary>What this source does itself, so the engine knows what it can leave to it.</summary>
    BqlSourceCapabilities Capabilities { get; }

    /// <summary>
    /// The rows that match, in the order asked for when the source can sort, else in its own order.
    /// Rows should be made with <see cref="BqlSchema.NewRow"/>.
    /// </summary>
    IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default);
}

/// <summary>A source that can count matching rows without reading them.</summary>
public interface IBqlCountingSource : IBqlSource
{
    /// <summary>How many rows match. Only called when every filter was pushed down exactly.</summary>
    ValueTask<long> CountAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default);
}

/// <summary>
/// A source that can compute an aggregate itself, rather than sending every row to the engine.
/// It must give exactly the answer the engine would, or return null so the engine computes it.
/// </summary>
public interface IBqlAggregatingSource : IBqlSource
{
    /// <summary>Computes the aggregate, or returns null if this source can't match the engine's answer.</summary>
    ValueTask<BqlAggregateResult?> AggregateAsync(BqlAggregateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a source does itself. Everything is off unless the source turns it on.</summary>
public sealed class BqlSourceCapabilities
{
    /// <summary>Nothing pushed down: the engine reads every row and does all the work.</summary>
    public static BqlSourceCapabilities None { get; } = new();

    /// <summary>
    /// The source keeps only rows within <see cref="BqlPushdown.StartTime"/> and
    /// <see cref="BqlPushdown.EndTime"/>, inclusive, read from the schema's time field.
    /// When false, the engine checks the bounds itself.
    /// </summary>
    public bool TimeBounds { get; init; }

    /// <summary>
    /// The source keeps only rows whose text has every one of <see cref="BqlPushdown.TextTerms"/>
    /// as a whole word, ignoring case, exactly as <see cref="SearchTerms.Matches"/> does.
    /// </summary>
    public bool TextSearch { get; init; }

    /// <summary>The source returns rows in <see cref="BqlPushdown.Sort"/> order when that is the time field.</summary>
    public bool TimeOrder { get; init; }

    /// <summary>
    /// Which predicates the source applies exactly as the pipeline would. The engine removes those
    /// from the pipeline. Null pushes none.
    /// </summary>
    public Func<BqlPredicate, bool>? ExactPredicates { get; init; }

    /// <summary>Whether the source applies a predicate exactly.</summary>
    public bool CanApply(BqlPredicate predicate) => ExactPredicates?.Invoke(predicate) ?? false;
}

/// <summary>What the engine asks a source for.</summary>
public sealed class BqlPushdown
{
    /// <summary>Earliest time, inclusive, or null.</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>Latest time, inclusive, or null.</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Words every row's text has, as <see cref="SearchTerms.Parse"/> splits them. Only set when the
    /// source can search text.
    /// </summary>
    public IReadOnlyList<string>? TextTerms { get; set; }

    /// <summary>Conditions every row meets. All must hold.</summary>
    public List<BqlPredicate> Predicates { get; set; } = [];

    /// <summary>
    /// Datasets to read, when the query names one. A source may ignore it; the query still
    /// checks the dataset field itself.
    /// </summary>
    public IReadOnlyList<string>? Datasets { get; set; }

    /// <summary>Datasets the caller may read: names or prefixes ending in <c>*</c>. Null means all.</summary>
    public IReadOnlyList<string>? AllowedDatasets { get; set; }

    /// <summary>The order the engine wants rows in.</summary>
    public BqlSortOrder? Sort { get; set; }

    /// <summary>
    /// Most rows the engine will take, when it knows. A hint for sources that read in pages; the
    /// engine stops reading on its own.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>Where a previous page stopped, passed through from the caller.</summary>
    public string? After { get; set; }

    /// <summary>
    /// Whether the rows need the schema's text field. False only when the engine has shown that
    /// no stage reads the text and it can't reach the output, as in <c>stats count by ext</c>.
    /// Then a source may leave the text field and its synonyms out of the rows, which saves
    /// reading large text. It must still apply <see cref="TextTerms"/> exactly if it claimed
    /// <see cref="BqlSourceCapabilities.TextSearch"/>. True by default.
    /// </summary>
    public bool NeedsText { get; set; } = true;

    /// <summary>A copy, to change for one read.</summary>
    public BqlPushdown Clone() => new()
    {
        StartTime = StartTime,
        EndTime = EndTime,
        TextTerms = TextTerms,
        Predicates = [.. Predicates],
        Datasets = Datasets,
        AllowedDatasets = AllowedDatasets,
        Sort = Sort,
        Limit = Limit,
        After = After,
        NeedsText = NeedsText,
    };
}

/// <summary>One condition: a field compared with a literal value.</summary>
/// <param name="Field">The field, as the query wrote it.</param>
/// <param name="Operator">One of <c>=</c>, <c>!=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>.</param>
/// <param name="Value">The literal: a string, a number or a time.</param>
public sealed record BqlPredicate(string Field, string Operator, object? Value);
