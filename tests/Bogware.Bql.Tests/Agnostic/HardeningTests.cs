using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>
/// What an embedding host relies on when the person typing the query is not trusted: subsearches
/// stay inside the caller's datasets and time, nesting stops, values and rows stay bounded, long
/// work can be stopped, and nothing but a BQL error ever comes back.
/// </summary>
public sealed class HardeningTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Rows in two datasets, "public" and "secret". Like a real host's storage, it keeps a caller
    /// to the datasets the engine says they may read, and it remembers what it was asked for.
    /// </summary>
    private sealed class TenantSource : IBqlSource
    {
        public BqlSchema Schema { get; } = new() { DatasetField = "index" };

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public ConcurrentQueue<BqlPushdown> Reads { get; } = new();

        /// <summary>Thrown on every read after the first, to stand in for a storage fault.</summary>
        public Exception? FailAfterFirstRead { get; init; }

        /// <summary>How long each read after the first waits before answering.</summary>
        public TimeSpan SlowAfterFirstRead { get; init; }

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Reads.Enqueue(pushdown);
            if (Reads.Count > 1 && FailAfterFirstRead is { } failure)
            {
                throw failure;
            }

            if (Reads.Count > 1 && SlowAfterFirstRead > TimeSpan.Zero)
            {
                await Task.Delay(SlowAfterFirstRead, cancellationToken);
            }

            foreach (var (index, name) in new[] { ("public", "ann"), ("public", "bob"), ("secret", "eve") })
            {
                if (pushdown.AllowedDatasets is { } allowed && !allowed.Contains(index))
                {
                    continue;
                }

                var row = Schema.NewRow();
                row["index"] = index;
                row["name"] = name;
                row["_time"] = From.AddHours(1);
                yield return row;
            }
        }
    }

    private static BqlExecutionContext Caller(string query) => new()
    {
        Query = query,
        AllowedDatasets = ["public"],
        StartTime = From,
        EndTime = To,
    };

    private static IEnumerable<IDictionary<string, object?>> Rows(BqlResult result) =>
        result.Events.Cast<IDictionary<string, object?>>();

    // ─── 1. Subsearches run as the caller ──────────────────────────────

    [Theory]
    [InlineData("find * | append [find *]")]
    [InlineData("| multisearch [find *] [find name=bob]")]
    [InlineData("find * | join name [find *]")]
    [InlineData("find * | join type=left name [find *]")]
    [InlineData("find * | subsearch [find * | fields name]")]
    [InlineData("find * | append [find * | append [find *]]")]
    [InlineData("| makeresults | append [| multisearch [find *] [find * | join name [find *]]]")]
    public async Task EverySubsearch_ReadsOnlyWhatTheCallerMay(string query)
    {
        var source = new TenantSource();
        var engine = new BqlQueryEngine(source);

        var result = await engine.ExecuteAsync(Caller(query));

        source.Reads.Should().HaveCountGreaterThan(1, "the subsearches read too");
        source.Reads.Should().OnlyContain(p => p.AllowedDatasets != null && p.AllowedDatasets.SequenceEqual(new[] { "public" }));
        source.Reads.Should().OnlyContain(p => p.StartTime == From && p.EndTime == To);
        Rows(result).Select(r => r.TryGetValue("name", out var n) ? n : null).Should().NotContain("eve");
    }

    [Fact]
    public async Task ASubsearch_StaysInTheCallersDataset()
    {
        var source = new TenantSource();
        var engine = new BqlQueryEngine(source);

        await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | append [find *]", Dataset = "public" });

        source.Reads.Should().HaveCount(2);
        source.Reads.Should().OnlyContain(p => p.Datasets != null && p.Datasets.SequenceEqual(new[] { "public" }));
    }

    [Fact]
    public async Task ASubsearch_StopsWhenTheQuerysTimeIsUp()
    {
        var source = new TenantSource { SlowAfterFirstRead = TimeSpan.FromSeconds(30) };
        var engine = new BqlQueryEngine(source, new BqlEngineOptions { DefaultTimeout = TimeSpan.FromMinutes(5) });
        var clock = Stopwatch.StartNew();

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | append [find *]", Timeout = TimeSpan.FromMilliseconds(300) });

        await run.Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the subsearch has only what is left of the query's time");
    }

    // ─── 2. One nesting budget for macros and subsearches ──────────────

    [Fact]
    public async Task AMacroThatCallsItselfThroughASubsearch_Stops()
    {
        var knowledge = new InMemoryKnowledgeStorage();
        await knowledge.SaveMacroAsync(new QueryMacro { Name = "m", Definition = "append [| makeresults | macro m]" });
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Knowledge = knowledge });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = "| makeresults | macro m" });

        (await run.Should().ThrowAsync<BqlLimitExceededException>()).Which.Message.Should().Contain("nest");
    }

    [Fact]
    public async Task SubsearchesNestedTooDeep_Stop()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { MaxNestingDepth = 3 });
        var shallow = "| makeresults" + string.Concat(Enumerable.Repeat(" | append [| makeresults", 3)) + new string(']', 3);
        var deep = "| makeresults" + string.Concat(Enumerable.Repeat(" | append [| makeresults", 4)) + new string(']', 4);

        (await engine.ExecuteAsync(new BqlExecutionContext { Query = shallow })).Events.Should().HaveCount(4);
        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = deep });
        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    // ─── 3. Memory ─────────────────────────────────────────────────────

    [Fact]
    public async Task AValueThatDoublesEachStep_Stops()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxValueLength = 1_000 } });
        var query = "| makeresults | eval x=\"ab\"" + string.Concat(Enumerable.Repeat(" | eval x=x . x", 12));

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = query });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    [Theory]
    [InlineData("mvrange(0, 1000000000000000, 1)")]
    [InlineData("mvappend(mvrange(0, 9000), mvrange(0, 9000))")]
    [InlineData("split(\"a,a,a,a,a,a,a,a,a,a,a,a\", \",\")")]
    [InlineData("mvzip(mvrange(0, 9000), mvrange(0, 9000), mvrange(0, 9000))")]
    public async Task AFunctionThatBuildsTooMuch_Stops(string expression)
    {
        var limits = new BqlLimits { MaxMultiValueCount = 10, MaxValueLength = 1_000 };
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = limits });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = $"| makeresults | eval x={expression}" });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    [Fact]
    public async Task Replace_CantGrowAValuePastTheLimit()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxValueLength = 1_000 } });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext
        {
            Query = "| makeresults | eval x=\"" + new string('a', 100) + "\" | eval x=replace(x, \"a\", \"aaaaaaaaaaaaaaaaaaaa\")",
        });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    [Fact]
    public async Task Mvrange_CountsItsSteps_EvenWhereAddingAStepChangesNothing()
    {
        // 1e16 + 1 == 1e16 in a double: stepping by adding would never reach the end
        var engine = new BqlQueryEngine(new TenantSource());

        var result = await engine.ExecuteAsync(new BqlExecutionContext
        {
            Query = "| makeresults | eval n=mvcount(mvrange(10000000000000000, 10000000000000010, 1))",
        });

        Convert.ToInt64(Rows(result).Single()["n"]).Should().Be(10);
    }

    [Fact]
    public async Task Mvexpand_HoldsItsOutputToTheRowLimit()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxRowsInMemory = 50 } });

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "| makeresults count=10 | eval v=mvrange(0, 100) | mvexpand v" });

        result.Events.Should().HaveCount(50);
        result.Statistics!.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task AJoinThatMultiplies_IsHeldToTheRowLimit()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxRowsInMemory = 50 } });

        // Every row shares the key k, so 40 rows joined to 40 rows would make 1,600
        var result = await engine.ExecuteAsync(new BqlExecutionContext
        {
            Query = "| makeresults count=40 | eval k=1 | join k [| makeresults count=40 | eval k=1 | streamstats count as n]",
        });

        result.Events.Should().HaveCount(50);
        result.Statistics!.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Makeresults_IsHeldToItsLimit()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxMakeResults = 10 } });

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "| makeresults count=1000" });

        result.Events.Should().HaveCount(10);
        result.Statistics!.Truncated.Should().BeTrue();
    }

    // ─── 4. Long work can be stopped ───────────────────────────────────

    [Fact]
    public async Task AWideStreamstatsWindow_IsRefused()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxStreamStatsWindow = 100 } });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = "| makeresults count=10 | streamstats window=1000 count" });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    [Theory]
    [InlineData("streamstats window=10000 avg(r) as a")]
    [InlineData("eventstats avg(r) as a by r | sort r | streamstats window=10000 max(r) as b")]
    public async Task AnEagerStage_StopsWhenTheTimeIsUp(string stages)
    {
        var engine = new BqlQueryEngine(new TenantSource());
        var clock = Stopwatch.StartNew();

        var run = () => engine.ExecuteAsync(new BqlExecutionContext
        {
            Query = $"| makeresults count=50000 | eval r=random() | {stages}",
            Timeout = TimeSpan.FromMilliseconds(300),
        });

        await run.Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Eventstats_CountsAgainstTheGroupLimit()
    {
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { MaxGroups = 10 } });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = "| makeresults count=100 | streamstats count as n | eventstats count by n" });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    // ─── 5. Only BQL errors come back ──────────────────────────────────

    [Theory]
    [InlineData("find * | where x > \u0663")]
    [InlineData("find * | where x > 99999999999999999999999")]
    [InlineData("find * | rex \"(\"")]
    [InlineData("find * | regex \"[\"")]
    [InlineData("find * | bin _time span=0s")]
    [InlineData("find * | bin x span=0")]
    [InlineData("find * | bucket _time span=-1")]
    [InlineData("find * | timechart span=0s count")]
    public void AQueryMistake_IsAParseError(string query)
    {
        var engine = new BqlQueryEngine(new TenantSource());

        var parse = () => engine.Parse(query);

        parse.Should().Throw<BqlParseException>().Which.Should().BeOfType<BqlParseException>();
        engine.Validate(query).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("printf(\"%99999999999999d\", 1)")]
    [InlineData("printf(\"%.99999999999999f\", 1)")]
    [InlineData("memk(\"99999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999k\")")]
    [InlineData("relative_time(-62135596800, \"@w\")")]
    [InlineData("tonumber(\"ff\", 7)")]
    [InlineData("lpad(\"a\", 99999999999, \"b\")")]
    public async Task AnAwkwardArgument_GivesAValueOrNothing(string expression)
    {
        var engine = new BqlQueryEngine(new TenantSource());

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = $"| makeresults | eval x={expression}" });

        result.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task RandomAndBrokenQueries_OnlyEverThrowBqlErrors()
    {
        string[] pieces =
        [
            "find", "*", "|", "[", "]", "(", ")", "\"", "=", "!=", ">", "<", ",", "by", "as", "where", "eval", "stats",
            "count", "sort", "-", "+", ".", "x", "name", "_time", "span=0s", "span=1h", "bin", "timechart", "rex",
            "\"(\"", "append", "join", "subsearch", "multisearch", "makeresults", "count=5", "mvexpand", "mvrange(0,5)",
            "streamstats", "window=3", "eventstats", "transaction", "fieldsummary", "dedup", "head", "tail", "top",
            "rare", "lookup", "macro", "inputlookup", "fields", "table", "rename", "fillnull", "if(", "case(",
            "printf(\"%9999999999d\",1)", "\u0663", "99999999999999999999", "1e999", "0x", "@w", "-1d@w", "earliest=",
            "latest=-1y", "AND", "OR", "NOT", "like", "\"%\"", "match(x,\"(\")", "replace(", "split(", "mvzip(", "\\",
            "'", "`", "\u0000", "\u202e", "\uD83D\uDE00", "tonumber(x,99)", "strftime(", "relative_time(0,\"@w\")",
            "| where x > \u0663", "| rex \"(\"", "| timechart span=0s count", "| eval x=printf(\"%.9999999999f\",1)",
            "| bin _time span=0s", "| eval t=relative_time(-62135596800,\"@w\")",
        ];

        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { DefaultTimeout = TimeSpan.FromSeconds(5) });
        var random = new Random(20261114);
        for (var i = 0; i < 400; i++)
        {
            var query = string.Join(' ', Enumerable.Range(0, random.Next(1, 14)).Select(_ => pieces[random.Next(pieces.Length)]));
            if (random.Next(3) == 0)
            {
                query = "| makeresults | " + query;
            }

            await ShouldOnlyThrowBqlErrors(engine, query);
        }
    }

    private static async Task ShouldOnlyThrowBqlErrors(BqlQueryEngine engine, string query)
    {
        try
        {
            engine.Validate(query);
            engine.Parse(query);
            await engine.ExecuteAsync(new BqlExecutionContext { Query = query });
        }
        catch (BqlParseException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"'{query}' threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─── 6. Natural language input stays cheap ─────────────────────────

    [Fact]
    public void AnEnormousQuestion_IsAnsweredQuickly()
    {
        var understander = new QueryUnderstander();
        var text = "show errors on a" + new string('1', 80_000) + "! since monday";
        var clock = Stopwatch.StartNew();

        var understood = understander.Understand(text);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        understood.Input.Should().Be(text);
    }

    [Fact]
    public void AnEnormousWord_IsReadQuickly()
    {
        var reader = new NaturalReader();
        var clock = Stopwatch.StartNew();

        reader.Read("errors on a" + new string('1', 80_000) + "!", FieldCatalog.Empty, DateTime.UtcNow);
        reader.Read("errors on a" + new string('1', 1_900) + "!", FieldCatalog.Empty, DateTime.UtcNow);
        reader.Read("errors from " + new string('f', 1_900) + "!", FieldCatalog.Empty, DateTime.UtcNow);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // ─── 7. Lookups and macros are asked for as the caller ─────────────

    [Fact]
    public async Task TheKnowledgeProvider_IsToldWhoIsAsking()
    {
        var knowledge = new InMemoryKnowledgeStorage();
        await knowledge.SaveMacroAsync(new QueryMacro { Name = "m", Definition = "eval y=1" });
        await knowledge.SaveLookupAsync(new LookupTable { Name = "people", Columns = ["name", "team"], Rows = [new() { ["name"] = "ann", ["team"] = "red" }] });
        var engine = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Knowledge = knowledge });

        await engine.ExecuteAsync(new BqlExecutionContext
        {
            Query = "find * | append [find * | macro m] | lookup people name OUTPUT team",
            AllowedDatasets = ["public"],
            Dataset = "public",
        });

        knowledge.Callers.Should().HaveCountGreaterThanOrEqualTo(2);
        knowledge.Callers.Should().OnlyContain(c => c.Dataset == "public" && c.AllowedDatasets!.SequenceEqual(new[] { "public" }));
    }

    // ─── 8. A failed subsearch says so, without the internals ──────────

    [Theory]
    [InlineData("find * | join name [find *]")]
    [InlineData("find * | subsearch [find * | fields name]")]
    public async Task AFailedSubsearch_KeepsTheDetailForTheHost(string query)
    {
        var diagnostics = new ConcurrentQueue<BqlDiagnostic>();
        var source = new TenantSource { FailAfterFirstRead = new InvalidOperationException(@"connection to db://10.1.2.3/secret refused") };
        var engine = new BqlQueryEngine(source, new BqlEngineOptions { Diagnostics = diagnostics.Enqueue });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = query });

        var thrown = await run.Should().ThrowAsync<BqlExecutionException>();
        thrown.Which.Message.Should().NotContain("10.1.2.3").And.NotContain("secret");
        diagnostics.Should().Contain(d => d.Exception != null && d.Exception.ToString().Contains("10.1.2.3"));
    }

    [Fact]
    public async Task AFailedAppend_KeepsTheMainRows_AndTellsOnlyTheHost()
    {
        var diagnostics = new ConcurrentQueue<BqlDiagnostic>();
        var source = new TenantSource { FailAfterFirstRead = new InvalidOperationException(@"connection to db://10.1.2.3/secret refused") };
        var engine = new BqlQueryEngine(source, new BqlEngineOptions { Diagnostics = diagnostics.Enqueue });

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | append [find *]" });

        result.Events.Should().HaveCount(3, "as in SPL, a failed append leaves the rows before it");
        diagnostics.Should().Contain(d => d.Exception != null && d.Exception.ToString().Contains("10.1.2.3"));
    }

    // ─── 9. Limits belong to an engine ─────────────────────────────────

    [Fact]
    public void Limits_CantBeChangedOnceMade()
    {
        foreach (var type in new[] { typeof(BqlLimits), typeof(BqlParserLimits) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = property.SetMethod;
                (setter == null || setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                    .Should().BeTrue($"{type.Name}.{property.Name} should be init-only");
            }
        }
    }

    [Fact]
    public async Task EachEngine_KeepsItsOwnPatternTime()
    {
        // A pattern that backtracks for a very long time on this value
        const string query = "| makeresults | eval s=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!\" | eval m=if(match(s, \"^(a+)+$\"), 1, 0)";
        var quick = new BqlQueryEngine(new TenantSource(), new BqlEngineOptions { Limits = new BqlLimits { RegexTimeoutPerRow = TimeSpan.FromMilliseconds(20) } });
        var clock = Stopwatch.StartNew();

        var run = () => quick.ExecuteAsync(new BqlExecutionContext { Query = query });

        (await run.Should().ThrowAsync<BqlLimitExceededException>()).Which.Message.Should().Contain("too slow");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        typeof(BqlQueryEngine).Assembly.GetType("Bogware.Bql.BqlRegex").Should().BeNull("the per-row time is the engine's, not the process's");
    }
}
