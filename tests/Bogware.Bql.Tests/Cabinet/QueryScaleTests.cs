using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Cabinet;

/// <summary>Queries over far more events than fit in memory, from a generated event source.</summary>
public sealed class QueryScaleTests
{
    private const int ManyEvents = 350_000;

    private static BqlQueryEngine Engine(IBqlSource storage, BqlLimits? limits = null) =>
        new(storage, new BqlEngineOptions { Limits = limits ?? new BqlLimits() });

    private static Task<BqlResult> RunAsync(IBqlSource storage, string query, BqlLimits? limits = null, int limit = 100_000, TimeSpan? timeout = null) =>
        Engine(storage, limits).ExecuteAsync(new BqlExecutionContext { Query = query, Limit = limit, Timeout = timeout ?? TimeSpan.FromMinutes(2) });

    private static List<IDictionary<string, object?>> Rows(BqlResult result) =>
        result.Events.Select(row => (IDictionary<string, object?>)row).ToList();

    [Fact]
    public async Task Stats_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats count by host");

        result.Statistics!.Truncated.Should().BeFalse();
        result.Statistics.EventsScanned.Should().Be(ManyEvents);
        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
        result.Events.Should().HaveCount(GeneratedStorage.HostCount);
    }

    [Fact]
    public async Task Aggregates_MatchTheirDefinition_OverEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats sum(n) as total, avg(n) as mean, min(n) as low, max(n) as high, dc(host) as hosts, stdevp(n) as sd");

        var row = Rows(result).Single();
        var n = (double)ManyEvents;
        Convert.ToDouble(row["total"]).Should().Be(n * (n - 1) / 2);
        Convert.ToDouble(row["mean"]).Should().BeApproximately((n - 1) / 2, 1e-6);
        Convert.ToDouble(row["low"]).Should().Be(0);
        Convert.ToDouble(row["high"]).Should().Be(n - 1);
        Convert.ToInt32(row["hosts"]).Should().Be(GeneratedStorage.HostCount);
        Convert.ToDouble(row["sd"]).Should().BeApproximately(Math.Sqrt(((n * n) - 1) / 12), 1e-3);
    }

    [Fact]
    public async Task Median_OverTooManyToKeep_IsCloseAndMarkedApproximate()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats median(n) as mid, p99(n) as tail");

        result.Statistics!.Approximate.Should().BeTrue();
        var row = Rows(result).Single();
        Convert.ToDouble(row["mid"]).Should().BeApproximately(ManyEvents * 0.5, ManyEvents * 0.01);
        Convert.ToDouble(row["tail"]).Should().BeApproximately(ManyEvents * 0.99, ManyEvents * 0.01);
    }

    [Fact]
    public async Task Median_OfAFewValues_IsExact()
    {
        var storage = new GeneratedStorage(4);

        var result = await RunAsync(storage, "find * | stats median(n) as mid");

        result.Statistics!.Approximate.Should().BeFalse();
        Convert.ToDouble(Rows(result).Single()["mid"]).Should().Be(1.5);
    }

    [Fact]
    public async Task Timechart_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | timechart span=1h count");

        result.Statistics!.Truncated.Should().BeFalse();
        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
    }

    [Fact]
    public async Task Top_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | top 100 host");

        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
    }

    [Fact]
    public async Task Filter_MatchingOnlyPastTheOldReadLimit_FindsIt()
    {
        var storage = new GeneratedStorage(ManyEvents);

        // Newest first, so n=0 is the very last event read
        var result = await RunAsync(storage, "find * | where n = 0");

        result.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Head_ReadsOnlyWhatItNeeds()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | head 5");

        result.Events.Should().HaveCount(5);
        result.Statistics!.EventsScanned.Should().Be(5);
        storage.PagesRead.Should().Be(1);
        storage.RowsRead.Should().Be(5, "the source stops as soon as the engine has enough");
    }

    [Fact]
    public async Task FilterThenHead_StopsReadingOnceItHasEnough()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | where n < 340000 | head 5");

        result.Events.Should().HaveCount(5);
        result.Statistics!.EventsScanned.Should().BeLessThan(ManyEvents / 10);
    }

    [Fact]
    public async Task SortThenHead_OverEveryEvent_GivesTheSameAsAFullSort()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | sort n | head 3");

        result.Statistics!.Truncated.Should().BeFalse();
        Rows(result).Select(row => Convert.ToInt64(row["n"])).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Sort_OfMoreRowsThanItCanHold_IsMarkedTruncated()
    {
        var storage = new GeneratedStorage(2_000);

        var result = await RunAsync(storage, "find * | sort n | eval x = 1", new BqlLimits { MaxRowsInMemory = 500 });

        result.Statistics!.Truncated.Should().BeTrue();
        result.Events.Should().HaveCount(500);
    }

    [Fact]
    public async Task Sort_OfMixedTypes_DoesNotThrow()
    {
        var storage = new InMemoryStorage();
        var events = new[] { "200", "-", "404" }.Select((status, i) =>
        {
            var logEvent = new LogEvent { RawLine = status, Timestamp = DateTime.UtcNow.AddSeconds(i) };
            logEvent.SetField("status", long.TryParse(status, out var number) ? number : status);
            return logEvent;
        }).ToList();
        await storage.StoreBatchAsync(events);

        var result = await RunAsync(storage, "find * | sort status");

        Rows(result).Select(row => row["status"]!.ToString()).Should().Equal("200", "404", "-");
    }

    [Fact]
    public async Task TooManyGroups_FailsTheQuery_RatherThanRunningOutOfMemory()
    {
        var storage = new GeneratedStorage(1_000);

        var run = () => RunAsync(storage, "find * | stats count by n", new BqlLimits { MaxGroups = 100 });

        await run.Should().ThrowAsync<BqlLimitExceededException>().WithMessage("*100 groups*");
    }

    [Fact]
    public async Task TooManyDistinctValues_FailsTheQuery()
    {
        var storage = new GeneratedStorage(1_000);

        var run = () => RunAsync(storage, "find * | stats dc(n) as values", new BqlLimits { MaxRetainedValues = 100 });

        await run.Should().ThrowAsync<BqlLimitExceededException>();
    }

    [Fact]
    public async Task MaxEventsScanned_StopsTheQuery_AndMarksItTruncated()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats count by host", new BqlLimits { MaxEventsScanned = 20_000 });

        result.Statistics!.Truncated.Should().BeTrue();
        result.Statistics.EventsScanned.Should().Be(20_000);
    }

    [Fact]
    public async Task SlowRegex_FailsQuickly_InsteadOfUsingTheWholeTimeLimit()
    {
        var storage = new GeneratedStorage(1_000, line: _ => new string('a', 40) + "!");
        var started = DateTime.UtcNow;

        var run = () => RunAsync(storage, "find * | rex field=RawLine \"(a+)+$\"");

        await run.Should().ThrowAsync<BqlLimitExceededException>().WithMessage("*too slow*");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("find * | where match(_raw, \"^(a+)+$\")")]
    [InlineData("find * | eval x = replace(_raw, \"^(a+)+$\", \"b\")")]
    public async Task SlowRegexInAnExpression_FailsTheQuery_InsteadOfQuietlyNotMatching(string query)
    {
        var storage = new GeneratedStorage(10, line: _ => new string('a', 40) + "!");

        var run = () => RunAsync(storage, query);

        await run.Should().ThrowAsync<BqlLimitExceededException>().WithMessage("*too slow*");
    }

    [Fact]
    public async Task Timeout_StopsAQueryThatIsStillReading()
    {
        var storage = new GeneratedStorage(int.MaxValue);
        var started = DateTime.UtcNow;

        var run = () => RunAsync(storage, "find * | stats count by host", timeout: TimeSpan.FromMilliseconds(300));

        await run.Should().ThrowAsync<OperationCanceledException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void LongChainOfNot_DoesNotOverflowTheStack()
    {
        var query = "find * | where " + string.Concat(Enumerable.Repeat("not ", 9_900)) + "a = 1";

        var parse = () => new BqlParser(LogSchema.Instance).ParseBql(query);

        parse.Should().NotThrow();
    }

    // Chains as long as the token limit allows. The old code ended the process with a stack
    // overflow evaluating the arithmetic ones.
    [Theory]
    [InlineData("find * | eval x = 1", " + 1", 4_990)]
    [InlineData("find * | eval x = 1", " * 1", 4_990)]
    [InlineData("find * | eval x = \"a\"", " . \"a\"", 4_990)]
    [InlineData("find * | where n = 1", " OR n = 1", 2_490)]
    [InlineData("find * | where n = 1", " AND n = 1", 2_490)]
    public async Task LongOperatorChain_FailsAsAParseError_InsteadOfOverflowingTheStack(string start, string repeated, int times)
    {
        var storage = new GeneratedStorage(10);
        var query = start + string.Concat(Enumerable.Repeat(repeated, times));

        var run = () => RunAsync(storage, query);

        await run.Should().ThrowAsync<BqlParseException>().WithMessage("*too deeply nested*");
    }

    [Fact]
    public async Task OperatorChain_JustUnderTheDepthLimit_StillGivesTheRightAnswer()
    {
        var storage = new GeneratedStorage(3);

        var result = await RunAsync(storage, "find * | eval x = 1" + string.Concat(Enumerable.Repeat(" + 1", 900)) + " | where x = 901 OR n = -1" + string.Concat(Enumerable.Repeat(" OR n = -1", 900)));

        Rows(result).Select(row => Convert.ToDouble(row["x"])).Should().Equal(901, 901, 901);
    }

    [Fact]
    public async Task DeeplyNestedSearchParentheses_FailAsAParseError_InsteadOfOverflowingTheStack()
    {
        var storage = new GeneratedStorage(10);
        var query = "find " + new string('(', 4_000) + "hello" + new string(')', 4_000);

        var run = () => RunAsync(storage, query);

        await run.Should().ThrowAsync<BqlParseException>().WithMessage("*too deeply*");
    }

    [Fact]
    public void LongChainOfNot_InSearchText_DoesNotOverflowTheStack()
    {
        var even = BqlTextSearch.Parse(string.Concat(Enumerable.Repeat("NOT ", 20_000)) + "hello");
        var odd = BqlTextSearch.Parse(string.Concat(Enumerable.Repeat("NOT ", 20_001)) + "hello");

        var row = new Dictionary<string, object?> { ["RawLine"] = "hello world" };
        even.Matches(row, LogSchema.Instance).Should().BeTrue();
        odd.Matches(row, LogSchema.Instance).Should().BeFalse();
    }

    [Fact]
    public void DeeplyNestedSubsearches_FailAsAParseError()
    {
        var query = "find *" + string.Concat(Enumerable.Repeat(" | append [search *", 1_000)) + new string(']', 1_000);

        var parse = () => new BqlParser(LogSchema.Instance).ParseBql(query);

        parse.Should().Throw<BqlParseException>().WithMessage("*nested too deeply*");
    }

    [Fact]
    public async Task Multisearch_CantHoldMoreThanTheRowLimit()
    {
        var storage = new GeneratedStorage(1_000);

        var result = await RunAsync(
            storage,
            "| multisearch [search \"*\"] [search \"*\"] [search \"*\"]",
            new BqlLimits { MaxRowsInMemory = 1_500 });

        result.Events.Should().HaveCount(1_500);
        result.Statistics!.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Stream_BeyondTheOldReadLimit_ReadsEveryMatch()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var count = 0L;
        await foreach (var _ in Engine(storage).ExecuteStreamAsync(new BqlExecutionContext { Query = "find * | where n < 50000", Limit = int.MaxValue }))
        {
            count++;
        }

        count.Should().Be(50_000);
    }

    /// <summary>
    /// Generates events on demand, newest first, so a test can read millions of them without
    /// storing any. It does nothing itself: the engine filters, bounds and stops reading.
    /// </summary>
    private sealed class GeneratedStorage : IBqlCountingSource
    {
        public const int HostCount = 7;

        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly int _count;
        private readonly Func<int, string> _line;

        public GeneratedStorage(int count, Func<int, string>? line = null)
        {
            _count = count;
            _line = line ?? (n => $"event {n}");
        }

        public BqlSchema Schema => LogSchema.Instance;

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public int PagesRead { get; private set; }

        public long RowsRead { get; private set; }

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PagesRead++;

            // Newest first: the highest n comes first
            for (var next = _count - 1; next >= 0; next--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logEvent = new LogEvent
                {
                    Id = next.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RawLine = _line(next),
                    Host = $"host-{next % HostCount}",
                    Timestamp = Start.AddSeconds(next % 86_400),
                };
                logEvent.SetField("n", (long)next);
                RowsRead++;
                yield return LogSchema.ToRow(logEvent);
            }

            await Task.CompletedTask;
        }

        public ValueTask<long> CountAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default) => ValueTask.FromResult((long)_count);
    }
}
