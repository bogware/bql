using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bogware.Bql;

// ─── Legacy operator wrappers ────────────────────────────────
// These allow the BQL parser to integrate with the existing QueryEngine

internal sealed class LegacySearchOperator : BqlOperator
{
    private readonly BqlTextSearch _search;
    public override string Name => "search";

    public LegacySearchOperator(string pattern) { _search = BqlTextSearch.Parse(pattern); }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<dynamic>>(((IEnumerable<object>)input).Where(evt => _search.Matches((IDictionary<string, object?>)evt, Schema)));
}

internal sealed class LegacyWhereOperator : BqlOperator
{
    private readonly BqlExpression _condition;
    public override string Name => "where";

    public LegacyWhereOperator(BqlExpression condition) { _condition = condition; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var rows = (IEnumerable<object>)input;
        return Task.FromResult<IEnumerable<dynamic>>(
            rows.Where(row => BqlExpressionEvaluator.EvaluateBool(_condition, (IDictionary<string, object?>)row)));
    }
}

internal sealed class LegacyStatsOperator : BqlOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    public override string Name => "stats";

    public LegacyStatsOperator(List<AggregateFunction> aggs, List<string> groupBy)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
    }

    internal List<AggregateFunction> Aggregations => _aggregations;

    internal List<string> GroupByFields => _groupByFields;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = new StatsSink(_aggregations, _groupByFields, Budget ?? BqlBudget.Unbounded());
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal static object? ComputeAggregate(AggregateFunction agg, List<dynamic> events, BqlBudget? budget = null)
    {
        var accumulator = new AggregateAccumulator(agg, budget ?? BqlBudget.Unbounded());
        foreach (var row in events)
        {
            accumulator.Add((IDictionary<string, object?>)row);
        }

        return accumulator.Result();
    }

    internal static double? GetNumericValue(IDictionary<string, object?> dict, string field)
    {
        if (!dict.TryGetValue(field, out var val) || val == null) return null;
        if (val is double d) return d;
        if (val is long l) return l;
        if (val is int i) return i;
        // Float, not Any: "1,000" and "(5)" aren't numbers, which is also what storage aggregates assume
        if (double.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }
}

internal sealed class LegacySortOperator : BqlOperator
{
    private readonly List<SortField> _fields;
    public override string Name => "sort";

    public LegacySortOperator(List<SortField> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult(Order(input));

    /// <summary>Orders rows. Rows that compare equal keep their order.</summary>
    internal IEnumerable<dynamic> Order(IEnumerable<dynamic> input)
    {
        if (_fields.Count == 0)
        {
            return input;
        }

        var first = _fields[0];
        var ordered = first.Descending
            ? input.OrderByDescending<dynamic, object?>(e => GetSortValue(e, first.Field), BqlValueComparer.Instance)
            : input.OrderBy<dynamic, object?>(e => GetSortValue(e, first.Field), BqlValueComparer.Instance);

        for (var i = 1; i < _fields.Count; i++)
        {
            var f = _fields[i];
            ordered = f.Descending
                ? ordered.ThenByDescending<dynamic, object?>(e => GetSortValue(e, f.Field), BqlValueComparer.Instance)
                : ordered.ThenBy<dynamic, object?>(e => GetSortValue(e, f.Field), BqlValueComparer.Instance);
        }

        return ordered;
    }

    private static object? GetSortValue(object evt, string field)
    {
        var dict = (IDictionary<string, object?>)evt;
        return dict.TryGetValue(field, out var v) ? v : null;
    }
}

internal sealed class LegacyLimitOperator : BqlOperator
{
    private readonly int _count;
    public override string Name => "head";

    public LegacyLimitOperator(int count) { _count = count; }

    internal int Count => _count;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult(input.Take(_count));
}

internal sealed class LegacyTailOperator : BqlOperator
{
    private readonly int _count;
    public override string Name => "tail";

    public LegacyTailOperator(int count) { _count = count; }

    internal int Count => _count;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var list = input.ToList();
        return Task.FromResult<IEnumerable<dynamic>>(list.Skip(Math.Max(0, list.Count - _count)));
    }
}

internal sealed class LegacyDedupOperator : BqlOperator
{
    private readonly List<string> _fields;
    public override string Name => "dedup";

    public LegacyDedupOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = input.Where(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var key = BqlGroupKey.For(dict, _fields);
            return seen.Add(key);
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTopOperator : BqlOperator
{
    private readonly int _count;
    private readonly string _field;
    private readonly string? _byField;
    public override string Name => "top";

    public LegacyTopOperator(int count, string field, string? byField = null)
    {
        _count = count;
        _field = field;
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? BqlBudget.Unbounded());
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(BqlBudget budget) => new ValueCountSink(_field, _byField, budget, Rank);

    /// <summary>The rows for counts computed elsewhere, as storage gives them.</summary>
    internal IEnumerable<dynamic> FromCounts(IEnumerable<KeyValuePair<string, long>> counts) => Rank(counts, null).ToList();

    private IEnumerable<dynamic> Rank(IEnumerable<KeyValuePair<string, long>> counts, string? group)
    {
        foreach (var (value, count) in counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_count))
        {
            var row = NewRow();
            if (_byField != null)
            {
                row[_byField] = group;
            }

            row[_field] = value;
            row["count"] = AggregateAccumulator.CountValue(count);
            yield return row;
        }
    }
}

internal sealed class LegacyTableOperator : BqlOperator
{
    private readonly List<string> _fields;
    public override string Name => "table";

    public LegacyTableOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var row = NewRow();
            foreach (var field in _fields)
            {
                if (dict.TryGetValue(field, out var val))
                    row[field] = val;
            }
            return (dynamic)row;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyEvalOperator : BqlOperator
{
    private readonly List<EvalAssignment> _assignments;
    public override string Name => "eval";

    public LegacyEvalOperator(List<EvalAssignment> assignments)
    {
        _assignments = assignments;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var assignment in _assignments)
            {
                dict[assignment.FieldName] = BqlExpressionEvaluator.Evaluate(assignment.Expression, dict);
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyRexOperator : BqlOperator
{
    private readonly string _pattern;
    private readonly string? _sourceField;
    private Regex? _regex;
    private RegexTimeoutTracker? _timeouts;
    public override string Name => "rex";

    public LegacyRexOperator(string pattern, string? sourceField)
    {
        _pattern = pattern;
        _sourceField = sourceField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Runs for each batch of a query, so compile once. The timeout guards against ReDoS.
        _regex ??= new Regex(_pattern, RegexOptions.Compiled, RegexTimeoutTracker.PerEvent);
        _timeouts ??= new RegexTimeoutTracker(_pattern, Budget?.Limits ?? new BqlLimits());
        var regex = _regex;
        var timeouts = _timeouts;

        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var source = _sourceField is null
                ? Schema.TextOf(dict)
                : dict.TryGetValue(_sourceField, out var v) ? v?.ToString() : null;
            if (source != null)
            {
                try
                {
                    var match = regex.Match(source);
                    if (match.Success)
                    {
                        foreach (var groupName in regex.GetGroupNames())
                        {
                            if (!int.TryParse(groupName, out _))
                                dict[groupName] = match.Groups[groupName].Value;
                        }
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    timeouts.Record();
                }
            }
            return evt;
        });
        return Task.FromResult<IEnumerable<dynamic>>(result);
    }
}

internal sealed class LegacyRenameOperator : BqlOperator
{
    private readonly List<RenameMapping> _mappings;
    public override string Name => "rename";

    public LegacyRenameOperator(List<RenameMapping> mappings)
    {
        _mappings = mappings;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var mapping in _mappings)
            {
                if (dict.TryGetValue(mapping.OldName, out var val))
                {
                    dict[mapping.NewName] = val;
                    dict.Remove(mapping.OldName);
                }
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTimechartOperator : BqlOperator
{
    private readonly TimeSpan _span;
    private readonly string? _byField;
    private readonly AggregateFunction _function;
    public override string Name => "timechart";

    public LegacyTimechartOperator(TimeSpan span, string aggregation, string? byField, string? aggField)
    {
        _span = span;
        _function = new AggregateFunction(aggregation, aggField);
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? BqlBudget.Unbounded());
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(BqlBudget budget) => new TimechartSink(_span, _function, _byField, budget);
}

internal sealed class LegacyFieldAliasOperator : BqlOperator
{
    private readonly Dictionary<string, string> _aliases;
    public override string Name => "fieldalias";

    public LegacyFieldAliasOperator(Dictionary<string, string> aliases) { _aliases = aliases; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var alias in _aliases)
            {
                if (dict.TryGetValue(alias.Value, out var val))
                    dict[alias.Key] = val;
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTransactionOperator : BqlOperator
{
    private readonly List<string> _fields;
    private readonly TimeSpan? _maxSpan;
    private readonly int? _maxEvents;
    private readonly string? _startsWith;
    private readonly string? _endsWith;
    public override string Name => "transaction";

    public LegacyTransactionOperator(List<string> fields, TimeSpan? maxSpan, int? maxEvents, string? startsWith = null, string? endsWith = null)
    {
        _startsWith = startsWith;
        _endsWith = endsWith;
        _fields = fields;
        _maxSpan = maxSpan;
        _maxEvents = maxEvents;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var results = new List<dynamic>();

        // Group events by the transaction fields
        var groups = events.GroupBy(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            return BqlGroupKey.For(dict, _fields);
        });

        foreach (var group in groups)
        {
            var orderedEvents = group
                .Select(e =>
                {
                    var dict = (IDictionary<string, object?>)e;
                    var ts = Schema.TimeOf(dict) ?? DateTime.MinValue;
                    return (Event: e, Timestamp: ts);
                })
                .OrderBy(x => x.Timestamp)
                .ToList();

            // Split into transactions based on maxSpan and maxEvents
            var txn = new List<(dynamic Event, DateTime Timestamp)>();

            foreach (var item in orderedEvents)
            {
                // With a start marker, events outside a transaction are dropped, and a new
                // start marker begins a new transaction.
                if (_startsWith != null)
                {
                    var isStart = TextContains(item.Event, _startsWith);
                    if (txn.Count == 0 && !isStart)
                        continue;
                    if (txn.Count > 0 && isStart)
                    {
                        if (_endsWith == null)
                            results.Add(BuildTransaction(txn));
                        txn.Clear();
                    }
                }

                var shouldSplit = false;

                if (txn.Count > 0)
                {
                    if (_maxSpan.HasValue && item.Timestamp - txn[0].Timestamp > _maxSpan.Value)
                        shouldSplit = true;
                    if (_maxEvents.HasValue && txn.Count >= _maxEvents.Value)
                        shouldSplit = true;
                }

                if (shouldSplit)
                {
                    // A transaction that never reached its end marker is incomplete
                    if (_endsWith == null)
                        results.Add(BuildTransaction(txn));
                    txn.Clear();
                    if (_startsWith != null && !TextContains(item.Event, _startsWith))
                        continue;
                }

                txn.Add(item);

                if (_endsWith != null && TextContains(item.Event, _endsWith))
                {
                    results.Add(BuildTransaction(txn));
                    txn.Clear();
                }
            }

            if (txn.Count > 0 && _endsWith == null)
                results.Add(BuildTransaction(txn));
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }

    private bool TextContains(dynamic evt, string text)
    {
        var dict = (IDictionary<string, object?>)evt;
        return Schema.TextOf(dict) is { } raw
            && raw?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    }

    private dynamic BuildTransaction(List<(dynamic Event, DateTime Timestamp)> txnEvents)
    {
        var row = NewRow();
        var firstDict = (IDictionary<string, object?>)txnEvents[0].Event;

        // Copy fields from first event
        foreach (var kvp in firstDict)
            row[kvp.Key] = kvp.Value;

        // Add transaction metadata
        row["_eventcount"] = txnEvents.Count;
        if (txnEvents.Count > 1)
        {
            row["_duration"] = (txnEvents[^1].Timestamp - txnEvents[0].Timestamp).TotalSeconds;
        }
        else
        {
            row["_duration"] = 0.0;
        }

        // Concatenate raw lines
        var rawLines = txnEvents
            .Select(e => Schema.TextOf((IDictionary<string, object?>)e.Event))
            .Where(l => l != null);
        row["_raw"] = string.Join("\n", rawLines);

        return row;
    }
}

/// <summary>
/// EventStats: compute aggregations per group and append results to all original events.
/// Unlike stats, eventstats is non-reducing — all original events are preserved.
/// </summary>
internal sealed class LegacyEventStatsOperator : BqlOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    public override string Name => "eventstats";

    public LegacyEventStatsOperator(List<AggregateFunction> aggs, List<string> groupBy)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();

        if (_groupByFields.Count == 0)
        {
            // Global aggregation — compute once, append to all events
            var aggValues = new Dictionary<string, object?>();
            foreach (var agg in _aggregations)
            {
                var key = agg.OutputName;
                aggValues[key] = LegacyStatsOperator.ComputeAggregate(agg, events);
            }
            foreach (var evt in events)
            {
                var dict = (IDictionary<string, object?>)evt;
                foreach (var kvp in aggValues)
                    dict[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            // Group and compute per-group, then append to each event in that group
            var groups = events.GroupBy(evt =>
            {
                var dict = (IDictionary<string, object?>)evt;
                return BqlGroupKey.For(dict, _groupByFields);
            });

            foreach (var group in groups)
            {
                var groupEvents = group.ToList();
                var aggValues = new Dictionary<string, object?>();
                foreach (var agg in _aggregations)
                {
                    var key = agg.OutputName;
                    aggValues[key] = LegacyStatsOperator.ComputeAggregate(agg, groupEvents);
                }
                foreach (var evt in groupEvents)
                {
                    var dict = (IDictionary<string, object?>)evt;
                    foreach (var kvp in aggValues)
                        dict[kvp.Key] = kvp.Value;
                }
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

/// <summary>
/// StreamStats: compute running/cumulative aggregations as events stream through.
/// Supports optional window parameter to limit the number of preceding events considered.
/// </summary>
internal sealed class LegacyStreamStatsOperator : BqlOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    private readonly int _window;
    public override string Name => "streamstats";

    public LegacyStreamStatsOperator(List<AggregateFunction> aggs, List<string> groupBy, int window)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
        _window = window;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (_window <= 0)
        {
            // Without a window, running totals: each row costs one update, not a pass over all before it
            var budget = Budget ?? BqlBudget.Unbounded();
            var running = new Dictionary<string, AggregateAccumulator[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in events)
            {
                var dict = (IDictionary<string, object?>)evt;
                var groupKey = _groupByFields.Count > 0 ? BqlGroupKey.For(dict, _groupByFields) : "";
                if (!running.TryGetValue(groupKey, out var accumulators))
                {
                    accumulators = _aggregations.Select(agg => new AggregateAccumulator(agg, budget)).ToArray();
                    running[groupKey] = accumulators;
                }

                foreach (var accumulator in accumulators)
                {
                    accumulator.Add(dict);
                }

                for (var i = 0; i < _aggregations.Count; i++)
                {
                    dict[_aggregations[i].OutputName] = accumulators[i].Result();
                }
            }

            return Task.FromResult<IEnumerable<dynamic>>(events);
        }

        // Track running windows per group (or global if no group-by)
        var groupWindows = new Dictionary<string, List<dynamic>>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var groupKey = _groupByFields.Count > 0
                ? BqlGroupKey.For(dict, _groupByFields)
                : "";

            if (!groupWindows.TryGetValue(groupKey, out var window))
            {
                window = new List<dynamic>();
                groupWindows[groupKey] = window;
            }

            window.Add(evt);

            // Keep window bounded to avoid unbounded memory growth.
            // Trim excess entries in-place — zero allocation vs original Skip().ToList().
            if (_window > 0 && window.Count > _window)
            {
                window.RemoveRange(0, window.Count - _window);
            }

            // Compute running aggregations over the (now bounded) window
            foreach (var agg in _aggregations)
            {
                var key = agg.OutputName;
                dict[key] = LegacyStatsOperator.ComputeAggregate(agg, window);
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}


// ─── Splunk parity operators ─────────────────────────────────

internal sealed class LegacyFillNullOperator : BqlOperator
{
    private readonly string _defaultValue;
    private readonly List<string> _fields;
    public override string Name => "fillnull";

    public LegacyFillNullOperator(string defaultValue, List<string> fields)
    {
        _defaultValue = defaultValue;
        _fields = fields;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            if (_fields.Count > 0)
            {
                foreach (var field in _fields)
                {
                    if (!dict.ContainsKey(field) || dict[field] == null)
                        dict[field] = _defaultValue;
                }
            }
            else
            {
                // Fill all null fields
                foreach (var key in dict.Keys.ToList())
                {
                    if (dict[key] == null)
                        dict[key] = _defaultValue;
                }
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyBucketOperator : BqlOperator
{
    private readonly string _field;
    private readonly TimeSpan? _span;
    private readonly double? _numericSpan;
    private readonly string? _alias;
    public override string Name => "bucket";

    public LegacyBucketOperator(string field, TimeSpan? span, double? numericSpan, string? alias)
    {
        _field = field;
        _span = span;
        _numericSpan = numericSpan;
        _alias = alias;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var targetField = _alias ?? _field;

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            if (!dict.TryGetValue(_field, out var val) || val == null) continue;

            if (val is DateTime dt && _span.HasValue)
            {
                // Time bucketing: floor to span interval
                var ticks = dt.Ticks - (dt.Ticks % _span.Value.Ticks);
                dict[targetField] = new DateTime(ticks, DateTimeKind.Utc);
            }
            else if (_numericSpan.HasValue)
            {
                // Numeric bucketing: floor to span interval
                if (BqlExpressionEvaluator.ToDoublePublic(val) is { } numVal)
                    dict[targetField] = Math.Floor(numVal / _numericSpan.Value) * _numericSpan.Value;
            }
            else if (_span.HasValue && BqlExpressionEvaluator.ToDoublePublic(val) is { } epochVal)
            {
                // Epoch seconds bucketing
                var spanSeconds = _span.Value.TotalSeconds;
                dict[targetField] = Math.Floor(epochVal / spanSeconds) * spanSeconds;
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyMakeResultsOperator : BqlOperator
{
    private readonly int _count;
    public override string Name => "makeresults";

    public LegacyMakeResultsOperator(int count) { _count = count; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // makeresults generates synthetic events, ignoring input
        var results = new List<dynamic>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < _count; i++)
        {
            var expando = NewRow();
            var dict = (IDictionary<string, object?>)expando;
            var schema = Schema;
            dict[schema.TimeField] = now;
            foreach (var synonym in schema.TimeFieldSynonyms)
                dict[synonym] = now;
            dict[schema.TextField] = "";
            foreach (var synonym in schema.TextFieldSynonyms)
                dict[synonym] = "";
            results.Add(expando);
        }
        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

internal sealed class LegacyMvExpandOperator : BqlOperator
{
    private readonly string _field;
    private readonly int _limit;
    public override string Name => "mvexpand";

    public LegacyMvExpandOperator(string field, int limit) { _field = field; _limit = limit; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var results = new List<dynamic>();
        foreach (var evt in input)
        {
            var count = 0;
            var dict = (IDictionary<string, object?>)evt;
            if (!dict.TryGetValue(_field, out var val) || val == null)
            {
                results.Add(evt);
                continue;
            }

            IEnumerable<object?> values;
            if (val is string[] arr) values = arr;
            else if (val is IEnumerable<object?> list) values = list;
            else if (val is string s && s.Contains(',')) values = s.Split(',').Select(v => (object?)v.Trim());
            else { results.Add(evt); continue; }

            foreach (var v in values)
            {
                if (count++ >= _limit) break;
                var newExpando = NewRow();
                var newDict = (IDictionary<string, object?>)newExpando;
                foreach (var kvp in dict) newDict[kvp.Key] = kvp.Value;
                newDict[_field] = v;
                results.Add(newExpando);
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

internal sealed class LegacyChartOperator : BqlOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly string? _overField;
    private readonly string? _byField;
    public override string Name => "chart";

    public LegacyChartOperator(List<AggregateFunction> aggs, string? overField, string? byField)
    {
        _aggregations = aggs;
        _overField = overField;
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? BqlBudget.Unbounded());
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    /// <summary>One row per <c>over</c> value. With <c>by</c>, one column per <c>by</c> value.</summary>
    internal IRowSink CreateSink(BqlBudget budget)
    {
        if (_overField != null && _byField != null)
        {
            return new ChartSink(_aggregations, _overField, _byField, budget);
        }

        var groupBy = new List<string>();
        if (_overField != null) groupBy.Add(_overField);
        if (_byField != null) groupBy.Add(_byField);
        return new StatsSink(_aggregations, groupBy, budget);
    }
}

internal sealed class LegacyFillDownOperator : BqlOperator
{
    private readonly List<string> _fields;
    public override string Name => "filldown";

    public LegacyFillDownOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var lastValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var fieldsToFill = _fields.Count > 0 ? _fields : dict.Keys.ToList();

            foreach (var field in fieldsToFill)
            {
                if (dict.TryGetValue(field, out var val) && val != null)
                {
                    lastValues[field] = val;
                }
                else if (lastValues.TryGetValue(field, out var last) && last != null)
                {
                    dict[field] = last;
                }
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyAddColTotalsOperator : BqlOperator
{
    private readonly string _labelField;
    private readonly List<string> _fields;
    public override string Name => "addcoltotals";

    public LegacyAddColTotalsOperator(string labelField, List<string> fields)
    {
        _labelField = labelField;
        _fields = fields;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (events.Count == 0) return Task.FromResult<IEnumerable<dynamic>>(events);

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var fieldsToTotal = _fields.Count > 0 ? _fields : ((IDictionary<string, object?>)events[0]).Keys.ToList();

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var field in fieldsToTotal)
            {
                if (dict.TryGetValue(field, out var val) && BqlExpressionEvaluator.ToDoublePublic(val) is { } numVal)
                {
                    totals[field] = totals.GetValueOrDefault(field) + numVal;
                }
            }
        }

        // Append totals row
        var totalRow = NewRow();
        var totalDict = (IDictionary<string, object?>)totalRow;
        totalDict[_labelField] = "TOTALS";
        foreach (var kvp in totals) totalDict[kvp.Key] = kvp.Value;
        events.Add(totalRow);

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

// ─── P1-P3 new operators ─────────────────────────────────────

/// <summary>
/// Rare: find least common values of a field (inverse of top).
/// </summary>
internal sealed class LegacyRareOperator : BqlOperator
{
    private readonly int _count;
    private readonly string _field;
    public override string Name => "rare";

    public LegacyRareOperator(int count, string field) { _count = count; _field = field; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? BqlBudget.Unbounded());
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(BqlBudget budget) => new ValueCountSink(_field, null, budget, (counts, _) => Rank(counts));

    /// <summary>The rows for counts computed elsewhere, as storage gives them.</summary>
    internal IEnumerable<dynamic> FromCounts(IEnumerable<KeyValuePair<string, long>> counts) => Rank(counts);

    private IEnumerable<dynamic> Rank(IEnumerable<KeyValuePair<string, long>> counts)
    {
        var rarest = counts
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_count)
            .ToList();

        var total = rarest.Sum(pair => pair.Value);
        return rarest.Select(pair =>
        {
            var row = NewRow();
            row[_field] = pair.Key;
            row["count"] = AggregateAccumulator.CountValue(pair.Value);
            row["percent"] = total > 0 ? Math.Round(pair.Value * 100.0 / total, 2) : 0.0;
            return (dynamic)row;
        }).ToList();
    }
}

/// <summary>
/// Regex: filter events where a field matches (or doesn't match) a regex pattern.
/// </summary>
internal sealed class LegacyRegexOperator : BqlOperator
{
    private readonly string? _field;
    private readonly string _pattern;
    private readonly bool _negate;
    private Regex? _regex;
    private RegexTimeoutTracker? _timeouts;
    public override string Name => "regex";

    public LegacyRegexOperator(string? field, string pattern, bool negate)
    {
        _field = field;
        _pattern = pattern;
        _negate = negate;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Runs for each batch of a query, so compile once.
        try
        {
            _regex ??= new Regex(_pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeoutTracker.PerEvent);
        }
        catch (ArgumentException ex)
        {
            throw new BqlParseException($"Invalid regular expression '{_pattern}': {ex.Message}");
        }

        _timeouts ??= new RegexTimeoutTracker(_pattern, Budget?.Limits ?? new BqlLimits());
        var regex = _regex;
        var timeouts = _timeouts;

        var result = input.Where(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var value = (_field is null
                ? Schema.TextOf(dict)
                : dict.TryGetValue(_field, out var v) ? v?.ToString() : null) ?? "";
            try
            {
                var match = regex.IsMatch(value);
                return _negate ? !match : match;
            }
            catch (RegexMatchTimeoutException)
            {
                timeouts.Record();
                return false;
            }
        });

        return Task.FromResult<IEnumerable<dynamic>>(result);
    }
}

/// <summary>
/// Field summary: compute statistics about each field across all events.
/// Returns one row per field with count, distinct count, numeric min/max/avg, and sample values.
/// </summary>
internal sealed class LegacyFieldSummaryOperator : BqlOperator
{
    private readonly int _maxValues;
    public override string Name => "fieldsummary";

    public LegacyFieldSummaryOperator(int maxValues) { _maxValues = maxValues; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (events.Count == 0) return Task.FromResult<IEnumerable<dynamic>>(events);

        // Collect stats per field
        var fieldStats = new Dictionary<string, FieldStatData>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var kvp in dict)
            {
                if (!fieldStats.TryGetValue(kvp.Key, out var stats))
                {
                    stats = new FieldStatData();
                    fieldStats[kvp.Key] = stats;
                }

                stats.TotalCount++;
                if (kvp.Value != null)
                {
                    stats.ValueCount++;
                    stats.IsTime |= kvp.Value is DateTime;
                    var strVal = kvp.Value.ToString() ?? "";
                    // Cap distinct value tracking to prevent memory exhaustion
                    if (stats.DistinctValues.Count < 100_000)
                        stats.DistinctValues.Add(strVal);
                    if (stats.SampleValues.Count < _maxValues && !stats.SampleValues.Contains(strVal))
                        stats.SampleValues.Add(strVal);

                    if (BqlExpressionEvaluator.ToDoublePublic(kvp.Value) is { } numVal)
                    {
                        stats.NumericCount++;
                        stats.Sum += numVal;
                        // Running variance (Welford), so values needn't be kept
                        var delta = numVal - stats.RunningMean;
                        stats.RunningMean += delta / stats.NumericCount;
                        stats.SquaredDeviations += delta * (numVal - stats.RunningMean);
                        stats.Min = Math.Min(stats.Min, numVal);
                        stats.Max = Math.Max(stats.Max, numVal);
                    }
                }
            }
        }

        var results = new List<dynamic>();
        foreach (var kvp in fieldStats.OrderBy(f => f.Key))
        {
            var row = NewRow();
            row["field"] = kvp.Key;
            row["count"] = kvp.Value.ValueCount;
            row["distinct_count"] = kvp.Value.DistinctValues.Count;
            row["type"] = kvp.Value.IsTime ? "time" : kvp.Value.ValueCount > 0 && kvp.Value.NumericCount == kvp.Value.ValueCount ? "numeric" : "string";
            row["is_exact"] = kvp.Value.DistinctValues.Count <= 1000;
            row["numeric_count"] = kvp.Value.NumericCount;
            row["min"] = kvp.Value.NumericCount > 0 ? kvp.Value.Min : (object?)null;
            row["max"] = kvp.Value.NumericCount > 0 ? kvp.Value.Max : (object?)null;
            row["mean"] = kvp.Value.NumericCount > 0 ? kvp.Value.Sum / kvp.Value.NumericCount : (object?)null;
            row["stdev"] = kvp.Value.NumericCount > 1 ? Math.Sqrt(kvp.Value.SquaredDeviations / (kvp.Value.NumericCount - 1)) : (object?)null;
            row["values"] = string.Join(", ", kvp.Value.SampleValues.Take(_maxValues));
            results.Add(row);
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }

    private sealed class FieldStatData
    {
        public int TotalCount;
        public int ValueCount;
        public int NumericCount;
        public double Sum;
        public double RunningMean;
        public double SquaredDeviations;
        public bool IsTime;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public HashSet<string> DistinctValues = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SampleValues = new();
    }
}

/// <summary>
/// XY Series: pivot data from row format into columnar format.
/// Takes (row_field, column_field, value_field) and creates one row per unique row_field value,
/// with columns named after distinct column_field values.
/// </summary>
internal sealed class LegacyXyseriesOperator : BqlOperator
{
    private readonly string _rowField;
    private readonly string _colField;
    private readonly string _valField;
    public override string Name => "xyseries";

    public LegacyXyseriesOperator(string rowField, string colField, string valField)
    {
        _rowField = rowField;
        _colField = colField;
        _valField = valField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();

        // Group by row field, then pivot column values
        var rowGroups = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var rowKey = dict.TryGetValue(_rowField, out var rv) ? rv?.ToString() ?? "" : "";
            var colKey = dict.TryGetValue(_colField, out var cv) ? cv?.ToString() ?? "" : "";
            var value = dict.TryGetValue(_valField, out var vv) ? vv : null;

            if (!rowGroups.TryGetValue(rowKey, out var row))
            {
                row = NewRow();
                row[_rowField] = rowKey;
                rowGroups[rowKey] = row;
            }
            row[colKey] = value;
        }

        return Task.FromResult<IEnumerable<dynamic>>(rowGroups.Values.Cast<dynamic>().ToList());
    }
}

/// <summary>
/// Untable: inverse of xyseries — unpivot columnar data back into rows.
/// </summary>
internal sealed class LegacyUntableOperator : BqlOperator
{
    private readonly string _rowField;
    private readonly string _colField;
    private readonly string _valField;
    public override string Name => "untable";

    public LegacyUntableOperator(string rowField, string colField, string valField)
    {
        _rowField = rowField;
        _colField = colField;
        _valField = valField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var results = new List<dynamic>();

        foreach (var evt in input)
        {
            var dict = (IDictionary<string, object?>)evt;
            var rowValue = dict.TryGetValue(_rowField, out var rv) ? rv : null;

            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, _rowField, StringComparison.OrdinalIgnoreCase)) continue;

                var row = NewRow();
                row[_rowField] = rowValue;
                row[_colField] = kvp.Key;
                row[_valField] = kvp.Value;
                results.Add(row);
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

/// <summary>
/// InputLookup: load all rows from a lookup table as events.
/// The table comes from the host's <see cref="IBqlKnowledgeProvider"/>.
/// </summary>
internal sealed class LegacyInputLookupOperator : BqlOperator
{
    private readonly string _lookupName;
    private readonly IBqlKnowledgeProvider _storage;
    public override string Name => "inputlookup";

    public LegacyInputLookupOperator(string lookupName, IBqlKnowledgeProvider storage)
    {
        _lookupName = lookupName;
        _storage = storage;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var lookup = await _storage.GetLookupAsync(_lookupName, ct);
        if (lookup == null)
            throw new BqlExecutionException($"There's no lookup table named '{_lookupName}'");

        var results = new List<dynamic>();
        foreach (var row in lookup.Rows)
        {
            var expando = NewRow();
            foreach (var kvp in row)
            {
                expando[kvp.Key] = kvp.Value;
            }
            results.Add(expando);
        }

        return results;
    }
}

/// <summary>
/// Lookup: enrich events by joining with a lookup table on a key field.
/// For each event, looks up the key field value in the lookup table and appends matching columns.
/// </summary>
internal sealed class LegacyLookupOperator : BqlOperator
{
    private readonly string _lookupName;
    private readonly string _keyField;
    private readonly List<string> _outputFields;
    private readonly IBqlKnowledgeProvider _storage;
    public override string Name => "lookup";

    public LegacyLookupOperator(string lookupName, string keyField, List<string> outputFields, IBqlKnowledgeProvider storage)
    {
        _lookupName = lookupName;
        _keyField = keyField;
        _outputFields = outputFields;
        _storage = storage;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var lookup = await _storage.GetLookupAsync(_lookupName, ct);
        if (lookup == null)
            throw new BqlExecutionException($"There's no lookup table named '{_lookupName}'");

        // Build index: key field value → lookup row (first match wins)
        var index = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in lookup.Rows)
        {
            if (row.TryGetValue(_keyField, out var keyVal) && !index.ContainsKey(keyVal))
            {
                index[keyVal] = row;
            }
        }

        // Determine which fields to output from the lookup
        var fieldsToOutput = _outputFields.Count > 0
            ? _outputFields
            : lookup.Columns.Where(c => !string.Equals(c, _keyField, StringComparison.OrdinalIgnoreCase)).ToList();

        var results = new List<dynamic>();
        foreach (var evt in input)
        {
            var dict = (IDictionary<string, object?>)evt;
            var keyValue = dict.TryGetValue(_keyField, out var kv) ? kv?.ToString() : null;

            if (keyValue != null && index.TryGetValue(keyValue, out var lookupRow))
            {
                foreach (var field in fieldsToOutput)
                {
                    if (lookupRow.TryGetValue(field, out var val))
                    {
                        dict[field] = val;
                    }
                }
            }

            results.Add(evt);
        }

        return results;
    }
}
