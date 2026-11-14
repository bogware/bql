using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>The engine's entry points, dialects, options and the neutral vocabulary.</summary>
public sealed class EngineSurfaceTests
{
    /// <summary>Rows with no time or text at all: a plain table.</summary>
    private sealed class TableSource(params (string Name, long Score)[] rows) : IBqlSource
    {
        public BqlSchema Schema { get; } = new() { DefaultSort = new BqlSortOrder("name", false) };

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (name, score) in rows)
            {
                var row = Schema.NewRow();
                row["name"] = name;
                row["score"] = score;
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>A table that sums scores by name itself, or declines when asked anything else.</summary>
    private sealed class SummingSource : IBqlAggregatingSource
    {
        private readonly TableSource _table = new(("ann", 7), ("bob", 3), ("ann", 1));

        public BqlSchema Schema => _table.Schema;

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public int Reads { get; private set; }

        public IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, CancellationToken cancellationToken = default)
        {
            Reads++;
            return _table.ReadAsync(pushdown, cancellationToken);
        }

        public ValueTask<BqlAggregateResult?> AggregateAsync(BqlAggregateRequest request, CancellationToken cancellationToken = default)
        {
            if (request is not { GroupBy: ["name"], Aggregates: [{ Kind: BqlAggregateKind.Sum, Field: "score" } spec] })
            {
                return ValueTask.FromResult<BqlAggregateResult?>(null);
            }

            return ValueTask.FromResult<BqlAggregateResult?>(new BqlAggregateResult
            {
                Rows =
                [
                    new() { ["name"] = "ann", [spec.OutputName] = 8.0 },
                    new() { ["name"] = "bob", [spec.OutputName] = 3.0 },
                ],
                EventsScanned = 3,
            });
        }
    }

    /// <summary>A toy dialect: "top N by field" means "sort -field | head N".</summary>
    private sealed class TopDialect : IBqlDialect
    {
        public string Name => "top";

        public bool TryTranslate(string text, BqlDialectContext context, out BqlQuery? query, out IReadOnlyList<string> diagnostics)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not ["top", var n, "by", var field] || !int.TryParse(n, out _))
            {
                query = null;
                diagnostics = ["expected: top N by field"];
                return false;
            }

            query = context.Parse($"find * | sort -{field} | head {n}");
            diagnostics = [];
            return true;
        }
    }

    private static readonly TableSource Table = new(("ann", 7), ("bob", 3), ("cy", 9));

    [Fact]
    public async Task ATableWithNoTimeOrText_StillQueries()
    {
        var engine = new BqlQueryEngine(Table);

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | where score > 5 | stats avg(score) as mean" });

        Convert.ToDouble(((IDictionary<string, object?>)result.Events.Single())["mean"]).Should().Be(8);
    }

    [Fact]
    public async Task AnAggregatingSource_AnswersWhatItCan()
    {
        var source = new SummingSource();
        var engine = new BqlQueryEngine(source);

        var pushed = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | stats sum(score) as total by name | sort -total" });
        var declined = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | stats max(score) as top by name | sort -top" });

        pushed.Statistics!.PushedDown.Should().BeTrue();
        ((IDictionary<string, object?>)pushed.Events[0])["total"].Should().Be(8.0);
        declined.Statistics!.PushedDown.Should().BeFalse("the source returned null, so the engine read the rows");
        ((IDictionary<string, object?>)declined.Events[0])["top"].Should().Be(7L);
        source.Reads.Should().Be(1);
    }

    [Fact]
    public async Task ADialect_RunsAsBql()
    {
        var engine = new BqlQueryEngine(Table, new BqlEngineOptions { Dialects = [new TopDialect()] });

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "top 1 by score", Dialect = "top" });

        ((IDictionary<string, object?>)result.Events.Single())["name"].Should().Be("cy");
        engine.Dialects.Should().Equal("bql", "top");
    }

    [Fact]
    public void ADialectThatCantRead_SaysWhy()
    {
        var engine = new BqlQueryEngine(Table, new BqlEngineOptions { Dialects = [new TopDialect()] });

        var validation = engine.Validate("bottom 3", "top");

        validation.IsValid.Should().BeFalse();
        validation.Error.Should().Contain("top N by field");
    }

    [Fact]
    public void AnUnknownDialect_IsAnError()
    {
        var engine = new BqlQueryEngine(Table);

        engine.Validate("select 1", "sql").Error.Should().Contain("doesn't read 'sql'");
    }

    [Fact]
    public void Validate_NamesTheStages()
    {
        var engine = new BqlQueryEngine(Table);

        engine.Validate("find * | where score > 1 | stats count").Stages.Should().Equal("search", "where", "stats");
        engine.Validate("find * | stats").IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Stream_YieldsRowsAsTheyCome()
    {
        var engine = new BqlQueryEngine(Table);
        var names = new List<object?>();

        await foreach (IDictionary<string, object?> row in engine.ExecuteStreamAsync(new BqlExecutionContext { Query = "find * | where score >= 7" }))
        {
            names.Add(row["name"]);
        }

        names.Should().Equal("ann", "cy");
    }

    [Fact]
    public async Task Limits_ComeFromOptions()
    {
        var engine = new BqlQueryEngine(Table, new BqlEngineOptions { Limits = new BqlLimits { MaxGroups = 2 } });

        var run = () => engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | stats count by name" });

        await run.Should().ThrowAsync<BqlLimitExceededException>().WithMessage("*2 groups*");
    }

    [Fact]
    public void ParserLimits_ComeFromOptions()
    {
        var parser = new BqlParser(limits: new BqlParserLimits { MaxPipelineStages = 2 });

        var parse = () => parser.ParseBql("find * | head 1 | head 1 | head 1");

        parse.Should().Throw<BqlParseException>().WithMessage("*2 pipeline stages*");
    }

    [Fact]
    public void TheParser_IsSafeAcrossThreads()
    {
        var parser = new BqlParser();
        var queries = Enumerable.Range(0, 200).Select(i => $"find * | where n = {i} | stats count by k{i}").ToList();

        // AsParallel with an index keeps each query paired with its number

        var parsed = queries.AsParallel()
            .Select((q, i) => (Expected: $"k{i}", Got: ((StatsStage)parser.ParseBql(q).Stages.Last()).GroupByFields[0]))
            .ToList();

        parsed.Should().OnlyContain(p => p.Expected == p.Got);
    }

    [Fact]
    public void TheNeutralVocabulary_KnowsNoLogFields()
    {
        var reading = new QueryUnderstander().Understand("errors on web-01");

        (reading.Query ?? string.Empty).Should().NotContain("severity").And.NotContain("host");
    }

    [Fact]
    public void AHostVocabulary_ReadsItsOwnWords()
    {
        var words = BqlVocabulary.CreateNeutral();
        words.FieldWords["size"] = ["size"];
        var catalog = new FieldCatalog(["size", "ext", "path"], new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ext"] = ["pdf", "jpg"],
        });

        var reading = new QueryUnderstander(words).Understand("largest 5 pdf files by size", catalog);

        reading.Query.Should().Contain("ext = \"pdf\"").And.Contain("size");
    }
}
