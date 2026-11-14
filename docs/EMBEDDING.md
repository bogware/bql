# Embedding BQL

This is for a host: a program that has rows and wants people to query them.

You write one class, a source. You describe your rows with a schema. You say what your source can do itself. The engine does the rest, and checks everything you didn't promise to do.

## The source

```csharp
public interface IBqlSource
{
    BqlSchema Schema { get; }
    BqlSourceCapabilities Capabilities { get; }
    IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default);
}
```

`ReadAsync` yields rows. The engine stops pulling when it has what it needs, so a source that reads lazily, in pages or from a cursor, reads no more than it must. `| head 5` reads five rows.

Make rows with `Schema.NewRow()`. That row knows your schema's aliases.

## The schema

```csharp
var schema = new BqlSchema
{
    TimeField = "modified",
    TextField = "name",
    DatasetField = "vault",
    Aliases = new Dictionary<string, string> { ["extension"] = "ext", ["bytes"] = "size" },
    Fields =
    [
        new BqlField("path", BqlFieldType.String),
        new BqlField("size", BqlFieldType.Integer),
        new BqlField("modified", BqlFieldType.Time),
        new BqlField("tags", BqlFieldType.StringList),
    ],
    DefaultSort = new BqlSortOrder("modified", Descending: true),
};
```

| Member | What it does |
|---|---|
| `TimeField` | The field time ranges, `timechart`, `earliest()` and `latest()` read. Default `_time`. |
| `TimeFieldSynonyms` | Other fields holding the same time. |
| `TextField` | The field bare words search, and `rex`, `regex` and `cluster` read by default. Default `_raw`. |
| `TextFieldSynonyms` | Other fields holding the same text. |
| `DatasetField` | A field naming a dataset, such as an index. `index=main` in a search narrows the read. |
| `Aliases` | Other names for fields. Alias to canonical name. |
| `OrdinalFields` | Fields with named levels. With a `severity` scale, `severity >= warning` compares numbers. |
| `InternalFields` | Fields a subsearch never filters by. Default: the time and text fields. |
| `Fields` | Fields rows usually have. For completion and docs; the engine doesn't require them. |
| `DefaultSort` | The order rows come in when a query doesn't sort. |

## Capabilities

Say what you do yourself. Everything is off by default. With nothing on, the engine still answers every query correctly. It just reads every row.

```csharp
public BqlSourceCapabilities Capabilities { get; } = new()
{
    TimeBounds = true,   // I keep only rows within StartTime..EndTime, inclusive
    TextSearch = true,   // I keep only rows whose text has every TextTerms word, as SearchTerms.Matches does
    TimeOrder = true,    // I return rows in Sort order when Sort is the time field
    ExactPredicates = p => p.Field is "ext" or "mime" && p.Operator == "=" && p.Value is string,
};
```

The rule is simple. If you say you do something, do it exactly as the pipeline would. The engine takes your word and removes that work from the pipeline. If you can only narrow, don't claim it; the engine will check.

`ExactPredicates` decides per predicate. A predicate is a field, an operator (`=`, `!=`, `>`, `>=`, `<`, `<=`) and a literal. Say yes only to the ones you compare exactly as BQL does: text ignoring case, numbers as numbers, a missing field never matching, a list matching when any of its values does (and `!=` when none does).

`BqlComparison.Holds(op, value, literal)` is that comparison. It runs the engine's own code, so a source that filters with it, or registers it as a function in its database to filter there, can't drift from the pipeline. `value` is the row's value (null when missing, a list for a multi-value) and `literal` is the predicate's `Value`.

```csharp
var kept = rows.Where(row => BqlComparison.Holds(p.Operator, row.GetValueOrDefault(p.Field), p.Value));
```

## The pushdown

| Member | Meaning |
|---|---|
| `StartTime`, `EndTime` | Time bounds, inclusive. Apply them if you claimed `TimeBounds`. |
| `TextTerms` | Words, lower case, as `SearchTerms.Parse` splits them. Set only if you claimed `TextSearch`. |
| `Predicates` | Conditions you agreed to apply. All must hold. |
| `Datasets` | Datasets the query names. A hint. You may ignore it. |
| `AllowedDatasets` | Datasets the caller may read. Enforce it. The engine passes it through and trusts you. |
| `Sort` | The order wanted. Honor it if you claimed `TimeOrder`. |
| `Limit` | Most rows the engine will take, when it knows. A hint. |
| `After` | An opaque cursor from the caller, for paging. |

## Doing more

Two optional interfaces let a source skip reading rows altogether.

`IBqlCountingSource.CountAsync` answers `| stats count` when every filter was pushed down.

`IBqlAggregatingSource.AggregateAsync` answers a leading `stats`, `timechart`, `top` or `rare`. Return null whenever your answer could differ from the engine's in any way. The engine computes it then. Being wrong is worse than being slow.

## Options

`BqlEngineOptions` holds every limit: rows in memory, groups, retained values, rows scanned, value sizes, batch sizes, subsearch sizes, nesting depth, parser limits, timeouts. Each has a default and a doc comment, and none can change once the options are made. Limits belong to the engine they were given to; two engines in one process can have different ones. A host shows the ones its users should see.

Lookup tables and macros come from `IBqlKnowledgeProvider`. Each call says who is asking: a `BqlCaller` with the query's `AllowedDatasets` and `Dataset`. Return null for a table or macro that caller may not use. GeoIP comes from `IBqlGeoIpLookup`. Leave them null and the stages that need them say so.

`Diagnostics` takes a callback for things worth logging. The engine writes nothing on its own. It traces with an `ActivitySource` named `Bogware.Bql`.

## Running untrusted queries

BQL is built to run queries typed by people the host doesn't trust. The engine does its part; the host does the rest.

What the engine does:

- **Subsearches run as the caller.** `append`, `join`, `multisearch`, `subsearch` and macros run with the outer query's `AllowedDatasets`, `Dataset`, time range and whatever is left of its timeout. Nested ones too.
- **Nesting stops.** Subsearches and macros share one budget, `MaxNestingDepth` (default 10). A macro that calls itself, directly or through a subsearch, fails with `BqlLimitExceededException`.
- **Memory is bounded.** Rows a stage holds, groups, retained values, the size of one value and the number of values in one field all have limits. A query that would pass one stops with `BqlLimitExceededException`, or, for row caps, returns what fits with `Statistics.Truncated` set.
- **Time is bounded.** `ExecuteAsync` honours `Timeout` (default 60 seconds) and the caller's token, inside stages that hold rows too. Each regular expression has a time per row.
- **Errors are BQL errors.** `Parse`, `Validate` and `ExecuteAsync` throw `BqlParseException` or one of its subclasses (`BqlExecutionException`, `BqlLimitExceededException`), or `OperationCanceledException` when time runs out. Their messages are safe to show the person who typed the query. Inner exceptions, which may carry paths, hosts or stack traces, go only to `Diagnostics`.

The limits that matter most, with their defaults:

| Limit | Default | What it bounds |
|---|---|---|
| `BqlEngineOptions.DefaultTimeout` | 60 s | One query, subsearches included |
| `BqlEngineOptions.MaxNestingDepth` | 10 | Subsearches and macros inside each other |
| `BqlEngineOptions.SubsearchResultLimit` | 50,000 | Rows one subsearch returns |
| `BqlLimits.MaxRowsInMemory` | 100,000 | Rows a stage holds (`sort`, `join`, `mvexpand`, `append`...) |
| `BqlLimits.MaxGroups` | 100,000 | Groups in `stats`, `eventstats`, `streamstats` |
| `BqlLimits.MaxRetainedValues` | 20,000,000 | Values held for `dc`, `values`, percentiles |
| `BqlLimits.MaxValueLength` | 1,000,000 | Characters in one value, or in one field's values together |
| `BqlLimits.MaxMultiValueCount` | 10,000 | Values in one multi-value field |
| `BqlLimits.MaxStreamStatsWindow` | 10,000 | `streamstats window=` |
| `BqlLimits.MaxMakeResults` | 50,000 | Rows `makeresults` makes |
| `BqlLimits.RegexTimeoutPerRow` | 250 ms | One pattern on one row |
| `BqlLimits.MaxRegexTimeouts` | 8 | Rows a pattern may time out on before the query stops |
| `BqlLimits.MaxEventsScanned` | none | Rows read from the source |
| `BqlParserLimits.MaxQueryLength` | 100,000 | Characters in a query |
| `QueryUnderstander.MaxInputLength` | 2,000 | Characters read as a question; longer input is parsed strictly |

What the host must do:

- **Enforce `AllowedDatasets` in your source.** The engine passes it in `BqlPushdown.AllowedDatasets` on every read, subsearches included, and trusts you to apply it. A source that ignores it lets any query read any dataset.
- **Treat names from queries as untrusted.** Lookup and macro names in `IBqlKnowledgeProvider` calls come straight from query text. Check them against the `BqlCaller` you are given, and never use one as a path or in a query of your own without escaping it.
- **Set limits for your hardware and your users.** The defaults suit one interactive user on a server. A host serving many users lowers `MaxRowsInMemory`, `MaxRetainedValues` and `DefaultTimeout`, and sets `MaxEventsScanned`.
- **Limit how many queries run at once.** The engine bounds one query, not how many a user starts.
- **Log `Diagnostics`, don't show it.** It carries the detail kept out of user-facing messages.

## Other languages

```csharp
public interface IBqlDialect
{
    string Name { get; }
    bool TryTranslate(string text, BqlDialectContext context, out BqlQuery? query, out IReadOnlyList<string> diagnostics);
}
```

Register dialects in `BqlEngineOptions.Dialects`. A caller picks one with `BqlExecutionContext.Dialect`. The easy way to write one is to produce BQL text and call `context.Parse`. A dialect that can't read its input returns false with reasons.

## Plain words

`Bogware.Bql.Natural` reads questions. Give it a vocabulary and a catalog.

```csharp
var words = BqlVocabulary.CreateNeutral();
words.FieldWords["size"] = ["size"];
words.FieldWords["type"] = ["ext", "mime"];

var understander = new QueryUnderstander(words, schema);
var catalog = new FieldCatalog(fieldNames, knownValues, words.BuiltinFields);
var understanding = understander.Understand("pdfs over 10 mb modified this week", catalog);
if (understanding.WillRun)
{
    await engine.ExecuteAsync(new BqlExecutionContext { Query = understanding.Query! });
}
```

Build the catalog from rows the caller may read. It never names anything they couldn't see.

`BqlVocabulary.Logs` reads log questions: hosts, levels, indexes, sourcetypes. `BqlVocabulary.Neutral` reads only what the words and the catalog say. Start from `CreateNeutral()` or `CreateLogs()` and add your own words.

## Threading

One engine runs many queries at once. One parser parses on many threads at once. A source is called once per query, and may be called from many queries at the same time.
