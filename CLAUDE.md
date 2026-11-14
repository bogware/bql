# BQL: agent guide

BQL is a query language and a streaming engine with no opinion about what rows are. It came out of Cabinet, a log product. Keep it free of logs.

## Layout

| Path | What |
|---|---|
| `src/Bogware.Bql` | Language and engine. No package references. Keep it that way. |
| `src/Bogware.Bql.Natural` | Plain-English reader. Depends only on `Bogware.Bql`. |
| `tests/Bogware.Bql.Tests/Cabinet` | Tests ported from Cabinet. They run against a log-shaped source (`Support/`) and pin behavior. |
| `tests/Bogware.Bql.Tests/Agnostic` | Tests over other shapes of row. They prove nothing depends on logs. |
| `docs/BQL-GUIDE.md` | The language. Every query in it runs in the tests. |
| `docs/EMBEDDING.md` | How a host writes a source. |

Key types: `BqlQueryEngine`, `IBqlSource`, `BqlSchema`, `BqlSourceCapabilities`, `BqlPushdown`, `BqlRow`, `BqlParser`, `BqlOptimizer`, `BqlEngineOptions`, `IBqlDialect`. Natural: `QueryUnderstander`, `NaturalReader`, `BqlVocabulary`, `FieldCatalog`.

## Invariants

1. **No field names in the engine.** No `host`, `severity`, `RawLine`, `Timestamp`, `index`. Time, text, datasets, levels and aliases come from `BqlSchema`. The only names the engine knows are SPL's defaults, `_time` and `_raw`, and they live in `BqlSchema`.
2. **No magic numbers.** Every limit, size and timeout is a property of `BqlEngineOptions`, `BqlLimits` or `BqlParserLimits` (in Natural, `QueryUnderstander` and `NaturalReader`), init-only, with a default and a doc comment. Limits belong to an engine, never to the process: no static, settable limits. A constant that is part of the language's meaning (the median is the 50th percentile, `list()` keeps 100 values as in SPL) or an internal guard (how often eager stages check for cancellation, the timeout on the engine's own fixed patterns) is a named constant with a comment saying so.
3. **Pushdown is a promise.** The optimizer removes a stage from the pipeline only when the source said, through `BqlSourceCapabilities`, that it does that work exactly. Narrowing hints (datasets, time from a `where`) never remove a stage.
4. **A source that does nothing gets the right answer.** `PlainMemoryBqlEndToEndTests` runs every end-to-end query against a source with no capabilities. Both must agree.
5. **The parser is shared.** `ParseBql` makes fresh state per call. Don't add mutable fields read across calls.
6. **The engine writes nothing.** No logging packages. Report through `BqlEngineOptions.Diagnostics` and the `Bogware.Bql` activity source.
7. **Wrong is worse than slow.** An aggregating source returns null rather than an answer that might differ. A subsearch that fails fails the query, except `append`, which keeps SPL's behavior.
8. **Queries are untrusted.** Anything that runs a query (a subsearch, a macro, a join) runs it as the caller: the same `AllowedDatasets`, `Dataset`, time range and what is left of the timeout, through `BqlQueryEngine.ExecuteSubsearchAsync`. Subsearches and macros share one nesting budget. Every value, multi-value and row set a query can grow is held to a limit. Stages that hold rows check the token every `CancellationCheckInterval` rows. `Parse`, `Validate` and `ExecuteAsync` throw only `BqlParseException` and its subclasses, or cancellation; never catch `OutOfMemoryException`. Error messages a query author sees carry no internals: inner exceptions go to `Diagnostics`.

## Commands

```
dotnet build -c Release
dotnet test
dotnet test --filter "FullyQualifiedName~Agnostic"
```

Warnings are errors. Nullable is on.

## Style

File-scoped namespaces. `sealed` by default. `Async` suffix and a `CancellationToken` last. XML docs on public members. Comments say why, not what. Docs and comments are plain: short sentences, no hype.

## Changing the language

Add the stage to the AST, the parser, the engine's operator map, the guide, and a test in `BqlLanguageTests`. If it holds state, decide whether it's a sink. If it reads time or text, read them through `Schema`.

## Commits

Conventional commits. No AI attribution lines.
