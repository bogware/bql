using System.Globalization;
using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>
/// <see cref="BqlPushdown.NeedsText"/> lets a source leave out a large text field. It must be
/// false only when no stage can see the text: a source that drops the text whenever it is told
/// it may has to give the same answers as one that always sends it.
/// </summary>
public sealed class NeedsTextTests
{
    private static readonly DateTime Base = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>File records whose text is the path and the document's words, under two names.</summary>
    private static readonly BqlSchema Schema = new()
    {
        TimeField = "modified",
        TextField = "text",
        TextFieldSynonyms = ["_raw"],
        Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content"] = "text",
            ["extension"] = "ext",
        },
        DefaultSort = new BqlSortOrder("modified", Descending: true),
    };

    private sealed record FileRecord(string Path, long Size, string[] Tags, int Day, string Words)
    {
        public string Name => System.IO.Path.GetFileName(Path);

        public string Ext => System.IO.Path.GetExtension(Path).TrimStart('.');

        public string Text => $"{Path}\n{Words}";
    }

    private static readonly FileRecord[] Files =
    [
        new("/docs/taxes-2025.pdf", 2_400_000, ["finance", "tax"], 1, "Tax return for 2025. Invoice totals attached."),
        new("/docs/invoice-0042.pdf", 90_000, ["finance"], 2, "Invoice 42: two chairs, one desk. Paid."),
        new("/docs/draft-invoice.docx", 30_000, [], 3, "Draft invoice, not sent. tax return notes"),
        new("/photos/beach.jpg", 5_100_000, ["photo", "summer"], 4, string.Empty),
        new("/photos/beach-copy.jpg", 5_100_000, ["photo"], 5, string.Empty),
        new("/notes/todo.md", 1_200, ["work"], 6, "todo: file the tax return, call about invoice 42"),
        new("/notes/ideas.md", 800, ["work"], 7, "ideas for 2026"),
        new("/music/song.flac", 31_000_000, [], 8, string.Empty),
    ];

    private static readonly BqlSourceCapabilities Exact = new()
    {
        TimeBounds = true,
        TextSearch = true,
        TimeOrder = true,
        ExactPredicates = p => p.Field.ToLowerInvariant() is "ext" or "size" or "name",
    };

    /// <summary>Reads the files. With <c>omit</c>, leaves out the text whenever the pushdown allows.</summary>
    private sealed class FileSource(bool exact, bool omit) : IBqlSource
    {
        public BqlSchema Schema => NeedsTextTests.Schema;

        public BqlSourceCapabilities Capabilities { get; } = exact ? Exact : BqlSourceCapabilities.None;

        public List<bool> Asked { get; } = [];

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (Asked)
            {
                Asked.Add(pushdown.NeedsText);
            }

            // Newest first is this source's own order, so plain and exact sources agree on order too
            IEnumerable<BqlRow> rows = Files.Select(ToRow).OrderByDescending(r => (DateTime)r["modified"]!);
            if (exact)
            {
                if (pushdown.StartTime is { } start)
                    rows = rows.Where(r => (DateTime)r["modified"]! >= start);
                if (pushdown.EndTime is { } end)
                    rows = rows.Where(r => (DateTime)r["modified"]! <= end);
                if (pushdown.TextTerms is { } terms)
                    rows = rows.Where(r => SearchTerms.Matches((string)r["text"]!, terms));
                foreach (var p in pushdown.Predicates)
                    rows = rows.Where(r => BqlComparison.Holds(p.Operator, r.GetValueOrDefault(p.Field), p.Value));
                rows = pushdown.Sort is { Descending: false }
                    ? rows.OrderBy(r => (DateTime)r["modified"]!)
                    : rows.OrderByDescending(r => (DateTime)r["modified"]!);
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Filtering above may read the text; what goes to the engine is another matter
                if (omit && !pushdown.NeedsText)
                {
                    row.Remove("text");
                    row.Remove("_raw");
                }

                yield return row;
            }

            await Task.CompletedTask;
        }

        private BqlRow ToRow(FileRecord file)
        {
            var row = Schema.NewRow();
            row["path"] = file.Path;
            row["name"] = file.Name;
            row["ext"] = file.Ext;
            row["size"] = file.Size;
            row["tags"] = file.Tags;
            row["modified"] = Base.AddDays(file.Day);
            row["text"] = file.Text;
            row["_raw"] = file.Text;
            return row;
        }
    }

    public static TheoryData<string> Queries =>
    [
        "*",
        "invoice",
        "invoice | stats count",
        "\"tax return\"",
        "\"tax return\" | stats count by ext",
        "invoice NOT draft | table name",
        "inv* | stats count",
        "invoice tax | table name, size",
        "invoice OR ideas | stats count",
        "* | where text like \"%invoice%\" | stats count",
        "* | where _raw like \"%invoice%\" | table name",
        "* | where CONTENT like \"%chairs%\" | table name",
        "* | where like(text, \"%2025%\") | stats count by ext",
        "* | where match(_raw, \"\\\\d{4}\") | table name",
        "* | rex \"(?<year>20\\d\\d)\" | stats count by year",
        "* | rex field=name \"(?<stem>[a-z]+)\" | stats count by stem",
        "* | rex field=_raw \"(?<stem>[a-z]+)\" | stats count by stem",
        "* | regex \"invoice\" | table name",
        "* | regex name=\"^t\" | table name",
        "* | regex text!=\"photo\" | table name",
        "* | eval n = len(_raw) | stats max(n) as longest",
        "* | eval n = len(name) | stats max(n) as longest",
        "* | eval first = substr(text, 0, 7) | table name, first",
        "* | eval t = coalesce(missing, text) | table t",
        "* | eval t = if(size > 1000, name, text) | table t",
        "* | where name in (\"todo.md\", text) | table name",
        "* | stats count by text",
        "* | stats dc(text) as d",
        "* | stats count(text) as n",
        "* | stats count(eval(len(text) > 40)) as long",
        "* | stats values(content) as v",
        "* | table name, size",
        "* | fields name",
        "* | fields name, _raw",
        "* | head 2",
        "* | head 3 | table name",
        "* | tail 2 | table name",
        "* | sort -size | head 3 | table name, size",
        "* | sort text | table name",
        "* | sort name | head 2",
        "* | where searchmatch(\"chairs\") | table name",
        "* | eval hit = searchmatch(\"tax\") | stats count by hit",
        "* | stats count by ext",
        "* | stats sum(size) as total by ext | sort -total",
        "* | stats count by ext | eval t = text",
        "* | top ext",
        "* | top 2 text",
        "* | rare ext",
        "* | chart count by ext",
        "* | timechart span=1d count",
        "* | timechart span=1d count by ext",
        "* | where size > 1KB | stats count",
        "* | where modified >= -3650d | stats count by ext",
        "* | where ext = \"pdf\" | table name",
        "* | where ext = \"pdf\" and size > 100KB | stats count",
        "* | where ext = \"pdf\" | eval t = text | table t",
        "* | dedup ext | table ext",
        "* | dedup text | table name",
        "* | eval big = if(size > 1000, \"yes\", \"no\") | stats count by big",
        "* | fillnull value=none missing | stats count by missing",
        "* | fillnull | table name, missing",
        "* | fillnull value=x text | table name, text",
        "* | rename name as n | table n",
        "* | rename text as body | table body",
        "* | rename _raw as body | stats count by body",
        "* | fieldalias body=text | table body",
        "* | fieldsummary",
        "* | append [search invoice | table name] | table name",
        "* | append [search invoice] | stats count",
        "* | join type=left name [search tax | eval tax = \"yes\" | table name, tax] | table name, tax",
        "* | subsearch [search chairs | fields name] | table name",
        "| multisearch [search invoice] [search ideas] | stats count",
        "* | eventstats count by ext | table name, count",
        "* | eventstats count by text | table name, count",
        "* | streamstats count as n | table name, n",
        "* | mvexpand tags | stats count by tags",
        "* | xyseries ext name size",
        "* | untable name k v | stats count by k",
        "* | transaction ext | table ext, _eventcount",
        "* | cluster | table cluster_pattern, cluster_count",
        "* | cluster field=name | table cluster_count",
        "* | cluster labelonly=true | stats count by cluster_label",
        "search ext=pdf | stats count",
        "search ext=pdf",
        "* | bucket size span=1000000 | stats count by size",
        "* | addcoltotals | table name, size",
        "* | addcoltotals size | table name, size",
        "* | where isnotnull(text) | stats count",
        "* | where isnull(text) | stats count",
        "* | eval x = mvfilter(tags, match(tags, \"ph\")) | stats count by x",
        "* | eval x = mvfilter(tags, searchmatch(\"chairs\")) | stats count by x",
    ];

    private static async Task<List<string>> RunAsync(IBqlSource source, string query)
    {
        var engine = new BqlQueryEngine(source);
        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = query, Limit = 1000 });
        return result.Events.Select(e => Describe((IDictionary<string, object?>)e)).ToList();
    }

    private static string Describe(IDictionary<string, object?> row) =>
        string.Join("; ", row.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={Describe(kv.Value)}"));

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => s,
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Describe)) + "]",
        _ => value.ToString() ?? string.Empty,
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task ASourceThatLeavesOutTheText_GivesTheSameAnswer(string query)
    {
        var expected = await RunAsync(new FileSource(exact: false, omit: false), query);

        (await RunAsync(new FileSource(exact: false, omit: true), query)).Should().Equal(expected, "a plain source that leaves out the text agrees");
        (await RunAsync(new FileSource(exact: true, omit: false), query)).Should().Equal(expected, "an exact source agrees");
        (await RunAsync(new FileSource(exact: true, omit: true), query)).Should().Equal(expected, "an exact source that leaves out the text agrees");
    }

    [Fact]
    public async Task TheTextIsLeftOutOften()
    {
        var spared = 0;
        foreach (var query in Queries.Cast<object[]>().Select(row => (string)row[0]))
        {
            var source = new FileSource(exact: true, omit: true);
            await RunAsync(source, query);
            spared += source.Asked.Count(needed => !needed);
        }

        spared.Should().BeGreaterThan(30, "most queries that project or aggregate without the text should spare it");
    }

    private static bool NeedsText(string query, BqlSourceCapabilities? capabilities = null, BqlSchema? schema = null)
    {
        schema ??= Schema;
        var parser = new BqlParser(schema);
        return new BqlOptimizer(schema, capabilities ?? BqlSourceCapabilities.None, parser).Optimize(parser.ParseBql(query)).Pushdown.NeedsText;
    }

    [Theory]
    [InlineData("* | stats count")]
    [InlineData("* | stats count by ext")]
    [InlineData("* | stats sum(size) as total, max(modified) as latest by ext, name")]
    [InlineData("* | where size > 10 | table name, size")]
    [InlineData("* | fields name")]
    [InlineData("* | top ext")]
    [InlineData("* | top 3 ext by name")]
    [InlineData("* | rare ext")]
    [InlineData("* | chart count by ext")]
    [InlineData("* | timechart span=1d count by ext")]
    [InlineData("* | eval x = size * 2 | stats sum(x)")]
    [InlineData("* | sort -size | head 5 | table name")]
    [InlineData("* | where name like \"a%\" and ext in (\"pdf\", \"md\") | stats count by ext")]
    [InlineData("* | rex field=name \"(?<w>\\w+)\" | stats count by w")]
    [InlineData("* | regex name=\"x\" | stats count")]
    [InlineData("* | rename name as n | table n")]
    [InlineData("* | xyseries ext name size")]
    [InlineData("* | cluster field=name")]
    [InlineData("* | cluster labelonly=true field=name | table cluster_label")]
    [InlineData("* | stats count by ext | eval t = text | table t")]
    [InlineData("* | head 5 | stats count")]
    [InlineData("* | eventstats count by ext | table name, count")]
    [InlineData("* | fillnull value=0 size | stats count by size")]
    [InlineData("* | where modified >= -7d | stats count")]
    public void Projections_WithoutTheText_DoNotNeedIt(string query)
    {
        NeedsText(query).Should().BeFalse();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("* | head 5")]
    [InlineData("* | where size > 10")]
    [InlineData("* | eval x = 1 | sort name")]
    [InlineData("error | stats count")]
    [InlineData("\"a phrase\" | stats count")]
    [InlineData("err* | stats count")]
    [InlineData("* | search error | stats count")]
    [InlineData("* | where text like \"%x%\" | stats count")]
    [InlineData("* | where _raw = \"x\" | stats count")]
    [InlineData("* | where CONTENT = \"x\" | stats count")]
    [InlineData("* | where Text = \"x\" | stats count")]
    [InlineData("* | rex \"(?<w>\\w+)\" | stats count by w")]
    [InlineData("* | rex field=content \"(?<w>\\w+)\" | stats count by w")]
    [InlineData("* | regex \"x\" | stats count")]
    [InlineData("* | stats count by text")]
    [InlineData("* | stats count(_raw)")]
    [InlineData("* | stats count(eval(len(text) > 5)) as n")]
    [InlineData("* | table name, text")]
    [InlineData("* | eval m = searchmatch(\"x\") | stats count by m")]
    [InlineData("* | where searchmatch(\"x\") | stats count")]
    [InlineData("* | eval t = if(size > 1, text, name) | table t")]
    [InlineData("* | eval t = coalesce(name, lower(upper(_raw))) | table t")]
    [InlineData("* | where name in (\"a\", text) | table name")]
    [InlineData("* | eval x = mvfilter(tags, searchmatch(\"x\")) | table x")]
    [InlineData("* | append [search x] | stats count")]
    [InlineData("* | join name [search x] | stats count")]
    [InlineData("* | subsearch [search x | fields name] | stats count")]
    [InlineData("| multisearch [search a] [search b] | stats count")]
    [InlineData("* | fieldsummary")]
    [InlineData("* | fillnull | stats count")]
    [InlineData("* | filldown | stats count")]
    [InlineData("* | addcoltotals | table name")]
    [InlineData("* | transaction name | stats count")]
    [InlineData("* | rename text as body | table body")]
    [InlineData("* | fieldalias body=_raw | table body")]
    [InlineData("* | untable name k v | stats count by k")]
    [InlineData("* | sort text | table name")]
    [InlineData("* | dedup text | table name")]
    [InlineData("* | top text")]
    [InlineData("* | eventstats count by text | table name")]
    [InlineData("* | mvexpand text | table name")]
    [InlineData("* | cluster | table cluster_count")]
    [InlineData("* | makeresults | table x")]
    public void AnythingThatMaySeeTheText_NeedsIt(string query)
    {
        NeedsText(query).Should().BeTrue();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("te*")]
    [InlineData("*xt")]
    public void AWildcardColumn_NeedsTheText(string column)
    {
        // The parser has no wildcard columns yet; a dialect could make one
        var query = new BqlQuery();
        query.Stages.Add(new SearchStage("*"));
        query.Stages.Add(new TableStage(["name", column]));

        new BqlOptimizer(Schema).Optimize(query).Pushdown.NeedsText.Should().BeTrue();
    }

    [Fact]
    public void AStageTheOptimizerDoesNotKnow_NeedsTheText()
    {
        var query = new BqlQuery();
        query.Stages.Add(new SearchStage("*"));
        query.Stages.Add(new UnknownStage());
        query.Stages.Add(new TableStage(["name"]));

        new BqlOptimizer(Schema).Optimize(query).Pushdown.NeedsText.Should().BeTrue();
    }

    private sealed class UnknownStage : BqlStage
    {
        public override string StageName => "unknown";
    }

    [Fact]
    public void AnExpressionTheOptimizerDoesNotKnow_NeedsTheText()
    {
        var query = new BqlQuery();
        query.Stages.Add(new SearchStage("*"));
        query.Stages.Add(new EvalStage("x", new UnknownExpression()));
        query.Stages.Add(new TableStage(["x"]));

        new BqlOptimizer(Schema).Optimize(query).Pushdown.NeedsText.Should().BeTrue();
    }

    private sealed class UnknownExpression : BqlExpression
    {
    }

    [Fact]
    public void ASearchTheSourceDoes_StillLeavesTheTextOut()
    {
        NeedsText("invoice | stats count", Exact).Should().BeFalse("the source applies the words; nothing after reads the text");
        NeedsText("invoice | stats count").Should().BeTrue("without a text search the pipeline reads the text");
        NeedsText("\"tax return\" | stats count", Exact).Should().BeTrue("a phrase is checked in the pipeline");
    }

    [Fact]
    public void TheDefaultSchema_ReadsRawAsText()
    {
        NeedsText("* | eval n = len(_raw) | stats max(n)", schema: BqlSchema.Default).Should().BeTrue();
        NeedsText("* | eval n = len(name) | stats max(n)", schema: BqlSchema.Default).Should().BeFalse();
    }

    [Fact]
    public void ATimeOrDatasetFieldThatIsTheText_IsAlwaysNeeded()
    {
        var odd = new BqlSchema { TimeField = "line", TextField = "line" };
        NeedsText("* | stats count", schema: odd).Should().BeTrue();

        var dataset = new BqlSchema { TextField = "line", DatasetField = "LINE" };
        NeedsText("* | stats count", schema: dataset).Should().BeTrue();
    }

    [Fact]
    public void Clone_KeepsNeedsText()
    {
        new BqlPushdown { NeedsText = false }.Clone().NeedsText.Should().BeFalse();
        new BqlPushdown().NeedsText.Should().BeTrue();
    }
}
