using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>
/// Plain questions about files, read with a host's vocabulary: words that stand for conditions
/// ("photos", "pdfs", "duplicates") and words that rank ("largest", "smallest").
/// </summary>
public sealed class FileQuestionTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    private static readonly BqlSchema Schema = new()
    {
        TimeField = "modified",
        TextField = "text",
        DefaultSort = new BqlSortOrder("modified", Descending: true),
    };

    private static BqlVocabulary FileWords()
    {
        var words = BqlVocabulary.CreateNeutral("modified");
        words.FieldWords["size"] = ["size"];
        words.Conditions["photos"] = "type like \"image/%\"";
        words.Conditions["pdfs"] = "ext = \"pdf\"";
        words.Conditions["duplicates"] = "copies > 1";
        words.Conditions["documents"] = "ext in (\"pdf\", \"docx\", \"md\")";
        words.Conditions["music files"] = "type like \"audio/%\"";
        words.Rankings["largest"] = ("size", true);
        words.Rankings["biggest"] = ("size", true);
        words.Rankings["smallest"] = ("size", false);
        return words;
    }

    private static readonly FieldCatalog Catalog = new(
        ["path", "name", "ext", "folder", "size", "type", "modified", "copies"],
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ext"] = ["pdf", "jpg", "docx", "md", "flac"],
            ["folder"] = ["docs", "photos", "notes", "music"],
        });

    private static QueryUnderstanding Understand(string question, BqlVocabulary? words = null) =>
        new QueryUnderstander(words ?? FileWords(), Schema).Understand(question, Catalog, Now);

    public static TheoryData<string, string> Readings => new()
    {
        { "largest pdfs", "find * | where (ext = \"pdf\") | sort by size desc" },
        { "the 5 biggest photos", "find * | where (type like \"image/%\") | sort by size desc | head 5" },
        { "the 10 largest pdfs", "find * | where (ext = \"pdf\") | sort by size desc | head 10" },
        { "top 3 smallest documents", "find * | where (ext in (\"pdf\", \"docx\", \"md\")) | sort by size asc | head 3" },
        { "photos from this week", "find * earliest=@w | where (type like \"image/%\")" },
        { "this week's PDFs", "find * earliest=@w | where (ext = \"pdf\")" },
        { "duplicates over 1 MB", "find * | where (copies > 1) and size > 1048576" },
        { "smallest documents modified yesterday", "find * earliest=-1d@d latest=@d | where (ext in (\"pdf\", \"docx\", \"md\")) | sort by size asc" },
        { "pdfs that are not duplicates", "find * | where (ext = \"pdf\") and NOT (copies > 1)" },
        { "largest music files", "find * | where (type like \"audio/%\") | sort by size desc" },
        { "photos modified since monday", "find * earliest=2026-11-09T00:00:00Z | where (type like \"image/%\")" },
        { "how many photos by folder", "find * | where (type like \"image/%\") | stats count by folder | sort by count desc" },
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void Questions_ReadAsExpected(string question, string expected)
    {
        var reading = Understand(question);

        reading.WillRun.Should().BeTrue($"\"{question}\" should run: {string.Join("; ", reading.Problems.Select(p => p.Message))}");
        reading.Kind.Should().Be(UnderstandingKind.Natural);
        reading.Query.Should().Be(expected);
    }

    [Fact]
    public void AWordBeforeAField_IsReadAsBefore()
    {
        // "largest folders" ranks folders by a measure, as it did before the host added rankings
        var without = BqlVocabulary.CreateNeutral("modified");
        without.FieldWords["size"] = ["size"];

        Understand("largest folders").Query.Should().Be(Understand("largest folders", without).Query);
        Understand("biggest size by folder").Query.Should().Be(Understand("biggest size by folder", without).Query);
    }

    [Fact]
    public void TheSharedVocabularies_HaveNoConditionsOrRankings()
    {
        BqlVocabulary.Logs.Conditions.Should().BeEmpty();
        BqlVocabulary.Logs.Rankings.Should().BeEmpty();
        BqlVocabulary.Neutral.Conditions.Should().BeEmpty();
        BqlVocabulary.Neutral.Rankings.Should().BeEmpty();
        BqlVocabulary.CreateNeutral("modified").TimeField.Should().Be("modified");
    }

    [Fact]
    public void ConditionWords_AreNotCorrectedAway()
    {
        // Words the host gave meanings are known words, so typo correction leaves them alone
        var reading = Understand("pdfs");

        reading.Corrections.Should().BeEmpty();
        reading.Query.Should().Be("find * | where (ext = \"pdf\")");
    }

    [Fact]
    public void TheExplanation_SaysTheCondition()
    {
        Understand("largest pdfs").Explanation.Should().Contain("ext = \"pdf\"").And.Contain("highest first");
    }

    [Fact]
    public void AManyWordQuestion_IsReadQuickly()
    {
        var words = FileWords();
        for (var i = 0; i < 200; i++)
        {
            words.Conditions[$"kind{i} things"] = $"kind = {i}";
        }

        var question = string.Join(' ', Enumerable.Repeat("largest kind7 photos", 150));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Understand(question, words);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // ─── The readings run, and find the right files ─────────

    private sealed record FileRecord(string Path, string Type, long Size, int Copies, TimeSpan Age);

    private static readonly FileRecord[] Files =
    [
        new("/docs/taxes.pdf", "application/pdf", 2_400_000, 1, TimeSpan.FromDays(400)),
        new("/docs/lease.pdf", "application/pdf", 900_000, 2, TimeSpan.FromDays(30)),
        new("/docs/scan.pdf", "application/pdf", 7_000_000, 1, TimeSpan.FromDays(2)),
        new("/photos/beach.jpg", "image/jpeg", 5_100_000, 3, TimeSpan.FromHours(1)),
        new("/photos/dog.jpg", "image/jpeg", 300_000, 1, TimeSpan.FromDays(60)),
        new("/notes/todo.md", "text/markdown", 1_200, 2, TimeSpan.FromDays(1)),
        new("/music/song.flac", "audio/flac", 31_000_000, 1, TimeSpan.FromDays(90)),
    ];

    private sealed class FileSource : IBqlSource
    {
        public BqlSchema Schema => FileQuestionTests.Schema;

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            foreach (var file in Files)
            {
                var row = Schema.NewRow();
                row["path"] = file.Path;
                row["name"] = System.IO.Path.GetFileName(file.Path);
                row["ext"] = System.IO.Path.GetExtension(file.Path).TrimStart('.');
                row["folder"] = file.Path.Split('/')[1];
                row["type"] = file.Type;
                row["size"] = file.Size;
                row["copies"] = (long)file.Copies;
                row["modified"] = now - file.Age;
                row["text"] = file.Path;
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("largest pdfs", new[] { "scan.pdf", "taxes.pdf", "lease.pdf" })]
    [InlineData("the 1 biggest photos", new[] { "beach.jpg" })]
    [InlineData("duplicates over 1 MB", new[] { "beach.jpg" })]
    [InlineData("smallest documents", new[] { "todo.md", "lease.pdf", "taxes.pdf", "scan.pdf" })]
    [InlineData("pdfs that are not duplicates", new[] { "scan.pdf", "taxes.pdf" })]
    public async Task Readings_FindTheRightFiles(string question, string[] names)
    {
        var reading = new QueryUnderstander(FileWords(), Schema).Understand(question, Catalog);
        var engine = new BqlQueryEngine(new FileSource());

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = reading.Query! });
        var found = result.Events.Select(e => (string)((IDictionary<string, object?>)e)["name"]!).ToList();

        if (reading.Query!.Contains("sort", StringComparison.Ordinal))
        {
            found.Should().Equal(names);
        }
        else
        {
            found.Should().BeEquivalentTo(names);
        }
    }
}
