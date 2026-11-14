using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Bogware.Bql;

/// <summary>
/// Runs BQL queries over one <see cref="IBqlSource"/>. It parses the query, gives the source
/// what the source can do, and runs the rest as a pipeline that streams rows a batch at a time.
/// One engine can run any number of queries at once.
/// </summary>
public sealed class BqlQueryEngine
{
    /// <summary>Name of the activity source the engine traces with.</summary>
    public const string ActivitySourceName = "Bogware.Bql";

    private static readonly ActivitySource Telemetry = new(ActivitySourceName);

    private readonly IBqlSource _source;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly BqlEngineOptions _options;
    private readonly Dictionary<string, IBqlDialect> _dialects;

    /// <summary>An engine over a source, with options or the defaults.</summary>
    public BqlQueryEngine(IBqlSource source, BqlEngineOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new BqlEngineOptions();
        _parser = new BqlParser(source.Schema, _options.Parser);
        _optimizer = new BqlOptimizer(source.Schema, source.Capabilities, _parser);
        _dialects = new Dictionary<string, IBqlDialect>(StringComparer.OrdinalIgnoreCase);
        foreach (var dialect in _options.Dialects)
        {
            _dialects[dialect.Name] = dialect;
        }
    }

    /// <summary>The source queries read.</summary>
    public IBqlSource Source => _source;

    /// <summary>The source's schema.</summary>
    public BqlSchema Schema => _source.Schema;

    /// <summary>The options this engine runs with.</summary>
    public BqlEngineOptions Options => _options;

    /// <summary>The parser this engine uses, which knows the source's schema.</summary>
    public BqlParser Parser => _parser;

    /// <summary>The names of the languages this engine reads: <c>bql</c>, then each dialect.</summary>
    public IReadOnlyList<string> Dialects => ["bql", .. _dialects.Keys];

    internal BqlLimits Limits => _options.Limits;

    /// <summary>
    /// Parses query text. <paramref name="dialect"/> null or <c>bql</c> reads BQL and SPL; any
    /// other name must be a registered dialect.
    /// </summary>
    public BqlQuery Parse(string text, string? dialect = null)
    {
        try
        {
            return ParseCore(text, dialect);
        }
        catch (Exception ex) when (ex is not BqlParseException && !BqlFailures.PassesThrough(ex))
        {
            // A bug or a case the parser didn't foresee: say so plainly, keep the details for the host.
            Report(BqlDiagnosticLevel.Error, "The parser failed on a query", ex);
            throw new BqlParseException("The query could not be read.", ex);
        }
    }

    private BqlQuery ParseCore(string text, string? dialect)
    {
        if (string.IsNullOrEmpty(dialect) || dialect.Equals("bql", StringComparison.OrdinalIgnoreCase) || dialect.Equals("spl", StringComparison.OrdinalIgnoreCase))
        {
            return _parser.ParseBql(text);
        }

        if (!_dialects.TryGetValue(dialect, out var translator))
        {
            throw new BqlParseException($"This engine doesn't read '{dialect}'. It reads: {string.Join(", ", Dialects)}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new BqlParseException("Query cannot be empty");
        }

        if (text.Length > _options.Parser.MaxQueryLength)
        {
            throw new BqlParseException($"Query exceeds maximum length of {_options.Parser.MaxQueryLength} characters");
        }

        if (translator.TryTranslate(text, new BqlDialectContext(Schema, _parser), out var query, out var diagnostics) && query != null)
        {
            return query;
        }

        throw new BqlParseException(diagnostics.Count > 0
            ? $"The {translator.Name} query doesn't translate: {string.Join("; ", diagnostics)}"
            : $"The {translator.Name} query doesn't translate");
    }

    /// <summary>Whether query text parses, and why not when it doesn't.</summary>
    public BqlValidation Validate(string text, string? dialect = null)
    {
        try
        {
            var query = Parse(text, dialect);
            return new BqlValidation(true, null, query.Stages.Select(stage => stage.StageName).ToList());
        }
        catch (BqlParseException ex)
        {
            return new BqlValidation(false, ex.Message, []);
        }
    }

    /// <summary>
    /// How a query would run, without running it: what the source is asked for and which stages
    /// are left to the pipeline.
    /// </summary>
    public BqlExplanation Explain(BqlExecutionContext context)
    {
        var query = Parse(context.Query, context.Dialect);
        var optimized = _optimizer.Optimize(query, context);
        return new BqlExplanation(
            optimized.Pushdown,
            optimized.RemainingStages.Select(stage => stage.StageName).ToList(),
            IsBareCount(optimized.RemainingStages, out _) && _source is IBqlCountingSource,
            _source is IBqlAggregatingSource && optimized.RemainingStages.Count > 0
                && optimized.RemainingStages[0] is StatsStage or TimechartStage or TopStage { ByField: null } or RareStage);
    }

    /// <summary>Runs a query and returns its rows.</summary>
    public Task<BqlResult> ExecuteAsync(
        BqlExecutionContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteGuardedAsync(context, depth: 0, cancellationToken);

    /// <summary>
    /// Runs a subsearch for a query: for the same caller, inside the same time bounds, with what is
    /// left of the same time, one level deeper.
    /// </summary>
    internal Task<BqlResult> ExecuteSubsearchAsync(string query, BqlBudget parent, CancellationToken cancellationToken)
    {
        var depth = parent.Depth + 1;
        if (depth > _options.MaxNestingDepth)
        {
            throw new BqlLimitExceededException(
                $"Subsearches and macros nest more than {_options.MaxNestingDepth} deep; one probably refers to itself.");
        }

        var left = parent.Deadline == DateTime.MaxValue ? (TimeSpan?)null : parent.Deadline - DateTime.UtcNow;
        var context = new BqlExecutionContext
        {
            Query = query,
            Limit = _options.SubsearchResultLimit,
            AllowedDatasets = parent.Caller.AllowedDatasets,
            Dataset = parent.Caller.Dataset,
            StartTime = parent.StartTime,
            EndTime = parent.EndTime,
            Timeout = left is { } remaining ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1)) : null,
        };
        return ExecuteGuardedAsync(context, depth, cancellationToken);
    }

    private async Task<BqlResult> ExecuteGuardedAsync(BqlExecutionContext context, int depth, CancellationToken cancellationToken)
    {
        using var scope = BqlScope.Enter(_options.Limits);
        try
        {
            return await ExecuteCoreAsync(context, depth, cancellationToken);
        }
        catch (Exception ex) when (ex is not BqlParseException && !BqlFailures.PassesThrough(ex))
        {
            // Never a raw .NET exception or a stack trace in front of the person who typed the query.
            Report(BqlDiagnosticLevel.Error, "A query failed on an unexpected error", ex);
            throw new BqlExecutionException("The query stopped on an internal error.", ex);
        }
    }

    private async Task<BqlResult> ExecuteCoreAsync(BqlExecutionContext context, int depth, CancellationToken cancellationToken)
    {
        using var span = Telemetry.StartActivity("bql.execute");

        // Enforce the per-query timeout by linking to the caller's cancellation token
        var timeout = context.Timeout ?? _options.DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        cancellationToken = timeoutCts.Token;
        var deadline = timeout < TimeSpan.Zero || timeout >= DateTime.MaxValue - DateTime.UtcNow ? DateTime.MaxValue : DateTime.UtcNow + timeout;

        var sw = Stopwatch.StartNew();
        var resultLimit = context.Limit ?? _options.DefaultResultLimit;

        var bqlQuery = Parse(context.Query, context.Dialect);
        var optimized = _optimizer.Optimize(bqlQuery, context);

        Report(BqlDiagnosticLevel.Debug, $"Query optimized: {optimized.Pushdown.Predicates.Count} predicates pushed to the source, {optimized.RemainingStages.Count} pipeline stages remaining");

        // A bare count needs no rows
        if (_source is IBqlCountingSource counting && IsBareCount(optimized.RemainingStages, out var countColumn))
        {
            var count = await counting.CountAsync(optimized.Pushdown, cancellationToken);
            var countRow = Schema.NewRow();
            countRow[countColumn] = count;
            sw.Stop();
            return new BqlResult
            {
                Events = new List<dynamic> { countRow },
                Columns = [countColumn],
                Statistics = new BqlStatistics
                {
                    EventsScanned = count,
                    EventsMatched = 1,
                    ExecutionTime = sw.Elapsed
                }
            };
        }

        var budget = new BqlBudget(_options.Limits, Schema)
        {
            Caller = new BqlCaller(context.AllowedDatasets, context.Dataset),
            StartTime = context.StartTime,
            EndTime = context.EndTime,
            Deadline = deadline,
            Depth = depth,
        };

        // A source that can aggregate itself saves reading every row
        if (await TryAggregateInSourceAsync(optimized, resultLimit, budget, sw, span, cancellationToken) is { } aggregated)
        {
            return aggregated;
        }

        var plan = CreatePlan(optimized.RemainingStages, resultLimit, budget);

        // Read only until the first stateful stage has what it needs. It holds what it computes,
        // not the rows, so the number of rows read isn't limited by memory.
        var eventsScanned = await FeedAsync(plan, optimized.Pushdown, budget, cancellationToken);

        // The stages left work on that stage's output, which is bounded
        var result = plan.Sink.Complete();
        foreach (var op in plan.RemainingOperators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await op.ExecuteAsync(result, cancellationToken);
        }

        var finalEvents = Cancellable(result, cancellationToken).Take(resultLimit).ToList();

        var columns = finalEvents.Count > 0
            ? ((IDictionary<string, object?>)finalEvents[0]).Keys.ToList()
            : null;

        sw.Stop();

        span?.SetTag("bql.events_scanned", eventsScanned);
        span?.SetTag("bql.rows", finalEvents.Count);
        span?.SetTag("bql.truncated", budget.Truncated);

        return new BqlResult
        {
            Events = finalEvents,
            Columns = columns,
            Statistics = new BqlStatistics
            {
                EventsScanned = eventsScanned,
                EventsMatched = finalEvents.Count,
                ExecutionTime = sw.Elapsed,
                Truncated = budget.Truncated,
                Approximate = budget.Approximate
            }
        };
    }

    /// <summary>
    /// Runs a leading <c>stats</c>, <c>timechart</c>, <c>top</c> or <c>rare</c> in the source, if
    /// the source can give the same answer. Returns null to run the query here instead.
    /// </summary>
    private async Task<BqlResult?> TryAggregateInSourceAsync(
        OptimizedQuery optimized,
        int resultLimit,
        BqlBudget budget,
        Stopwatch sw,
        Activity? span,
        CancellationToken cancellationToken)
    {
        if (_source is not IBqlAggregatingSource source || optimized.RemainingStages.Count == 0)
        {
            return null;
        }

        var request = optimized.RemainingStages[0] switch
        {
            StatsStage stats => BuildAggregateRequest(optimized.Pushdown, stats.Aggregations, stats.GroupByFields, null),
            TimechartStage timechart => BuildAggregateRequest(
                optimized.Pushdown,
                [new AggregateFunction(timechart.Aggregation, timechart.AggregationField)],
                timechart.ByField == null ? [] : [timechart.ByField],
                timechart.Span),

            // top and rare count each value as text, so only a source that writes values as .NET does can
            TopStage { ByField: null } top => CountValuesRequest(optimized.Pushdown, top.Field),
            RareStage rare => CountValuesRequest(optimized.Pushdown, rare.Field),
            _ => null,
        };

        if (request == null)
        {
            return null;
        }

        var aggregate = await source.AggregateAsync(request, cancellationToken);
        if (aggregate == null)
        {
            return null;
        }

        // As in the pipeline, stats by leaves out rows without a by-field
        var rows = optimized.RemainingStages[0] is StatsStage { GroupByFields.Count: > 0 } grouped
            ? aggregate.Rows.Where(values => grouped.GroupByFields.All(f => values.TryGetValue(f, out var v) && v != null)).ToList()
            : aggregate.Rows;
        budget.CheckGroupCount(rows.Count);
        IEnumerable<dynamic> result = optimized.RemainingStages[0] switch
        {
            TopStage top => new LegacyTopOperator(top.Count, top.Field) { Budget = budget }.FromCounts(ValueCounts(rows, top.Field)),
            RareStage rare => new LegacyRareOperator(rare.Count, rare.Field) { Budget = budget }.FromCounts(ValueCounts(rows, rare.Field)),
            _ => rows.Select(values =>
            {
                var row = Schema.NewRow();
                foreach (var (key, value) in values)
                {
                    row[key] = value;
                }

                return (dynamic)row;
            }).ToList(),
        };

        foreach (var stage in optimized.RemainingStages.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConvertStageToOperator(stage, budget) is { } op)
            {
                result = await op.ExecuteAsync(result, cancellationToken);
            }
        }

        var finalEvents = Cancellable(result, cancellationToken).Take(resultLimit).ToList();
        sw.Stop();
        span?.SetTag("bql.pushed_down", true);
        span?.SetTag("bql.events_scanned", aggregate.EventsScanned);

        return new BqlResult
        {
            Events = finalEvents,
            Columns = finalEvents.Count > 0 ? ((IDictionary<string, object?>)finalEvents[0]).Keys.ToList() : null,
            Statistics = new BqlStatistics
            {
                EventsScanned = aggregate.EventsScanned,
                EventsMatched = finalEvents.Count,
                ExecutionTime = sw.Elapsed,
                Truncated = budget.Truncated,
                Approximate = budget.Approximate || aggregate.Approximate,
                PushedDown = true,
            },
        };
    }

    /// <summary>A count of each value of a field, grouped as top and rare group them.</summary>
    private BqlAggregateRequest CountValuesRequest(BqlPushdown filter, string field)
    {
        var unbounded = filter.Clone();
        unbounded.Limit = null;
        return new BqlAggregateRequest
        {
            Filter = unbounded,
            GroupBy = [field],
            Aggregates = [new BqlAggregateSpec(BqlAggregateKind.Count, null, 0, "count")],
            MaxGroups = _options.Limits.MaxGroups,
            ExactTextKeys = true,
        };
    }

    private static IEnumerable<KeyValuePair<string, long>> ValueCounts(IEnumerable<Dictionary<string, object?>> rows, string field) =>
        rows.Where(row => row.TryGetValue(field, out var value) && value != null)
            .Select(row => new KeyValuePair<string, long>(row[field]!.ToString() ?? string.Empty, Convert.ToInt64(row["count"], System.Globalization.CultureInfo.InvariantCulture)));

    private BqlAggregateRequest? BuildAggregateRequest(
        BqlPushdown filter,
        IReadOnlyList<AggregateFunction> functions,
        IReadOnlyList<string> groupBy,
        TimeSpan? bucket)
    {
        var specs = new List<BqlAggregateSpec>();
        foreach (var function in functions)
        {
            if (ToAggregateSpec(function) is not { } spec)
            {
                return null;
            }

            specs.Add(spec);
        }

        var unbounded = filter.Clone();
        unbounded.Limit = null;
        return new BqlAggregateRequest
        {
            Filter = unbounded,
            GroupBy = groupBy,
            Bucket = bucket,
            Aggregates = specs,
            MaxGroups = _options.Limits.MaxGroups,
        };
    }

    /// <summary>The source form of an aggregate, or null for one a source isn't asked to do.</summary>
    internal static BqlAggregateSpec? ToAggregateSpec(AggregateFunction function)
    {
        if (function.Expression != null)
        {
            return null;
        }

        var name = function.Name.ToLowerInvariant();
        double percentile = 0;
        BqlAggregateKind? kind = name switch
        {
            "count" => BqlAggregateKind.Count,
            "dc" or "distinct_count" => BqlAggregateKind.DistinctCount,
            "sum" => BqlAggregateKind.Sum,
            "avg" or "mean" => BqlAggregateKind.Average,
            "min" => BqlAggregateKind.Min,
            "max" => BqlAggregateKind.Max,
            "stdev" => BqlAggregateKind.SampleDeviation,
            "stdevp" => BqlAggregateKind.PopulationDeviation,
            "variance" => BqlAggregateKind.SampleVariance,
            "varp" => BqlAggregateKind.PopulationVariance,
            _ => null,
        };

        if (name == "median")
        {
            (kind, percentile) = (BqlAggregateKind.Percentile, AggregateFunction.MedianPercentile);
        }
        else if (name == "percentile")
        {
            (kind, percentile) = (BqlAggregateKind.Percentile, AggregateFunction.DefaultPercentile);
        }
        else if ((name.StartsWith("perc", StringComparison.Ordinal) && double.TryParse(name[4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var perc))
            || (name.Length > 1 && name[0] == 'p' && char.IsDigit(name[1]) && double.TryParse(name[1..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out perc)))
        {
            (kind, percentile) = (BqlAggregateKind.Percentile, Math.Clamp(perc, 0, 100));
        }

        if (kind == null || (kind != BqlAggregateKind.Count && function.Field == null))
        {
            return null;
        }

        return new BqlAggregateSpec(kind.Value, function.Field, percentile, function.OutputName);
    }

    /// <summary>
    /// Splits a pipeline into the stages that work row by row, the first stage that holds state,
    /// and whatever follows it.
    /// </summary>
    private PipelinePlan CreatePlan(List<BqlStage> stages, int resultLimit, BqlBudget budget)
    {
        var rowOperators = new List<BqlOperator>();
        var index = 0;
        var readsEvents = stages.Count == 0 || stages[0] is not (MakeResultsStage or InputLookupStage or MultisearchStage);

        if (readsEvents)
        {
            while (index < stages.Count && IsRowStage(stages[index]))
            {
                rowOperators.Add(ConvertStageToOperator(stages[index], budget)!);
                index++;
            }
        }

        IRowSink sink;
        var stage = index < stages.Count ? stages[index] : null;
        var following = index + 1 < stages.Count ? stages[index + 1] : null;
        var maxRows = _options.Limits.MaxRowsInMemory;

        switch (stage)
        {
            case null:
                sink = new TakeSink(resultLimit);
                break;
            case LimitStage limit when limit.Count <= maxRows:
                sink = new TakeSink(limit.Count);
                index++;
                break;
            case TailStage tail when tail.Count <= maxRows:
                sink = new TailSink(tail.Count);
                index++;
                break;
            case SortStage sort when following is LimitStage head && head.Count <= maxRows:
                sink = new TopRowsSink(new LegacySortOperator(sort.Fields) { Budget = budget }, head.Count);
                index += 2;
                break;
            case SortStage sort when following == null && resultLimit <= maxRows:
                sink = new TopRowsSink(new LegacySortOperator(sort.Fields) { Budget = budget }, resultLimit);
                index++;
                break;
            case StatsStage stats:
                sink = new StatsSink(stats.Aggregations, stats.GroupByFields, budget);
                index++;
                break;
            case TimechartStage timechart:
                sink = new LegacyTimechartOperator(timechart.Span, timechart.Aggregation, timechart.ByField, timechart.AggregationField).CreateSink(budget);
                index++;
                break;
            case ChartStage chart:
                sink = new LegacyChartOperator(chart.Aggregations, chart.OverField, chart.ByField).CreateSink(budget);
                index++;
                break;
            case TopStage top:
                sink = new LegacyTopOperator(top.Count, top.Field, top.ByField).CreateSink(budget);
                index++;
                break;
            case RareStage rare:
                sink = new LegacyRareOperator(rare.Count, rare.Field).CreateSink(budget);
                index++;
                break;
            default:
                sink = new MaterializeSink(budget);
                break;
        }

        var remaining = stages
            .Skip(index)
            .Select(remainingStage => ConvertStageToOperator(remainingStage, budget))
            .Where(op => op != null)
            .Select(op => op!)
            .ToList();

        return new PipelinePlan(readsEvents, rowOperators, sink, remaining);
    }

    /// <summary>Stages that handle each row on its own, so they can run a batch at a time.</summary>
    private static bool IsRowStage(BqlStage stage) => stage is
        SearchStage or WhereStage or EvalStage or RexStage or RenameStage or RegexStage or IplocationStage
        or FieldAliasStage or FillNullStage or BucketStage or TableStage or BoundsStage;

    private async Task<long> FeedAsync(
        PipelinePlan plan,
        BqlPushdown pushdown,
        BqlBudget budget,
        CancellationToken cancellationToken)
    {
        if (!plan.ReadsEvents)
        {
            return 0;
        }

        var scanned = 0L;
        await foreach (var batch in ReadBatchesAsync(pushdown, plan, cancellationToken).WithCancellation(cancellationToken))
        {
            scanned += batch.Count;

            IEnumerable<dynamic> rows = batch;
            foreach (var op in plan.RowOperators)
            {
                rows = await op.ExecuteAsync(rows, cancellationToken);
            }

            foreach (object row in Cancellable(rows, cancellationToken))
            {
                plan.Sink.Add(row);
                if (plan.Sink.IsDone)
                {
                    return scanned;
                }
            }

            if (_options.Limits.MaxEventsScanned > 0 && scanned >= _options.Limits.MaxEventsScanned)
            {
                budget.Truncated = true;
                return scanned;
            }
        }

        return scanned;
    }

    /// <summary>
    /// Reads the source's rows in batches. With filters ahead of a sink that needs few rows,
    /// batches start near that many and grow, so <c>search ... | head 100</c> doesn't wait for
    /// 10,000 matches to keep 100.
    /// </summary>
    private async IAsyncEnumerable<List<dynamic>> ReadBatchesAsync(
        BqlPushdown template,
        PipelinePlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxBatch = _options.BatchSize;
        var pushdown = template.Clone();

        // Without filters ahead of it, a sink needing few rows needs only that many.
        pushdown.Limit = plan.RowOperators.Count == 0 ? plan.Sink.Remaining : null;

        var batchSize = pushdown.Limit is { } wanted
            ? Math.Clamp(wanted, 1, maxBatch)
            : plan.Sink.Remaining is { } needed ? Math.Clamp(needed, Math.Min(_options.MinimumReadHint, maxBatch), maxBatch) : maxBatch;

        var batch = new List<dynamic>(batchSize);
        await foreach (var row in _source.ReadAsync(pushdown, cancellationToken).WithCancellation(cancellationToken))
        {
            batch.Add(row);
            if (batch.Count >= batchSize)
            {
                yield return batch;
                batchSize = (int)Math.Min(maxBatch, (long)batchSize * _options.BatchGrowthFactor);
                batch = new List<dynamic>(batchSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Stages evaluate lazily, so this is where a slow one notices the query's time is up.
    /// </summary>
    private static IEnumerable<dynamic> Cancellable(IEnumerable<dynamic> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private sealed record PipelinePlan(
        bool ReadsEvents,
        List<BqlOperator> RowOperators,
        IRowSink Sink,
        List<BqlOperator> RemainingOperators);

    /// <summary>
    /// Streams a query's rows. When only row-by-row stages remain, rows arrive as the source
    /// yields them, without the result being held in memory. When a stage holds state (stats,
    /// sort, eventstats), the rows exist only once it has seen its whole input.
    /// </summary>
    public async IAsyncEnumerable<dynamic> ExecuteStreamAsync(
        BqlExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Async iterators lose ambient state between rows, so the query's limits are entered
        // again around each step, and failures are turned into BQL errors here.
        await using var rows = StreamCoreAsync(context, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool more;
            using (BqlScope.Enter(_options.Limits))
            {
                try
                {
                    more = await rows.MoveNextAsync();
                }
                catch (Exception ex) when (ex is not BqlParseException && !BqlFailures.PassesThrough(ex))
                {
                    Report(BqlDiagnosticLevel.Error, "A streamed query failed on an unexpected error", ex);
                    throw new BqlExecutionException("The query stopped on an internal error.", ex);
                }
            }

            if (!more)
            {
                yield break;
            }

            yield return rows.Current;
        }
    }

    private async IAsyncEnumerable<dynamic> StreamCoreAsync(
        BqlExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(context.Timeout ?? _options.DefaultTimeout);
        var token = timeoutCts.Token;
        var resultLimit = context.Limit ?? _options.DefaultResultLimit;

        var bqlQuery = Parse(context.Query, context.Dialect);
        var optimized = _optimizer.Optimize(bqlQuery, context);

        if (optimized.RemainingStages.All(IsRowStage))
        {
            // Only row stages remain, so nothing has to be held in memory
            var count = 0;
            var budget = new BqlBudget(_options.Limits, Schema)
            {
                Caller = new BqlCaller(context.AllowedDatasets, context.Dataset),
                StartTime = context.StartTime,
                EndTime = context.EndTime,
            };
            var plan = CreatePlan(optimized.RemainingStages, resultLimit, budget);
            await foreach (var batch in ReadBatchesAsync(optimized.Pushdown, plan with { Sink = new MaterializeSink(budget) }, token))
            {
                IEnumerable<dynamic> rows = batch;
                foreach (var op in plan.RowOperators)
                {
                    rows = await op.ExecuteAsync(rows, token);
                }

                foreach (var row in Cancellable(rows, token))
                {
                    yield return row;
                    if (++count >= resultLimit)
                    {
                        yield break;
                    }
                }
            }
        }
        else
        {
            var result = await ExecuteAsync(context, token);
            foreach (var evt in result.Events)
            {
                token.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }

    private static bool IsBareCount(List<BqlStage> stages, out string column)
    {
        column = string.Empty;
        if (stages is not [StatsStage { GroupByFields.Count: 0, Aggregations: [{ Name: "count", Field: null } aggregate] }])
        {
            return false;
        }

        column = aggregate.OutputName;
        return true;
    }

    internal void Report(BqlDiagnosticLevel level, string message, Exception? exception = null) =>
        _options.Diagnostics?.Invoke(new BqlDiagnostic(level, message, exception));

    internal BqlOperator? ConvertStageToOperator(BqlStage stage, BqlBudget budget)
    {
        var op = CreateOperator(stage, budget);
        if (op != null)
        {
            op.Budget = budget;
        }

        return op;
    }

    private BqlOperator? CreateOperator(BqlStage stage, BqlBudget budget) => stage switch
    {
        BoundsStage b => new BoundsOperator(b),
        SearchStage s => new LegacySearchOperator(s.SearchText, _options.Parser.MaxSearchDepth),
        WhereStage w => new LegacyWhereOperator(w.Condition),
        StatsStage st => new LegacyStatsOperator(st.Aggregations, st.GroupByFields),
        SortStage so => new LegacySortOperator(so.Fields),
        LimitStage l => new LegacyLimitOperator(l.Count),
        TailStage t => new LegacyTailOperator(t.Count),
        DedupStage d => new LegacyDedupOperator(d.Fields),
        TopStage tp => new LegacyTopOperator(tp.Count, tp.Field, tp.ByField),
        TableStage tb => new LegacyTableOperator(tb.Fields),
        EvalStage e => new LegacyEvalOperator(e.Assignments),
        RexStage r => new LegacyRexOperator(r.Pattern, r.SourceField),
        RenameStage rn => new LegacyRenameOperator(rn.Mappings),
        TimechartStage tc => new LegacyTimechartOperator(tc.Span, tc.Aggregation, tc.ByField, tc.AggregationField),
        FieldAliasStage fa => new LegacyFieldAliasOperator(fa.Aliases),
        TransactionStage tx => new LegacyTransactionOperator(tx.Fields, tx.MaxSpan, tx.MaxEvents, tx.StartsWith, tx.EndsWith),
        EventStatsStage es => new LegacyEventStatsOperator(es.Aggregations, es.GroupByFields),
        StreamStatsStage ss => new LegacyStreamStatsOperator(ss.Aggregations, ss.GroupByFields, ss.Window),
        FillNullStage fn => new LegacyFillNullOperator(fn.DefaultValue, fn.Fields),
        BucketStage bk => new LegacyBucketOperator(bk.Field, bk.Span, bk.NumericSpan, bk.Alias),
        IplocationStage ip => new LegacyIplocationOperator(ip, _options.GeoIp),
        ClusterStage cl => new LegacyClusterOperator(cl),
        MakeResultsStage mr => new LegacyMakeResultsOperator(mr.Count),
        MvExpandStage mv => new LegacyMvExpandOperator(mv.Field, mv.Limit),
        ChartStage ch => new LegacyChartOperator(ch.Aggregations, ch.OverField, ch.ByField),
        FillDownStage fd => new LegacyFillDownOperator(fd.Fields),
        AddColTotalsStage ac => new LegacyAddColTotalsOperator(ac.LabelField, ac.Fields),
        RareStage r => new LegacyRareOperator(r.Count, r.Field),
        RegexStage rx => new LegacyRegexOperator(rx.Field, rx.Pattern, rx.Negate),
        FieldSummaryStage fs => new LegacyFieldSummaryOperator(fs.MaxValues),
        XyseriesStage xy => new LegacyXyseriesOperator(xy.RowField, xy.ColumnField, xy.ValueField),
        UntableStage ut => new LegacyUntableOperator(ut.RowField, ut.ColumnField, ut.ValueField),
        InputLookupStage il => _options.Knowledge != null
            ? new LegacyInputLookupOperator(il.LookupName, _options.Knowledge)
            : throw new BqlExecutionException("inputlookup needs lookup tables, and this host has none"),
        LookupStage lk => _options.Knowledge != null
            ? new LegacyLookupOperator(lk.LookupSource, lk.KeyField, lk.OutputFields, _options.Knowledge)
            : throw new BqlExecutionException("lookup needs lookup tables, and this host has none"),
        AppendStage a => new LegacyAppendOperator(a.SubsearchQuery, this, budget),
        JoinStage j => new LegacyJoinOperator(j.JoinField, j.JoinType, j.SubsearchQuery, this, budget),
        MacroStage m => _options.Knowledge != null
            ? new LegacyMacroOperator(m.MacroName, m.Arguments, this, _options.Knowledge)
            : throw new BqlExecutionException("macros need a macro store, and this host has none"),
        SubsearchStage ss => new LegacySubsearchOperator(ss.SubsearchQuery, this, budget),
        MultisearchStage ms => new LegacyMultisearchOperator(ms.SearchQueries, this, budget),
        _ => throw new BqlExecutionException($"The {stage.StageName} stage isn't supported here")
    };
}

/// <summary>Whether query text parses.</summary>
/// <param name="IsValid">True when it parses.</param>
/// <param name="Error">Why not, when it doesn't.</param>
/// <param name="Stages">The stage names it parsed to.</param>
public sealed record BqlValidation(bool IsValid, string? Error, IReadOnlyList<string> Stages);

/// <summary>How a query would run.</summary>
/// <param name="Pushdown">What the source is asked for.</param>
/// <param name="PipelineStages">Stages the engine runs itself, in order.</param>
/// <param name="CountedBySource">Whether the source counts the rows without reading them.</param>
/// <param name="MayAggregateInSource">Whether the source will be asked to aggregate.</param>
public sealed record BqlExplanation(BqlPushdown Pushdown, IReadOnlyList<string> PipelineStages, bool CountedBySource, bool MayAggregateInSource);

/// <summary>Keeps rows inside the bounds a source wasn't trusted to apply.</summary>
internal sealed class BoundsOperator : BqlOperator
{
    private readonly BoundsStage _stage;

    public BoundsOperator(BoundsStage stage)
    {
        _stage = stage;
    }

    public override string Name => "bounds";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<dynamic>>(((IEnumerable<object>)input).Where(row =>
        {
            var dict = (IDictionary<string, object?>)row;
            if (_stage.Start != null || _stage.End != null)
            {
                if (Schema.TimeOf(dict) is not { } time
                    || (_stage.Start is { } start && time < start)
                    || (_stage.End is { } end && time > end))
                {
                    return false;
                }
            }

            if (_stage.DatasetField is { } field && _stage.Dataset is { } dataset)
            {
                return dict.TryGetValue(field, out var value)
                    && string.Equals(value?.ToString(), dataset, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }));
}
