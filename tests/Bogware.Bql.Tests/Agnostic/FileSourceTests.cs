using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>
/// Queries over file records: a shape with no host, no severity, no raw line and a time field
/// with another name. Nothing here should need anything from logs.
/// </summary>
public sealed class FileSourceTests
{
    private static readonly DateTime Base = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly BqlSchema Schema = new()
    {
        TimeField = "modified",
        TextField = "name",
        DatasetField = "vault",
        Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["extension"] = "ext",
            ["bytes"] = "size",
        },
        OrdinalFields = new Dictionary<string, BqlOrdinalScale>(StringComparer.OrdinalIgnoreCase)
        {
            ["protection"] = new BqlOrdinalScale(
            [
                new BqlOrdinalLevel(0, ["none"]),
                new BqlOrdinalLevel(1, ["mirrored"]),
                new BqlOrdinalLevel(2, ["encrypted"]),
            ]),
        },
        Fields =
        [
            new BqlField("path", BqlFieldType.String),
            new BqlField("name", BqlFieldType.String),
            new BqlField("ext", BqlFieldType.String),
            new BqlField("size", BqlFieldType.Integer),
            new BqlField("mime", BqlFieldType.String),
            new BqlField("hash", BqlFieldType.String),
            new BqlField("tags", BqlFieldType.StringList),
            new BqlField("modified", BqlFieldType.Time),
            new BqlField("created", BqlFieldType.Time),
        ],
        DefaultSort = new BqlSortOrder("modified", Descending: true),
    };

    private sealed record FileRecord(string Path, long Size, string Mime, string Hash, string[] Tags, DateTime Modified, int Protection, string Vault = "home")
    {
        public string Name => System.IO.Path.GetFileName(Path);

        public string Ext => System.IO.Path.GetExtension(Path).TrimStart('.');
    }

    private static readonly FileRecord[] Files =
    [
        new("/docs/taxes-2025.pdf", 2_400_000, "application/pdf", "a1", ["finance", "tax"], Base.AddDays(1), 2),
        new("/docs/lease.pdf", 900_000, "application/pdf", "b2", ["home"], Base.AddDays(2), 1),
        new("/photos/beach.jpg", 5_100_000, "image/jpeg", "c3", ["photo", "summer"], Base.AddDays(3), 0),
        new("/photos/beach-copy.jpg", 5_100_000, "image/jpeg", "c3", ["photo"], Base.AddDays(4), 0),
        new("/music/song.flac", 31_000_000, "audio/flac", "d4", [], Base.AddDays(5), 1),
        new("/notes/todo.md", 1_200, "text/markdown", "e5", ["work"], Base.AddDays(6), 0, "work"),
    ];

    /// <summary>Files in memory. With <c>exact</c>, it bounds time, searches names and filters ext itself.</summary>
    private sealed class FileSource : IBqlSource
    {
        private readonly bool _exact;

        public FileSource(bool exact)
        {
            _exact = exact;
            Capabilities = exact
                ? new BqlSourceCapabilities
                {
                    TimeBounds = true,
                    TextSearch = true,
                    TimeOrder = true,
                    ExactPredicates = p => p.Field.Equals("ext", StringComparison.OrdinalIgnoreCase) && p.Operator == "=" && p.Value is string,
                }
                : BqlSourceCapabilities.None;
        }

        public BqlSchema Schema => FileSourceTests.Schema;

        public BqlSourceCapabilities Capabilities { get; }

        public int Reads { get; private set; }

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Reads++;
            IEnumerable<FileRecord> files = Files;
            if (_exact)
            {
                if (pushdown.StartTime is { } start)
                    files = files.Where(f => f.Modified >= start);
                if (pushdown.EndTime is { } end)
                    files = files.Where(f => f.Modified <= end);
                if (pushdown.TextTerms is { } terms)
                    files = files.Where(f => SearchTerms.Matches(f.Name, terms));
                foreach (var p in pushdown.Predicates)
                    files = files.Where(f => string.Equals(f.Ext, (string)p.Value!, StringComparison.OrdinalIgnoreCase));
                files = pushdown.Sort is { Descending: false } ? files.OrderBy(f => f.Modified) : files.OrderByDescending(f => f.Modified);
            }

            if (pushdown.AllowedDatasets is { } allowed)
                files = files.Where(f => allowed.Contains(f.Vault, StringComparer.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Schema.NewRow();
                row["path"] = file.Path;
                row["name"] = file.Name;
                row["ext"] = file.Ext;
                row["size"] = file.Size;
                row["mime"] = file.Mime;
                row["hash"] = file.Hash;
                row["tags"] = file.Tags;
                row["modified"] = file.Modified;
                row["created"] = file.Modified.AddDays(-1);
                row["protection"] = file.Protection;
                row["vault"] = file.Vault;
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query, bool exact = true, BqlExecutionContext? context = null)
    {
        var engine = new BqlQueryEngine(new FileSource(exact));
        context ??= new BqlExecutionContext { Query = query };
        context.Query = query;
        var result = await engine.ExecuteAsync(context);
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    public static TheoryData<bool> BothSources => new() { true, false };

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Stats_ByAField_Counts(bool exact)
    {
        var rows = await RunAsync("find * | stats count, sum(size) as total by mime | sort -count, mime", exact);

        rows[0]["mime"].Should().Be("application/pdf");
        Convert.ToInt64(rows[0]["count"]).Should().Be(2);
        rows.Should().HaveCount(4);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Where_OnNumbersAndAliases(bool exact)
    {
        var rows = await RunAsync("find * | where bytes > 5000000 and extension != \"flac\" | table path", exact);

        rows.Select(r => r["path"]).Should().BeEquivalentTo(["/photos/beach.jpg", "/photos/beach-copy.jpg"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Where_OnAList_HoldsWhenAnyValueMatches(bool exact)
    {
        var rows = await RunAsync("find * | where tags = \"photo\" | table name", exact);

        rows.Select(r => r["name"]).Should().BeEquivalentTo(["beach.jpg", "beach-copy.jpg"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task NotEquals_OnAList_HoldsWhenNoValueMatches_AndAnEmptyListIsMissing(bool exact)
    {
        var rows = await RunAsync("find * | where tags != \"photo\" | table name", exact);
        var names = rows.Select(r => (string)r["name"]!).ToList();

        names.Should().Contain(["taxes-2025.pdf", "lease.pdf"]);
        names.Should().NotContain(["beach.jpg", "beach-copy.jpg", "song.flac"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Words_SearchTheSchemasTextField(bool exact)
    {
        var rows = await RunAsync("find beach | table name", exact);

        rows.Select(r => r["name"]).Should().BeEquivalentTo(["beach.jpg", "beach-copy.jpg"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Rex_WithNoField_ReadsTheTextField(bool exact)
    {
        var rows = await RunAsync("find * | rex \"^(?<stem>[a-z]+)-\" | where isnotnull(stem) | table stem", exact);

        rows.Select(r => r["stem"]).Should().BeEquivalentTo(["taxes", "beach"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task TimeRange_ReadsTheSchemasTimeField(bool exact)
    {
        var rows = await RunAsync("find * earliest=2026-03-03T00:00:00Z latest=2026-03-05T00:00:00Z | table name", exact);

        rows.Select(r => r["name"]).Should().BeEquivalentTo(["lease.pdf", "beach.jpg", "beach-copy.jpg"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Timechart_BucketsByTheSchemasTimeField(bool exact)
    {
        var rows = await RunAsync("find * | timechart span=2d count", exact);

        rows.Should().OnlyContain(r => r.ContainsKey("modified"), "buckets go in the schema's time field");
        rows.Sum(r => Convert.ToInt64(r["count"])).Should().Be(Files.Length);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task DuplicateContent_FoundByHash(bool exact)
    {
        var rows = await RunAsync("find * | stats count, values(path) as paths by hash | where count > 1", exact);

        rows.Should().ContainSingle().Which["hash"].Should().Be("c3");
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task OrdinalLevels_ComeFromTheSchema(bool exact)
    {
        var rows = await RunAsync("find * | where protection >= mirrored | table name", exact);

        rows.Select(r => r["name"]).Should().BeEquivalentTo(["taxes-2025.pdf", "lease.pdf", "song.flac"]);
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task MultiValueTags(bool exact)
    {
        var rows = await RunAsync("find * | mvexpand tags | stats count by tags | sort -count | head 1", exact);

        rows.Single()["tags"].Should().Be("photo");
    }

    [Theory]
    [MemberData(nameof(BothSources))]
    public async Task Dataset_ScopesTheQuery(bool exact)
    {
        var rows = await RunAsync("find * | table name", exact, new BqlExecutionContext { Query = "", Dataset = "work" });

        rows.Select(r => r["name"]).Should().Equal("todo.md");
    }

    /// <summary>A source that sorts by time is asked for the schema's default order.</summary>
    [Fact]
    public async Task DefaultOrder_IsAskedOfASourceThatSorts()
    {
        var rows = await RunAsync("find * | head 2 | table name", exact: true);

        rows.Select(r => r["name"]).Should().Equal("todo.md", "song.flac");
    }

    [Fact]
    public async Task ExactPredicates_LeaveThePipeline()
    {
        var engine = new BqlQueryEngine(new FileSource(exact: true));

        var explained = engine.Explain(new BqlExecutionContext { Query = "find * | where ext = \"pdf\" | stats count" });

        explained.Pushdown.Predicates.Should().ContainSingle().Which.Should().Be(new BqlPredicate("ext", "=", "pdf"));
        explained.PipelineStages.Should().Equal("stats");
    }

    [Fact]
    public async Task WithoutCapabilities_NothingLeavesThePipeline()
    {
        var engine = new BqlQueryEngine(new FileSource(exact: false));

        var explained = engine.Explain(new BqlExecutionContext { Query = "find report | where ext = \"pdf\" | stats count" });

        explained.Pushdown.Predicates.Should().BeEmpty();
        explained.Pushdown.TextTerms.Should().BeNull();
        explained.PipelineStages.Should().Equal("search", "where", "stats");
        (await RunAsync("find report | where ext = \"pdf\" | stats count", exact: false)).Single()["count"].Should().Be(0L);
    }

    [Fact]
    public async Task Head_StopsReading()
    {
        var source = new FileSource(exact: false);
        var engine = new BqlQueryEngine(source);

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = "find * | head 1" });

        result.Events.Should().ContainSingle();
        result.Statistics!.EventsScanned.Should().Be(1);
    }

    [Fact]
    public async Task MakeResults_WritesTheSchemasFields()
    {
        var rows = await RunAsync("| makeresults count=1");

        rows.Single().Should().ContainKeys("modified", "name");
    }
}
