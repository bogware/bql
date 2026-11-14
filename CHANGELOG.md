# Changelog

All notable changes to this project are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-11-14

### Added

- `BqlPushdown.NeedsText`. False when the engine can show that no stage reads the text field and
  it can't reach the output, so a source may leave large text out of its rows. It is true for
  anything the engine isn't sure about.
- `BqlComparison.Holds(op, value, literal)`: the engine's comparison, for a source that applies
  predicates itself. It is the code `where` runs, so the two can't differ.
- Size literals in expressions: `where size > 10MB`. `B`, `KB`, `MB`, `GB`, `TB`, `PB` and
  `KiB` to `PiB`, counted in 1024s, any case. A size too large to hold is a parse error.
- Relative time literals in expressions: `where modified >= -7d`, `-1d@d`, `@w`, `+1h`. They are
  times from when the query is read, as `earliest=` reads them. A sign or a snap is required, so
  `x - 7` and `a-b` are arithmetic as before. Search text still reads both as text, as SPL does.
- Natural: `BqlVocabulary.Conditions`, words that stand for a BQL condition ("photos",
  "duplicates"), and `BqlVocabulary.Rankings`, words that sort by a field ("largest",
  "smallest"). Both are empty by default, so the log and neutral vocabularies read as before.
- Natural: `BqlVocabulary.CreateNeutral(timeField)`. The time field's name before a time,
  as in "modified yesterday", is read as part of the time. "this week's" and "today's" read as
  times.

## [0.1.0] - 2026-11-14

The first release. BQL comes out of Cabinet and stands on its own.

### Added

- `Bogware.Bql`: the language and the streaming engine, taken from Cabinet 3.1.1. Every stage
  and function Cabinet had is here and behaves the same way.
- `IBqlSource`, the one interface a host implements. Rows are `BqlRow` dictionaries. The source
  declares what it does itself in `BqlSourceCapabilities`: time bounds, word search, time order
  and which predicates it applies exactly. The engine does everything else.
- `BqlSchema` describes a source's rows: the time field, the text field, a dataset field,
  aliases, fields with named levels, and the default order. The engine has no field names of its own.
- `BqlPushdown`, a generic description of what the engine asks a source for. It replaces
  Cabinet's log-shaped query context.
- Optional `IBqlCountingSource` and `IBqlAggregatingSource`, for sources that count or aggregate
  without handing over rows.
- `IBqlDialect`, so a host can add query languages that read into BQL.
- `BqlQueryEngine.Validate` and `Explain`.
- `BqlEngineOptions`, `BqlLimits` and `BqlParserLimits` hold every limit, each with a default.
- `IBqlKnowledgeProvider` for lookup tables and macros, and `IBqlGeoIpLookup` for `iplocation`.
- `Bogware.Bql.Natural`: the offline plain-English reader, with the vocabulary now a value the
  host supplies. `BqlVocabulary.Logs` reads as Cabinet did. `BqlVocabulary.Neutral` knows no
  log fields.
- Tests ported from Cabinet, run against a log-shaped source, and new tests over file records
  and plain tables.

### Changed (from Cabinet's copy)

- The parser no longer takes a lock. Each parse has its own state, so one parser serves many threads.
- The engine reads a source as one stream and stops when it has enough, instead of paging with a cursor.
- Public types carry a `Bql` prefix: `BqlExecutionContext`, `BqlResult`, `BqlLimits`,
  `BqlParseException` and so on.
- Words in a search look in the schema's text field. `rex`, `regex` and `cluster` read it when no
  field is named. Time ranges and `timechart` read the schema's time field.

### Removed

- The legacy `QueryEngine`, `QueryParser` and `QueryPlan`.
- Direct file mode. A host that wants to query a file writes a source for it.
- The MaxMind reader. Hosts supply GeoIP through `IBqlGeoIpLookup`.
- Logging packages. The engine reports through a callback and an `ActivitySource`.

### Security (hardened before the public release)

- Subsearches (`append`, `join`, `multisearch`, `subsearch`) and macros now run with the outer
  query's `AllowedDatasets`, `Dataset`, time range and remaining timeout. Before, a subsearch
  ran with none of them and could read any dataset.
- Subsearches and macros share one nesting budget, `BqlEngineOptions.MaxNestingDepth`. A macro
  that called itself through a subsearch overflowed the stack and ended the process.
- Values, multi-values and the rows `mvexpand` and `join` make are held to limits. `mvrange`
  counts its steps, so a step too small to change a large number no longer loops forever.
- Stages that hold rows (`sort`, `streamstats`, `eventstats`, `transaction`, `mvexpand`, `join`,
  `fieldsummary`) stop when the query's time is up. `streamstats` and `eventstats` count against
  the group and value limits.
- `Parse`, `Validate` and `ExecuteAsync` throw only BQL errors. Numbers too large to hold, digits
  from other scripts, bad `rex` and `regex` patterns, and spans of zero are parse errors; huge
  `printf` widths, bad `memk` input and snapping before the first date give a value or null.
- The plain-English reader no longer uses patterns that backtrack on long words, and reads at
  most `MaxInputLength` characters as a question.
- Lookup and macro providers are told who is asking.
- A failed subsearch or join tells the query author it failed and nothing more. The detail goes
  to `Diagnostics`.

### Changed (before the public release)

- `IBqlKnowledgeProvider.GetLookupAsync` and `GetMacroAsync` take a `BqlCaller`.
- `BqlEngineOptions.MaxMacroDepth` is now `MaxNestingDepth` and covers subsearches.
- `BqlLimits` properties are init-only. The static `BqlRegex.PerEventTimeout` is
  gone; set `BqlLimits.RegexTimeoutPerRow` per engine.
- New limits: `BqlLimits.RegexTimeoutPerRow`, `MaxValueLength`, `MaxMultiValueCount`,
  `MaxStreamStatsWindow`, `MaxMakeResults`, `MaxPatternLength`, `MaxPadWidth`,
  `MaxFormatPrecision`, `MaxFieldSummaryValues`; `BqlParserLimits.MaxSearchDepth`,
  `MaxTransactionEvents`; `QueryUnderstander.MaxInputLength`, `NaturalReader.MaxInputLength`.
  The defaults keep earlier behavior where it was already bounded.
- `BqlTextSearch.Parse` has an overload that takes a depth limit.

- Comparisons on a multi-value field now look at each value, as SPL's search does: `=` and the
  ordering operators hold when any value satisfies them, `!=` when no value equals. An empty list
  counts as missing. Before, a list never equaled anything.
