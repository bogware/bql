using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests;

/// <summary>
/// Log events in memory, read as Cabinet's in-memory storage read them: time bounds, words,
/// host, sourcetype and minimum severity applied by the source; everything else by the pipeline.
/// </summary>
public sealed class InMemoryStorage : IBqlCountingSource
{
    private readonly ConcurrentDictionary<string, LogEvent> _events = new();

    /// <summary>
    /// Storage that applies bounds, words and some predicates itself, or with
    /// <paramref name="pushdown"/> false, storage that does nothing but hand over every row.
    /// </summary>
    public InMemoryStorage(bool pushdown = true)
    {
        _pushdown = pushdown;
        Capabilities = pushdown ? Exact : BqlSourceCapabilities.None;
    }

    private readonly bool _pushdown;

    public BqlSchema Schema => LogSchema.Instance;

    public BqlSourceCapabilities Capabilities { get; }

    /// <summary>What this storage applies itself, as Cabinet's storage did.</summary>
    public static readonly BqlSourceCapabilities Exact = new()
    {
        TimeBounds = true,
        TextSearch = true,
        TimeOrder = true,
        ExactPredicates = p => p.Field.ToLowerInvariant() switch
        {
            "host" or "sourcetype" or "source_type" => p.Operator == "=" && p.Value is string,
            "severity" => p.Operator is ">=" or ">" && p.Value is int or long or double,
            _ => false,
        },
    };

    /// <summary>Rows handed to the engine so far.</summary>
    public long RowsRead;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // Writes are idempotent by ID, as Cabinet's were: a retried batch stores nothing new.
    public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) =>
        Task.FromResult(_events.TryAdd(logEvent.Id, logEvent));

    public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var evt in events)
        {
            if (_events.TryAdd(evt.Id, evt))
            {
                count++;
            }
        }

        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = Filter(pushdown);
        var descending = pushdown.Sort?.Descending ?? true;
        query = descending
            ? query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id, StringComparer.Ordinal)
            : query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id, StringComparer.Ordinal);

        await Task.CompletedTask;
        foreach (var evt in query)
        {
            // As Cabinet's in-memory storage did: a cancelled read ends rather than throws
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            Interlocked.Increment(ref RowsRead);
            yield return LogSchema.ToRow(evt);
        }
    }

    public ValueTask<long> CountAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult((long)Filter(pushdown).Count());

    private IEnumerable<LogEvent> Filter(BqlPushdown pushdown)
    {
        var query = _events.Values.ToList().AsEnumerable();
        if (!_pushdown)
        {
            return query;
        }

        if (pushdown.TextTerms is { Count: > 0 } terms)
        {
            query = query.Where(e => SearchTerms.Matches(e.RawLine, terms));
        }

        if (pushdown.AllowedDatasets is { } allowed)
        {
            query = query.Where(e => allowed.Any(pattern => Matches(pattern, e.Index)));
        }

        if (pushdown.Datasets is { } datasets)
        {
            query = query.Where(e => datasets.Any(d => string.Equals(d, e.Index, StringComparison.OrdinalIgnoreCase)));
        }

        if (pushdown.StartTime is { } start)
        {
            query = query.Where(e => e.Timestamp >= start);
        }

        if (pushdown.EndTime is { } end)
        {
            query = query.Where(e => e.Timestamp <= end);
        }

        foreach (var predicate in pushdown.Predicates)
        {
            query = predicate.Field.ToLowerInvariant() switch
            {
                "host" => query.Where(e => string.Equals(e.Host, (string)predicate.Value!, StringComparison.OrdinalIgnoreCase)),
                "sourcetype" or "source_type" => query.Where(e => string.Equals(e.SourceType, (string)predicate.Value!, StringComparison.OrdinalIgnoreCase)),
                "severity" when predicate.Operator == ">=" => query.Where(e => (int)e.Severity >= Convert.ToDouble(predicate.Value)),
                "severity" => query.Where(e => (int)e.Severity > Convert.ToDouble(predicate.Value)),
                _ => throw new InvalidOperationException($"Not pushed: {predicate}"),
            };
        }

        return query;
    }

    private static bool Matches(string pattern, string index) =>
        pattern.EndsWith('*')
            ? index.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, index, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Lookups and macros in memory.</summary>
public sealed class InMemoryKnowledgeStorage : IBqlKnowledgeProvider
{
    private readonly ConcurrentDictionary<string, BqlLookupTable> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BqlMacro> _macros = new(StringComparer.OrdinalIgnoreCase);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task SaveLookupAsync(LookupTable lookup)
    {
        _lookups[lookup.Name] = new BqlLookupTable { Name = lookup.Name, Columns = lookup.Columns, Rows = lookup.Rows };
        return Task.CompletedTask;
    }

    public Task SaveMacroAsync(QueryMacro macro)
    {
        _macros[macro.Name] = new BqlMacro { Name = macro.Name, Definition = macro.Definition, Parameters = macro.Parameters };
        return Task.CompletedTask;
    }

    /// <summary>The caller each lookup and macro was asked for, most recent last.</summary>
    public ConcurrentQueue<BqlCaller> Callers { get; } = new();

    public Task<BqlLookupTable?> GetLookupAsync(string name, BqlCaller caller, CancellationToken cancellationToken = default)
    {
        Callers.Enqueue(caller);
        return Task.FromResult(_lookups.TryGetValue(name, out var lookup) ? lookup : null);
    }

    public Task<BqlMacro?> GetMacroAsync(string name, BqlCaller caller, CancellationToken cancellationToken = default)
    {
        Callers.Enqueue(caller);
        return Task.FromResult(_macros.TryGetValue(name, out var macro) ? macro : null);
    }
}

/// <summary>Cabinet's lookup table shape, as the ported tests write them.</summary>
public sealed class LookupTable
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public List<string> Columns { get; set; } = new();

    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

/// <summary>Cabinet's macro shape, as the ported tests write them.</summary>
public sealed class QueryMacro
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Definition { get; set; }

    public List<string> Parameters { get; set; } = new();
}
