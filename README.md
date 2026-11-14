# BQL

A query language and a streaming engine for any rows you have.

BQL is a pipe language. You name what to read, then pass the rows through stages. It reads SPL as well, so people who know Splunk can work the way they're used to. It also reads plain English questions, offline, with no model.

The engine doesn't know or care what your rows are. You give it a source: log events, files in a vault, a table, a list in memory. The source says what its rows look like and what it can do itself. The engine does the rest.

```
find * | where status >= 500 | stats count by path | sort -count | head 10
search error host=web-* earliest=-24h | timechart span=1h count by host
find * | where ext = "pdf" and size > 10000000 | table path, size, modified
```

## What's here

| Project | What it is |
|---|---|
| `src/Bogware.Bql` | The language and the engine. Lexer, parser, optimizer, pipeline, 40-odd stages, 100-odd functions. No package dependencies. |
| `src/Bogware.Bql.Natural` | Reads plain-English questions into BQL. Offline. The host supplies the vocabulary. |
| `tests/Bogware.Bql.Tests` | The suite. Most of it came from Cabinet, where BQL was born, and runs against a log-shaped source to prove nothing changed. The rest runs against other shapes to prove nothing is tied to logs. |

## Using it

```csharp
var engine = new BqlQueryEngine(mySource);
var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | stats count by ext" });
foreach (IDictionary<string, object?> row in result.Events)
{
    Console.WriteLine($"{row["ext"]}: {row["count"]}");
}
```

A source is one interface. [docs/EMBEDDING.md](docs/EMBEDDING.md) shows how to write one, and what to tell the engine so it can leave work to you.

## Ask in plain words

With the log vocabulary, these questions read as these queries. Another vocabulary reads other words.

| You type | It runs |
|---|---|
| `errors on web servers since yesterday by host` | `find * earliest=-1d@d \| where severity >= error and host like "web%" \| stats count by host \| sort by count desc` |
| `how many unique users per day` | `find * \| timechart span=1d dc(user)` |
| `top 5 slowest endpoints` | `find * \| stats avg(duration_ms) as avg_duration_ms by path \| sort by avg_duration_ms desc \| head 5` |
| `POST requests over 1000 ms` | `find * \| where duration_ms > 1000 and method = "POST"` |
| `count of 5xx errors per endpoint` | `find * \| where status >= 500 and status <= 599 \| stats count by path \| sort by count desc` |
| `erors by hots` | `find * \| where severity >= error \| stats count by host \| sort by count desc` |

## The guide

[docs/BQL-GUIDE.md](docs/BQL-GUIDE.md) covers the language: every stage, every function, the SPL forms, and how questions are read. Every query in it runs in the test suite.

## Building

```
dotnet build -c Release
dotnet test
```

.NET 10. Warnings are errors.

## Other languages

A host can add query languages of its own. Each one is an `IBqlDialect`: it reads its text into a `BqlQuery`, and the engine runs that like any other. SQL, KQL, a search box syntax: whatever the host's users write.

## License

AGPL-3.0. See [LICENSE](LICENSE).
