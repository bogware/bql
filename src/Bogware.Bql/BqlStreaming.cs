using System.Globalization;

namespace Bogware.Bql;

/// <summary>
/// Limits on what one query can hold in memory. Time is limited separately, by
/// <see cref="BqlExecutionContext.Timeout"/>.
/// </summary>
public sealed class BqlLimits
{
    /// <summary>
    /// Rows kept for stages that need all their input at once, such as <c>sort</c> without
    /// <c>head</c>, <c>eventstats</c> and <c>transaction</c>, and rows a <c>join</c> or
    /// <c>mvexpand</c> may produce. Beyond this the rows are cut off and the result marked truncated.
    /// Filters and aggregates don't keep rows, so this doesn't limit them. Default 100,000.
    /// </summary>
    public int MaxRowsInMemory { get; init; } = 100_000;

    /// <summary>Groups an aggregate can produce before the query fails. Default 100,000.</summary>
    public int MaxGroups { get; init; } = 100_000;

    /// <summary>
    /// Values kept across <c>dc</c>, <c>median</c> and percentile aggregates before the query fails.
    /// Default 20,000,000.
    /// </summary>
    public long MaxRetainedValues { get; init; } = 20_000_000;

    /// <summary>
    /// Values kept for one <c>median</c> or percentile. Beyond this it samples, and the result is
    /// marked approximate. Default 100,000.
    /// </summary>
    public int MaxExactPercentileValues { get; init; } = 100_000;

    /// <summary>
    /// Events read by one query before it stops and is marked truncated. 0 means no limit, leaving
    /// the query's timeout to stop it.
    /// </summary>
    public long MaxEventsScanned { get; init; }

    /// <summary>
    /// Times a query's regular expression may run out of time on a row before the query fails,
    /// rather than spend its whole time limit on one slow pattern. Default 8.
    /// </summary>
    public int MaxRegexTimeouts { get; init; } = 8;

    /// <summary>
    /// How long a pattern from the query (<c>rex</c>, <c>regex</c>, <c>match()</c>, <c>replace()</c>)
    /// may spend on one value. Default 250 ms.
    /// </summary>
    public TimeSpan RegexTimeoutPerRow { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Longest text a query may build in one value, and the most text across the values of one
    /// multi-value field. A value that grows past it fails the query. Default 1,000,000 characters.
    /// </summary>
    public int MaxValueLength { get; init; } = 1_000_000;

    /// <summary>Most values one multi-value field may hold when a query builds it. Default 10,000.</summary>
    public int MaxMultiValueCount { get; init; } = 10_000;

    /// <summary>
    /// Widest <c>streamstats window=</c>. Each row looks back over the window, so time grows with
    /// rows times window. Default 10,000.
    /// </summary>
    public int MaxStreamStatsWindow { get; init; } = 10_000;

    /// <summary>Most rows <c>makeresults</c> makes; more are cut off and the result marked truncated. Default 50,000.</summary>
    public int MaxMakeResults { get; init; } = 50_000;

    /// <summary>Longest pattern <c>like</c>, <c>match()</c> and <c>replace()</c> accept. Default 10,000 characters.</summary>
    public int MaxPatternLength { get; init; } = 10_000;

    /// <summary>Widest padding <c>lpad()</c>, <c>rpad()</c> and <c>printf()</c> add. Default 1,000 characters.</summary>
    public int MaxPadWidth { get; init; } = 1_000;

    /// <summary>
    /// Most digits <c>printf()</c> writes after the point. .NET allows hundreds of millions, so
    /// this keeps one call from allocating gigabytes. Default 100.
    /// </summary>
    public int MaxFormatPrecision { get; init; } = 100;

    /// <summary>
    /// Distinct values <c>fieldsummary</c> counts per field; past it new values aren't counted and
    /// the field's counts are marked inexact. Default 100,000.
    /// </summary>
    public int MaxFieldSummaryValues { get; init; } = 100_000;
}

/// <summary>Who a query runs for: what its caller may read. Passed to subsearches and to the host's providers.</summary>
/// <param name="AllowedDatasets">Datasets the caller may read, as in <see cref="BqlExecutionContext.AllowedDatasets"/>. Null means all.</param>
/// <param name="Dataset">The dataset the query is scoped to, or null.</param>
public sealed record BqlCaller(IReadOnlyList<string>? AllowedDatasets, string? Dataset)
{
    /// <summary>A caller with no restrictions.</summary>
    public static BqlCaller Anyone { get; } = new(null, null);
}

/// <summary>A query went over a limit, in a way that would make its answer wrong if it continued.</summary>
public sealed class BqlLimitExceededException : BqlParseException
{
    public BqlLimitExceededException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Part of a query failed in a way that would make its answer wrong, such as a subsearch, join or
/// macro that couldn't run. Reported to the caller instead of returning a quietly different answer.
/// </summary>
public sealed class BqlExecutionException : BqlParseException
{
    public BqlExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>A failure with the error that caused it, for the host to log. The message stays generic.</summary>
    public BqlExecutionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Sorts exceptions into those a query may handle and those it must never swallow.</summary>
internal static class BqlFailures
{
    /// <summary>The process is in trouble; nothing may catch and carry on.</summary>
    public static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or InsufficientExecutionStackException or StackOverflowException or AccessViolationException;

    /// <summary>Failures a stage passes on unchanged: cancellation, limits, and fatal errors.</summary>
    public static bool PassesThrough(Exception ex) =>
        ex is OperationCanceledException or BqlLimitExceededException || IsFatal(ex);
}

/// <summary>
/// What one query has used so far, and the scope it runs in: who it runs for, its time bounds, its
/// deadline and how deeply it is nested in other queries. Subsearches and macros get a nested
/// budget that shares the counters.
/// </summary>
internal sealed class BqlBudget
{
    private readonly Usage _usage;

    public BqlBudget(BqlLimits limits, BqlSchema? schema = null)
        : this(limits, schema ?? BqlSchema.Default, new Usage())
    {
    }

    private BqlBudget(BqlLimits limits, BqlSchema schema, Usage usage)
    {
        Limits = limits;
        Schema = schema;
        _usage = usage;
    }

    /// <summary>A budget with default limits, for stages run outside an engine.</summary>
    public static BqlBudget Unbounded() => new(new BqlLimits());

    public BqlLimits Limits { get; }

    /// <summary>The schema of the rows the query reads.</summary>
    public BqlSchema Schema { get; }

    /// <summary>Who the query runs for. Subsearches run for the same caller.</summary>
    public BqlCaller Caller { get; init; } = BqlCaller.Anyone;

    /// <summary>The caller's time bounds, which subsearches keep.</summary>
    public DateTime? StartTime { get; init; }

    /// <summary>The caller's time bounds, which subsearches keep.</summary>
    public DateTime? EndTime { get; init; }

    /// <summary>When the outermost query's time runs out. Subsearches get what is left.</summary>
    public DateTime Deadline { get; init; } = DateTime.MaxValue;

    /// <summary>How deeply this query sits inside subsearches and macros. The outermost is 0.</summary>
    public int Depth { get; init; }

    public bool Truncated
    {
        get => _usage.Truncated;
        set => _usage.Truncated = value;
    }

    public bool Approximate
    {
        get => _usage.Approximate;
        set => _usage.Approximate = value;
    }

    /// <summary>The same budget, <paramref name="levels"/> deeper, as stages inside a macro see it.</summary>
    public BqlBudget Deeper(int levels) => new(Limits, Schema, _usage)
    {
        Caller = Caller,
        StartTime = StartTime,
        EndTime = EndTime,
        Deadline = Deadline,
        Depth = Depth + levels,
    };

    public void RetainValue()
    {
        if (Interlocked.Increment(ref _usage.RetainedValues) > Limits.MaxRetainedValues)
        {
            throw new BqlLimitExceededException(
                $"The query keeps more than {Limits.MaxRetainedValues:N0} distinct or percentile values. Narrow the time range or group by fewer fields.");
        }
    }

    public void CheckGroupCount(int count)
    {
        if (count > Limits.MaxGroups)
        {
            throw new BqlLimitExceededException(
                $"The query produces more than {Limits.MaxGroups:N0} groups. Narrow the time range or group by fewer fields.");
        }
    }

    /// <summary>Counters shared by a query and the subsearches and macros inside it.</summary>
    private sealed class Usage
    {
        public long RetainedValues;
        public bool Truncated;
        public bool Approximate;
    }
}

/// <summary>
/// The limits of the query running on this logical thread, for code that has no budget to hand,
/// such as expression functions. The engine enters a scope for each query; outside one, the
/// defaults apply.
/// </summary>
internal static class BqlScope
{
    private static readonly AsyncLocal<BqlLimits?> Current = new();
    private static readonly BqlLimits Defaults = new();

    public static BqlLimits Limits => Current.Value ?? Defaults;

    public static IDisposable Enter(BqlLimits limits)
    {
        var previous = Current.Value;
        Current.Value = limits;
        return new Restore(previous);
    }

    private sealed class Restore(BqlLimits? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// A query's first stateful stage. Row stages feed it a row at a time, so what it holds in memory
/// depends on what it computes, not on how many events it reads.
/// </summary>
internal interface IRowSink
{
    /// <summary>True when more rows can't change the result, so reading can stop.</summary>
    bool IsDone { get; }

    /// <summary>Rows this sink would still take, or null if it takes any number.</summary>
    int? Remaining { get; }

    void Add(object row);

    IEnumerable<dynamic> Complete();
}

/// <summary>Keeps the first rows, for <c>head</c> and for pipelines ending in filters.</summary>
internal sealed class TakeSink : IRowSink
{
    private readonly int _count;
    private readonly List<dynamic> _rows = new();

    public TakeSink(int count)
    {
        _count = count;
    }

    public bool IsDone => _rows.Count >= _count;

    public int? Remaining => _count - _rows.Count;

    public void Add(object row) => _rows.Add(row);

    public IEnumerable<dynamic> Complete() => _rows;
}

/// <summary>Keeps rows for stages that need all their input, up to a limit.</summary>
internal sealed class MaterializeSink : IRowSink
{
    private readonly BqlBudget _budget;
    private readonly List<dynamic> _rows = new();

    public MaterializeSink(BqlBudget budget)
    {
        _budget = budget;
    }

    public bool IsDone => _rows.Count > _budget.Limits.MaxRowsInMemory;

    public int? Remaining => null;

    public void Add(object row) => _rows.Add(row);

    public IEnumerable<dynamic> Complete()
    {
        if (_rows.Count > _budget.Limits.MaxRowsInMemory)
        {
            _rows.RemoveRange(_budget.Limits.MaxRowsInMemory, _rows.Count - _budget.Limits.MaxRowsInMemory);
            _budget.Truncated = true;
        }

        return _rows;
    }
}

/// <summary>Keeps the last rows, for <c>tail</c>.</summary>
internal sealed class TailSink : IRowSink
{
    private readonly int _count;
    private readonly Queue<dynamic> _rows = new();

    public TailSink(int count)
    {
        _count = count;
    }

    public bool IsDone => _count == 0;

    public int? Remaining => null;

    public void Add(object row)
    {
        _rows.Enqueue(row);
        if (_rows.Count > _count)
        {
            _rows.Dequeue();
        }
    }

    public IEnumerable<dynamic> Complete() => _rows;
}

/// <summary>
/// Keeps the first rows in a sort order, for <c>sort</c> followed by <c>head</c>, without keeping
/// every row. Rows that compare equal stay in the order they arrived, as in a full sort.
/// </summary>
internal sealed class TopRowsSink : IRowSink
{
    private const int MinCompactionSize = 1024;

    private readonly LegacySortOperator _sort;
    private readonly int _count;
    private List<dynamic> _rows = new();

    public TopRowsSink(LegacySortOperator sort, int count)
    {
        _sort = sort;
        _count = count;
    }

    public bool IsDone => _count == 0;

    public int? Remaining => null;

    public void Add(object row)
    {
        _rows.Add(row);
        if (_rows.Count >= Math.Max(_count * 2, MinCompactionSize))
        {
            Compact();
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        Compact();
        return _rows;
    }

    private void Compact()
    {
        _rows = _sort.Order(_rows).Take(_count).ToList();
    }
}

/// <summary>Computes <c>stats</c> and two-axis <c>chart</c> columns, one row at a time.</summary>
internal sealed class StatsSink : IRowSink
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    private readonly BqlBudget _budget;
    private readonly Dictionary<string, Group> _groups = new();
    private readonly List<Group> _ordered = new();

    public StatsSink(List<AggregateFunction> aggregations, List<string> groupByFields, BqlBudget budget)
    {
        _aggregations = aggregations;
        _groupByFields = groupByFields;
        _budget = budget;

        // With no grouping there's always one row, even with no input.
        if (groupByFields.Count == 0)
        {
            _ordered.Add(new Group(string.Empty, null, aggregations, budget));
        }
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        Group group;
        if (_groupByFields.Count == 0)
        {
            group = _ordered[0];
        }
        else
        {
            // As in SPL, an event without every by-field isn't counted
            if (!BqlGroupKey.HasAll(dict, _groupByFields))
            {
                return;
            }

            var key = BqlGroupKey.For(dict, _groupByFields);
            if (!_groups.TryGetValue(key, out group!))
            {
                var values = new object?[_groupByFields.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = dict.TryGetValue(_groupByFields[i], out var value) ? value : null;
                }

                group = new Group(key, values, _aggregations, _budget);
                _groups[key] = group;
                _ordered.Add(group);
                _budget.CheckGroupCount(_ordered.Count);
            }
        }

        foreach (var accumulator in group.Accumulators)
        {
            accumulator.Add(dict);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_ordered.Count);
        foreach (var group in _ordered)
        {
            var row = _budget.Schema.NewRow();
            for (var i = 0; i < _groupByFields.Count; i++)
            {
                row[_groupByFields[i]] = group.Values![i];
            }

            for (var i = 0; i < _aggregations.Count; i++)
            {
                row[_aggregations[i].OutputName] = group.Accumulators[i].Result();
            }

            results.Add(row);
        }

        return results;
    }

    private sealed class Group
    {
        public Group(string key, object?[]? values, List<AggregateFunction> aggregations, BqlBudget budget)
        {
            Key = key;
            Values = values;
            Accumulators = aggregations.Select(aggregate => new AggregateAccumulator(aggregate, budget)).ToArray();
        }

        public string Key { get; }

        public object?[]? Values { get; }

        public AggregateAccumulator[] Accumulators { get; }
    }
}

/// <summary>Computes <c>timechart</c> one row at a time.</summary>
internal sealed class TimechartSink : IRowSink
{
    private readonly TimeSpan _span;
    private readonly AggregateFunction _function;
    private readonly string? _byField;
    private readonly BqlBudget _budget;
    private readonly Dictionary<long, Dictionary<string, AggregateAccumulator>> _buckets = new();
    private int _groupCount;

    public TimechartSink(TimeSpan span, AggregateFunction function, string? byField, BqlBudget budget)
    {
        // A query built by a dialect skips the parser's check, so check again here
        _span = span > TimeSpan.Zero ? span : throw new BqlExecutionException("timechart needs a span above zero");
        _function = function;
        _byField = byField;
        _budget = budget;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        if (_budget.Schema.TimeOf(dict) is not { } timestamp)
        {
            return;
        }

        var bucketTicks = timestamp.Ticks / _span.Ticks * _span.Ticks;
        if (!_buckets.TryGetValue(bucketTicks, out var bucket))
        {
            bucket = new Dictionary<string, AggregateAccumulator>();
            _buckets[bucketTicks] = bucket;
        }

        var key = _byField == null
            ? string.Empty
            : dict.TryGetValue(_byField, out var byValue) ? byValue?.ToString() ?? string.Empty : string.Empty;
        if (!bucket.TryGetValue(key, out var accumulator))
        {
            accumulator = new AggregateAccumulator(_function, _budget);
            bucket[key] = accumulator;
            _budget.CheckGroupCount(++_groupCount);
        }

        accumulator.Add(dict);
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_groupCount);
        foreach (var (ticks, bucket) in _buckets.OrderBy(pair => pair.Key))
        {
            var time = new DateTime(ticks, DateTimeKind.Utc);
            foreach (var (key, accumulator) in bucket)
            {
                var row = _budget.Schema.NewRow();
                row[_budget.Schema.TimeField] = time;
                if (_byField != null)
                {
                    row[_byField] = key;
                }

                row[_function.OutputName] = accumulator.Result();
                results.Add(row);
            }
        }

        return results;
    }
}

/// <summary>Computes <c>top</c> and <c>rare</c>, one row at a time.</summary>
internal sealed class ValueCountSink : IRowSink
{
    private readonly string _field;
    private readonly string? _byField;
    private readonly BqlBudget _budget;
    private readonly Func<IEnumerable<KeyValuePair<string, long>>, string?, IEnumerable<dynamic>> _complete;
    private readonly Dictionary<string, Dictionary<string, long>> _groups = new();
    private int _valueCount;

    public ValueCountSink(
        string field,
        string? byField,
        BqlBudget budget,
        Func<IEnumerable<KeyValuePair<string, long>>, string?, IEnumerable<dynamic>> complete)
    {
        _field = field;
        _byField = byField;
        _budget = budget;
        _complete = complete;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        if (!dict.TryGetValue(_field, out var value) || value == null)
        {
            return;
        }

        var groupKey = _byField == null
            ? string.Empty
            : dict.TryGetValue(_byField, out var byValue) ? byValue?.ToString() ?? string.Empty : string.Empty;
        if (!_groups.TryGetValue(groupKey, out var counts))
        {
            counts = new Dictionary<string, long>();
            _groups[groupKey] = counts;
        }

        var key = value.ToString() ?? string.Empty;
        if (counts.TryGetValue(key, out var count))
        {
            counts[key] = count + 1;
        }
        else
        {
            counts[key] = 1;
            _budget.CheckGroupCount(++_valueCount);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>();
        foreach (var (group, counts) in _groups)
        {
            results.AddRange(_complete(counts, _byField == null ? null : group));
        }

        return results;
    }
}

/// <summary>Computes a two-axis <c>chart</c>, one row at a time.</summary>
internal sealed class ChartSink : IRowSink
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly string _overField;
    private readonly string _byField;
    private readonly BqlBudget _budget;
    private readonly SortedSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<string, Dictionary<string, AggregateAccumulator[]>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private int _cellCount;

    public ChartSink(List<AggregateFunction> aggregations, string overField, string byField, BqlBudget budget)
    {
        _aggregations = aggregations;
        _overField = overField;
        _byField = byField;
        _budget = budget;
    }

    public bool IsDone => false;

    public int? Remaining => null;

    public void Add(object row)
    {
        var dict = (IDictionary<string, object?>)row;
        var over = Value(dict, _overField);
        var column = Value(dict, _byField);
        _columns.Add(column);

        if (!_rows.TryGetValue(over, out var cells))
        {
            cells = new Dictionary<string, AggregateAccumulator[]>(StringComparer.OrdinalIgnoreCase);
            _rows[over] = cells;
        }

        if (!cells.TryGetValue(column, out var accumulators))
        {
            accumulators = _aggregations.Select(aggregate => new AggregateAccumulator(aggregate, _budget)).ToArray();
            cells[column] = accumulators;
            _budget.CheckGroupCount(++_cellCount);
        }

        foreach (var accumulator in accumulators)
        {
            accumulator.Add(dict);
        }
    }

    public IEnumerable<dynamic> Complete()
    {
        var results = new List<dynamic>(_rows.Count);
        foreach (var (over, cells) in _rows)
        {
            var row = _budget.Schema.NewRow(); row[_overField] = over;
            foreach (var column in _columns)
            {
                cells.TryGetValue(column, out var accumulators);
                for (var i = 0; i < _aggregations.Count; i++)
                {
                    var aggregate = _aggregations[i];
                    var name = _aggregations.Count == 1 ? column : $"{aggregate.OutputName}: {column}";
                    row[name] = accumulators != null ? accumulators[i].Result() : aggregate.Name == "count" ? 0 : null;
                }
            }

            results.Add(row);
        }

        return results;
    }

    private static string Value(IDictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out var value) ? value?.ToString() ?? "NULL" : "NULL";
}

/// <summary>
/// One aggregate function over a stream of rows. Everything that computes an aggregate goes
/// through this, so <c>stats</c>, <c>timechart</c>, <c>chart</c>, <c>eventstats</c> and
/// <c>streamstats</c> all give the same answer.
/// </summary>
internal sealed class AggregateAccumulator
{
    private readonly AggregateFunction _function;
    private readonly BqlBudget _budget;
    private readonly Kind _kind;
    private readonly double _percentile;

    private long _count;
    private double _sum;
    private double _mean;
    private double _squaredDeviations;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private string? _minText;
    private string? _maxText;
    private DateTime? _minTime;
    private DateTime? _maxTime;
    private HashSet<string>? _distinct;
    private List<double>? _values;
    private Random? _sampler;
    private SortedSet<string>? _valueSet;
    private List<object?>? _list;
    private object? _first;
    private bool _hasFirst;
    private object? _last;
    private DateTime? _earliestTime;
    private object? _earliestValue;
    private DateTime? _latestTime;
    private object? _latestValue;
    private Dictionary<string, (long Count, object? Value, long Order)>? _modes;

    /// <summary>Most values list() keeps, as in SPL.</summary>
    public const int MaxListValues = 100;

    public AggregateAccumulator(AggregateFunction function, BqlBudget budget)
    {
        _function = function;
        _budget = budget;
        _kind = KindOf(function.Name, out _percentile);
    }

    private enum Kind
    {
        Count,
        DistinctCount,
        Sum,
        Average,
        Min,
        Max,
        Median,
        Percentile,
        SampleDeviation,
        PopulationDeviation,
        SampleVariance,
        PopulationVariance,
        Values,
        List,
        First,
        Last,
        Earliest,
        Latest,
        Range,
        Mode,
    }

    public void Add(IDictionary<string, object?> row)
    {
        if (_function.Expression is { } expression)
        {
            // count(eval(...)) counts rows where it's true; the others aggregate its value
            if (_kind == Kind.Count)
            {
                if (BqlExpressionEvaluator.EvaluateBool(expression, row))
                {
                    _count++;
                }

                return;
            }

            row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [_function.Field!] = BqlExpressionEvaluator.Evaluate(expression, row),
                [_budget.Schema.TimeField] = RowTime(row),
            };
        }

        switch (_kind)
        {
            case Kind.Values or Kind.List or Kind.First or Kind.Last or Kind.Earliest or Kind.Latest or Kind.Mode:
                AddValue(row);
                return;

            case Kind.Count:
                if (_function.Field == null || (row.TryGetValue(_function.Field, out var counted) && counted != null))
                {
                    _count++;
                }

                return;

            case Kind.DistinctCount:
                if (row.TryGetValue(_function.Field ?? string.Empty, out var distinct) && distinct?.ToString() is { } text)
                {
                    _distinct ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (_distinct.Add(text))
                    {
                        _budget.RetainValue();
                    }
                }

                return;
        }

        var number = LegacyStatsOperator.GetNumericValue(row, _function.Field ?? string.Empty);
        if (number is not { } value)
        {
            if (_kind is Kind.Min or Kind.Max && row.TryGetValue(_function.Field ?? string.Empty, out var raw))
            {
                // Times compare as times; as text, "9:59 PM" would come after "10:16 PM"
                if (AsTime(raw) is { } time)
                {
                    AddTime(time);
                }
                else if (raw?.ToString() is { } rawText)
                {
                    AddText(rawText);
                }
            }

            return;
        }

        _count++;
        _sum += value;
        _min = Math.Min(_min, value);
        _max = Math.Max(_max, value);

        // Welford's method: variance without keeping the values
        var delta = value - _mean;
        _mean += delta / _count;
        _squaredDeviations += delta * (value - _mean);

        if (_kind is Kind.Median or Kind.Percentile)
        {
            AddToSample(value);
        }
    }

    public object? Result()
    {
        switch (_kind)
        {
            case Kind.Count:
                return CountValue(_count);
            case Kind.DistinctCount:
                return _distinct?.Count ?? 0;
            case Kind.Values:
                return _valueSet is { Count: > 0 } set ? set.Cast<object?>().ToList() : null;
            case Kind.List:
                return _list is { Count: > 0 } list ? list.ToList() : null;
            case Kind.First:
                return _first;
            case Kind.Last:
                return _last;
            case Kind.Earliest:
                return _earliestValue;
            case Kind.Latest:
                return _latestValue;
            case Kind.Mode:
                return _modes is { Count: > 0 } modes
                    ? modes.Values.OrderByDescending(m => m.Count).ThenBy(m => m.Order).First().Value
                    : null;
        }

        if (_count == 0)
        {
            return _kind switch
            {
                Kind.Min => _minTime is { } earliest ? earliest : _minText,
                Kind.Max => _maxTime is { } latest ? latest : _maxText,
                _ => null,
            };
        }

        object? result = _kind switch
        {
            Kind.Sum => _sum,
            Kind.Average => _sum / _count,
            Kind.Min => _min,
            Kind.Max => _max,
            Kind.Range => _max - _min,
            Kind.Median => Percentile(AggregateFunction.MedianPercentile, medianStyle: true),
            Kind.Percentile => Percentile(_percentile, medianStyle: false),
            Kind.SampleDeviation => _count == 1 ? double.NaN : Math.Sqrt(_squaredDeviations / (_count - 1)),
            Kind.PopulationDeviation => Math.Sqrt(_squaredDeviations / _count),
            Kind.SampleVariance => _count == 1 ? double.NaN : _squaredDeviations / (_count - 1),
            Kind.PopulationVariance => _squaredDeviations / _count,
            _ => null,
        };

        // The sample deviation of a single value is undefined, which isn't a JSON number
        return result is double number && !double.IsFinite(number) ? null : result;
    }

    /// <summary>A count, as an int while it fits, which is what stages and clients expect.</summary>
    public static object CountValue(long count) => count <= int.MaxValue ? (int)count : count;

    private static Kind KindOf(string name, out double percentile)
    {
        percentile = AggregateFunction.DefaultPercentile;
        var lower = name.ToLowerInvariant();

        if (lower == "percentile")
        {
            return Kind.Percentile;
        }

        if (lower.StartsWith("perc", StringComparison.Ordinal)
            && double.TryParse(lower[4..], NumberStyles.Any, CultureInfo.InvariantCulture, out var perc))
        {
            percentile = Math.Clamp(perc, 0, 100);
            return Kind.Percentile;
        }

        if (lower.Length > 1 && lower[0] == 'p' && char.IsDigit(lower[1])
            && double.TryParse(lower[1..], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
        {
            percentile = Math.Clamp(p, 0, 100);
            return Kind.Percentile;
        }

        return name switch
        {
            "count" => Kind.Count,
            "dc" or "distinct_count" => Kind.DistinctCount,
            "sum" => Kind.Sum,
            "avg" or "mean" => Kind.Average,
            "min" => Kind.Min,
            "max" => Kind.Max,
            "median" => Kind.Median,
            "stdev" => Kind.SampleDeviation,
            "stdevp" => Kind.PopulationDeviation,
            "variance" => Kind.SampleVariance,
            "varp" => Kind.PopulationVariance,
            "values" => Kind.Values,
            "list" => Kind.List,
            "first" => Kind.First,
            "last" => Kind.Last,
            "earliest" => Kind.Earliest,
            "latest" => Kind.Latest,
            "range" => Kind.Range,
            "mode" => Kind.Mode,
            _ => throw new BqlParseException($"Unknown aggregate function: {name}"),
        };
    }

    private DateTime? RowTime(IDictionary<string, object?> row)
    {
        var schema = _budget.Schema;
        if (row.TryGetValue(schema.TimeField, out var time) && AsTime(time) is { } found)
        {
            return found;
        }

        foreach (var synonym in schema.TimeFieldSynonyms)
        {
            if (row.TryGetValue(synonym, out var other) && AsTime(other) is { } synonymTime)
            {
                return synonymTime;
            }
        }

        return null;
    }

    /// <summary>For the functions that keep values rather than a number.</summary>
    private void AddValue(IDictionary<string, object?> row)
    {
        if (!row.TryGetValue(_function.Field ?? string.Empty, out var value) || value == null)
        {
            return;
        }

        switch (_kind)
        {
            case Kind.Values:
                _valueSet ??= new SortedSet<string>(StringComparer.Ordinal);
                if (_valueSet.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty))
                {
                    _budget.RetainValue();
                }

                break;

            case Kind.List:
                _list ??= new List<object?>();
                if (_list.Count < MaxListValues)
                {
                    _list.Add(value);
                    _budget.RetainValue();
                }

                break;

            case Kind.First:
                if (!_hasFirst)
                {
                    _first = value;
                    _hasFirst = true;
                }

                break;

            case Kind.Last:
                _last = value;
                break;

            case Kind.Earliest or Kind.Latest:
                if (RowTime(row) is { } time)
                {
                    if (_kind == Kind.Earliest && (_earliestTime is not { } earliest || time < earliest))
                    {
                        (_earliestTime, _earliestValue) = (time, value);
                    }
                    else if (_kind == Kind.Latest && (_latestTime is not { } latest || time > latest))
                    {
                        (_latestTime, _latestValue) = (time, value);
                    }
                }

                break;

            case Kind.Mode:
                _modes ??= new Dictionary<string, (long, object?, long)>(StringComparer.Ordinal);
                var key = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (_modes.TryGetValue(key, out var seen))
                {
                    _modes[key] = (seen.Count + 1, seen.Value, seen.Order);
                }
                else
                {
                    _modes[key] = (1, value, _modes.Count);
                    _budget.RetainValue();
                }

                break;
        }
    }

    private static DateTime? AsTime(object? value) => value switch
    {
        DateTime time => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time,
        DateTimeOffset offset => offset.UtcDateTime,
        _ => null,
    };

    private void AddTime(DateTime time)
    {
        if (_minTime is not { } min || time < min)
        {
            _minTime = time;
        }

        if (_maxTime is not { } max || time > max)
        {
            _maxTime = time;
        }
    }

    private void AddText(string text)
    {
        if (_minText == null || StringComparer.OrdinalIgnoreCase.Compare(text, _minText) < 0)
        {
            _minText = text;
        }

        if (_maxText == null || StringComparer.OrdinalIgnoreCase.Compare(text, _maxText) >= 0)
        {
            _maxText = text;
        }
    }

    /// <summary>
    /// Keeps every value up to a limit, then a uniform sample (reservoir sampling), which estimates
    /// a percentile well without keeping everything.
    /// </summary>
    private void AddToSample(double value)
    {
        _values ??= new List<double>();
        var capacity = _budget.Limits.MaxExactPercentileValues;
        if (_values.Count < capacity)
        {
            _values.Add(value);
            _budget.RetainValue();
            return;
        }

        _budget.Approximate = true;

        // A fixed seed gives the same answer for the same input.
        _sampler ??= new Random(capacity);
        var slot = _sampler.NextInt64(_count);
        if (slot < capacity)
        {
            _values[(int)slot] = value;
        }
    }

    private double Percentile(double percentile, bool medianStyle)
    {
        var sorted = _values!;
        sorted.Sort();

        if (medianStyle)
        {
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var index = percentile / 100.0 * (sorted.Count - 1);
        var lower = Math.Clamp((int)Math.Floor(index), 0, sorted.Count - 1);
        var upper = Math.Clamp((int)Math.Ceiling(index), 0, sorted.Count - 1);
        return lower == upper ? sorted[lower] : sorted[lower] + ((index - lower) * (sorted[upper] - sorted[lower]));
    }
}

/// <summary>
/// Orders field values, which can be of mixed types once a field holds numbers in some events and
/// text in others. Numbers compare as numbers. Nulls sort first, then numbers, then everything else.
/// </summary>
internal sealed class BqlValueComparer : IComparer<object?>
{
    public static readonly BqlValueComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x == null || y == null)
        {
            return x == null ? y == null ? 0 : -1 : 1;
        }

        var xNumber = AsNumber(x);
        var yNumber = AsNumber(y);
        if (xNumber.HasValue && yNumber.HasValue)
        {
            return xNumber.Value.CompareTo(yNumber.Value);
        }

        if (xNumber.HasValue != yNumber.HasValue)
        {
            return xNumber.HasValue ? -1 : 1;
        }

        if (x is DateTime xTime && y is DateTime yTime)
        {
            return xTime.CompareTo(yTime);
        }

        return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }

    private static double? AsNumber(object value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        short s => s,
        _ => null,
    };
}

/// <summary>
/// Stops a slow regular expression from using a query's whole time limit: each event gets a short
/// allowance, and a pattern that keeps running out fails the query.
/// </summary>
internal sealed class RegexTimeoutTracker
{
    private readonly string _pattern;
    private readonly int _maxTimeouts;
    private int _timeouts;

    public RegexTimeoutTracker(string pattern, BqlLimits limits)
    {
        _pattern = pattern;
        _maxTimeouts = limits.MaxRegexTimeouts;
    }

    public void Record()
    {
        if (Interlocked.Increment(ref _timeouts) >= _maxTimeouts)
        {
            throw new BqlLimitExceededException(
                $"The regular expression '{_pattern}' is too slow: it ran out of time on {_maxTimeouts} rows.");
        }
    }
}
