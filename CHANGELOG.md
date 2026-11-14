# Changelog

All notable changes to this project are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
