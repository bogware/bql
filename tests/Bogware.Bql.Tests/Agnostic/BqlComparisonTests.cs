using System.Runtime.CompilerServices;

namespace Bogware.Bql.Tests.Agnostic;

/// <summary>
/// <see cref="BqlComparison.Holds"/> must say exactly what <c>where field op literal</c> says, for
/// every kind of value a row can hold, or a source that uses it to apply predicates would drift.
/// </summary>
public sealed class BqlComparisonTests
{
    private static readonly DateTime March = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One row per value, each with an id, the value under <c>v</c> unless it is missing.</summary>
    private static readonly object?[] Values =
    [
        Missing.Value,
        null,
        5L,
        5,
        5.0,
        5.0000000001,
        4.9,
        -3.5,
        0L,
        "5",
        " 5",
        "5.0",
        "abc",
        "ABC",
        "abd",
        "",
        "b",
        true,
        false,
        March,
        March.AddSeconds(1),
        "2026-03-01T00:00:00Z",
        "2026-03-01",
        "March 1 2026",
        new DateTimeOffset(March).ToUnixTimeSeconds(),
        new[] { "a", "b" },
        new[] { "abc", "x" },
        Array.Empty<string>(),
        new List<object?> { null },
        new List<object?> { 1L, 9L },
        new List<object?> { "5", null },
        new object?[] { March, "x" },
    ];

    private static readonly string[] Literals =
    [
        "5", "5.0", "5.0000000001", "4.9", "0", "\"5\"", "\"abc\"", "\"ABC\"", "\"b\"", "\"\"",
        "\"2026-03-01\"", "\"2026-03-01T00:00:00Z\"", "1772323200", "true", "false", "null", "\"x\"", "9",
    ];

    // The parser has no ==; Holds takes it as =, as BqlPredicate does
    private static readonly string[] Ops = ["=", "!=", "<>", ">", ">=", "<", "<="];

    private sealed class Missing
    {
        public static readonly Missing Value = new();
    }

    private sealed class ValueSource : IBqlSource
    {
        public BqlSchema Schema { get; } = new() { TimeField = "t", TextField = "text" };

        public BqlSourceCapabilities Capabilities => BqlSourceCapabilities.None;

        public async IAsyncEnumerable<BqlRow> ReadAsync(BqlPushdown pushdown, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < Values.Length; i++)
            {
                var row = Schema.NewRow();
                row["id"] = (long)i;
                if (Values[i] is not Missing)
                {
                    row["v"] = Values[i];
                }

                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var op in Ops)
        {
            foreach (var literal in Literals)
            {
                data.Add(op, literal);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Holds_AgreesWithWhere(string op, string literal)
    {
        var source = new ValueSource();
        var engine = new BqlQueryEngine(source);
        var query = $"* | where v {op} {literal} | table id";

        var parsed = new BqlParser(source.Schema).ParseBql(query);
        var right = ((LiteralExpression)((ComparisonExpression)parsed.Stages.OfType<WhereStage>().Single().Condition).Right).Value;

        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = query, Limit = 1000 });
        var kept = result.Events.Select(e => (long)((IDictionary<string, object?>)e)["id"]!).ToHashSet();

        for (var i = 0; i < Values.Length; i++)
        {
            var left = Values[i] is Missing ? null : Values[i];
            BqlComparison.Holds(op, left, right).Should().Be(kept.Contains(i), $"v = {Describe(Values[i])} {op} {literal}");
        }
    }

    [Theory]
    [InlineData("~")]
    [InlineData("like")]
    [InlineData("")]
    [InlineData("=>")]
    public void Holds_RefusesWhatIsNotAComparison(string op)
    {
        var act = () => BqlComparison.Holds(op, 1, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Holds_FollowsTheDocumentedRules()
    {
        BqlComparison.Holds("=", "PDF", "pdf").Should().BeTrue("text ignores case");
        BqlComparison.Holds("!=", null, "x").Should().BeFalse("a missing value never matches, != included");
        BqlComparison.Holds("=", new[] { "tax", "home" }, "tax").Should().BeTrue("a list matches when any value does");
        BqlComparison.Holds("!=", new[] { "tax", "home" }, "tax").Should().BeFalse("!= on a list holds when no value equals");
        BqlComparison.Holds("!=", Array.Empty<string>(), "tax").Should().BeFalse("an empty list is missing");
        BqlComparison.Holds("=", 0.1 + 0.2, 0.3).Should().BeTrue("numbers are equal within a tolerance");
        BqlComparison.Holds(">", "10", 9L).Should().BeTrue("numeric text compares as a number");
        BqlComparison.Holds(">=", "2026-03-02", March).Should().BeTrue("date text compared with a time is a time");
        BqlComparison.Holds("<", March, "2026-03-02").Should().BeTrue();
        BqlComparison.Holds("==", "a", "A").Should().BeTrue("== is =");
    }

    private static string Describe(object? value) => value switch
    {
        Missing => "(missing)",
        null => "null",
        string s => $"\"{s}\"",
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Describe)) + "]",
        _ => $"{value} ({value.GetType().Name})",
    };
}
