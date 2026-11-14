using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bogware.Bql;

/// <summary>
/// Recursive-descent parser for BQL. It reads SPL as well: <c>search</c>, <c>sort -field</c>,
/// <c>rex field=</c>, <c>earliest=</c> and the rest. One parser can be shared by any number of
/// threads; each call parses with its own state.
/// </summary>
public sealed class BqlParser
{
    private readonly BqlSchema _schema;
    private readonly BqlParserLimits _limits;

    // False when parsing a condition rewritten from search text, where SPL reads 10MB and -7d as text
    private readonly bool _unitLiterals;
    private List<BqlToken> _tokens = new();
    private string _source = string.Empty;
    private int _pos;
    private int _recursionDepth;

    /// <summary>A parser for rows of the given schema, which names ordered fields such as a severity.</summary>
    public BqlParser(BqlSchema? schema = null, BqlParserLimits? limits = null)
        : this(schema ?? BqlSchema.Default, limits ?? new BqlParserLimits(), unitLiterals: true)
    {
    }

    private BqlParser(BqlSchema schema, BqlParserLimits limits, bool unitLiterals)
    {
        _schema = schema;
        _limits = limits;
        _unitLiterals = unitLiterals;
    }

    /// <summary>
    /// A parser that reads sizes such as <c>10MB</c> and relative times such as <c>-7d</c> as
    /// they were before, which is to say not at all. The optimizer uses it for conditions written
    /// in search text, whose bare values SPL reads as text.
    /// </summary>
    internal BqlParser WithoutUnitLiterals() => new(_schema, _limits, unitLiterals: false);

    /// <summary>The schema this parser reads level names with.</summary>
    public BqlSchema Schema => _schema;

    /// <summary>The limits this parser applies.</summary>
    public BqlParserLimits Limits => _limits;

    private int MaxRecursionDepth => _limits.MaxRecursionDepth;
    private int MaxExpressionDepth => _limits.MaxExpressionDepth;
    private int MaxSubsearchDepth => _limits.MaxSubsearchDepth;
    private int MaxQueryLength => _limits.MaxQueryLength;
    private int MaxPipelineStages => _limits.MaxPipelineStages;

    /// <summary>
    /// Parse a BQL or SPL query string into a BQL AST. Safe to call from many threads at once.
    /// </summary>
    public BqlQuery ParseBql(string query)
    {
        try
        {
            return new BqlParser(_schema, _limits, _unitLiterals).ParseBqlCore(query);
        }
        catch (Exception ex) when (ex is not BqlParseException && !BqlFailures.PassesThrough(ex))
        {
            // Whatever the text, a caller gets a parse error, never a raw .NET exception.
            throw new BqlParseException("The query could not be read.", ex);
        }
    }

    private BqlQuery ParseBqlCore(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new BqlParseException("Query cannot be empty");

        if (query.Length > MaxQueryLength)
            throw new BqlParseException($"Query exceeds maximum length of {MaxQueryLength} characters");

        var lexer = new BqlLexer(query, _limits.MaxTokenCount, _limits.MaxStringLiteralLength);
        _tokens = lexer.Tokenize();
        _source = query;
        _pos = 0;
        _recursionDepth = 0;

        var bqlQuery = new BqlQuery();

        // Parse the first stage (search/find or implicit search)
        var firstStage = ParseFirstStage(bqlQuery);
        if (firstStage != null)
            bqlQuery.Stages.Add(firstStage);

        // Parse piped stages
        while (Current.Kind == BqlTokenKind.Pipe)
        {
            if (bqlQuery.Stages.Count >= MaxPipelineStages)
                throw new BqlParseException($"Query exceeds maximum of {MaxPipelineStages} pipeline stages");

            Advance(); // skip pipe
            var stage = ParseStage();
            bqlQuery.Stages.Add(stage);

            if (Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
                ParseFromLast(bqlQuery);
        }

        if (Current.Kind != BqlTokenKind.Eof)
            throw new BqlParseException($"Unexpected token: {Current.Value}", $"position {Current.Position}");

        return bqlQuery;
    }

    // ─── First stage parsing ─────────────────────────────────

    private BqlStage? ParseFirstStage(BqlQuery query)
    {
        // Empty or just EOF
        if (Current.Kind == BqlTokenKind.Eof)
            return new SearchStage("*");

        // Explicit find/search keyword
        if (Current.IsAnyKeyword("find", "search"))
        {
            Advance();
            return ParseSearchBody(query);
        }

        // Check if first token is a known stage keyword
        if (IsStageKeyword(Current))
        {
            // Implicit "search *" followed by explicit stage
            return new SearchStage("*");
        }

        // Implicit search: everything before the first pipe is the search text
        return ParseImplicitSearch(query);
    }

    private SearchStage ParseSearchBody(BqlQuery query)
    {
        var sb = new StringBuilder();
        var parenDepth = 0;

        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            // Track parens to avoid breaking on pipes inside expressions
            if (Current.Kind == BqlTokenKind.OpenParen) parenDepth++;
            if (Current.Kind == BqlTokenKind.CloseParen) parenDepth--;

            // Check for time range modifiers: "from last Xh" or "between X and Y"
            if (parenDepth == 0 && Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
            {
                ParseFromLast(query);
                continue;
            }

            if (parenDepth == 0 && Current.IsKeyword("between"))
            {
                ParseBetween(query);
                continue;
            }

            AppendSearchToken(sb);
        }

        return new SearchStage(TakeTimeModifiers(query, sb.ToString()));
    }

    private static readonly System.Text.RegularExpressions.Regex TimeModifier = new(
        @"(?<![\w.])(?<which>earliest|latest)=(?<value>""[^""]*""|\S+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Takes SPL's <c>earliest=-1d@d latest=now</c> out of search text as the query's time range,
    /// and returns the rest of the text.
    /// </summary>
    private static string TakeTimeModifiers(BqlQuery query, string text)
    {
        var rest = TimeModifier.Replace(text, match =>
        {
            var value = match.Groups["value"].Value.Trim('"');
            var now = DateTime.UtcNow;
            var time = value.Equals("now", StringComparison.OrdinalIgnoreCase) ? now
                : value == "0" ? DateTime.UnixEpoch
                : BqlTime.ApplyRelative(now, value) ?? BqlTime.Parse(value, null)
                    ?? throw new BqlParseException($"'{value}' isn't a time: use one such as -24h, -1d@d, now or 2026-03-04T05:00:00Z");
            query.TimeRange ??= new BqlTimeRange();
            if (match.Groups["which"].Value.Equals("earliest", StringComparison.OrdinalIgnoreCase))
                query.TimeRange.Start = time;
            else
                query.TimeRange.End = time;
            return string.Empty;
        }).Trim();
        return rest.Length > 0 ? rest : "*";
    }

    private SearchStage ParseImplicitSearch(BqlQuery query)
    {
        var sb = new StringBuilder();

        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            // Check for time range modifiers
            if (Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
            {
                ParseFromLast(query);
                continue;
            }

            if (Current.IsKeyword("between"))
            {
                ParseBetween(query);
                continue;
            }

            AppendSearchToken(sb);
        }

        return new SearchStage(TakeTimeModifiers(query, sb.ToString()));
    }

    // Spaced as typed, so host=game-dev stays one comparison rather than becoming host = l4d2 - dev
    private void AppendSearchToken(StringBuilder sb)
    {
        var position = Current.Position;
        var spaced = position <= 0 || position > _source.Length || char.IsWhiteSpace(_source[position - 1]);
        if (sb.Length > 0 && spaced) sb.Append(' ');
        sb.Append(Current.Kind == BqlTokenKind.StringLiteral ? $"\"{Current.Value}\"" : Current.Value);
        Advance();
    }

    private void ParseFromLast(BqlQuery query)
    {
        Advance(); // skip "from"
        Advance(); // skip "last"

        if (Current.Kind != BqlTokenKind.TimeSpanLiteral)
            throw new BqlParseException("Expected time span after 'from last' (e.g., 24h, 7d, 30m)", $"position {Current.Position}");

        var ts = ParseTimeSpanValue(Current.Value) ?? TimeSpan.FromHours(24);
        query.TimeRange = new BqlTimeRange { Start = DateTime.UtcNow - ts, End = DateTime.UtcNow };
        Advance();
    }

    private void ParseBetween(BqlQuery query)
    {
        Advance(); // skip "between"

        var start = ParseDateTimeLiteral();
        ExpectKeyword("and");
        var end = ParseDateTimeLiteral();

        query.TimeRange = new BqlTimeRange { Start = start, End = end };
    }

    // ─── Stage parsing ───────────────────────────────────────

    private BqlStage ParseStage()
    {
        if (Current.Kind == BqlTokenKind.Eof)
            throw new BqlParseException("Expected a stage after pipe operator");

        // BQL-style keywords
        if (Current.IsKeyword("where")) { Advance(); return ParseWhereStage(); }
        if (Current.IsAnyKeyword("group")) return ParseGroupByStage();
        if (Current.IsAnyKeyword("sort")) return ParseSortStage();
        if (Current.IsAnyKeyword("limit")) { Advance(); return ParseLimitStage(); }
        if (Current.IsAnyKeyword("compute")) { Advance(); return ParseEvalStage(); }
        if (Current.IsAnyKeyword("extract")) { Advance(); return ParseRexStage(); }
        if (Current.IsAnyKeyword("select")) { Advance(); return ParseTableStage(); }
        if (Current.IsAnyKeyword("unique")) return ParseUniqueByStage();
        if (Current.IsKeyword("rename")) { Advance(); return ParseRenameStage(); }
        if (Current.IsKeyword("fill")) { Advance(); return ParseFillStage(); }
        if (Current.IsKeyword("expand")) { Advance(); return ParseExpandStage(); }
        if (Current.IsKeyword("fillnull")) { Advance(); return ParseFillNullStage(); }
        if (Current.IsAnyKeyword("bucket", "bin")) { Advance(); return ParseBucketStage(); }
        if (Current.IsKeyword("makeresults")) { Advance(); return ParseMakeResultsStage(); }
        if (Current.IsKeyword("mvexpand")) { Advance(); return ParseMvExpandStage(); }
        if (Current.IsKeyword("chart")) { Advance(); return ParseChartStage(); }
        if (Current.IsKeyword("filldown")) { Advance(); return ParseFillDownStage(); }
        if (Current.IsKeyword("addcoltotals")) { Advance(); return ParseAddColTotalsStage(); }
        if (Current.IsKeyword("inputlookup")) { Advance(); return ParseInputLookupStage(); }
        if (Current.IsKeyword("rare")) { Advance(); return ParseRareStage(); }
        if (Current.IsKeyword("regex")) { Advance(); return ParseRegexStage(); }
        if (Current.IsKeyword("fieldsummary")) { Advance(); return ParseFieldSummaryStage(); }
        if (Current.IsKeyword("xyseries")) { Advance(); return ParseXyseriesStage(); }
        if (Current.IsKeyword("untable")) { Advance(); return ParseUntableStage(); }
        if (Current.IsKeyword("convert")) { Advance(); return ParseConvertStage(); }
        if (Current.IsKeyword("iplocation")) { Advance(); return ParseIplocationStage(); }
        if (Current.IsKeyword("cluster")) { Advance(); return ParseClusterStage(); }

        // SPL-style keywords
        if (Current.IsKeyword("eventstats")) { Advance(); return ParseEventStatsStage(); }
        if (Current.IsKeyword("streamstats")) { Advance(); return ParseStreamStatsStage(); }
        if (Current.IsKeyword("stats")) { Advance(); return ParseStatsStage(); }
        if (Current.IsKeyword("eval")) { Advance(); return ParseEvalStage(); }
        if (Current.IsKeyword("rex")) { Advance(); return ParseRexStage(); }
        if (Current.IsKeyword("head")) { Advance(); return ParseLimitStage(); }
        if (Current.IsKeyword("tail")) { Advance(); return ParseTailStage(); }
        if (Current.IsAnyKeyword("table", "fields")) { Advance(); return ParseTableStage(); }
        if (Current.IsKeyword("dedup")) { Advance(); return ParseDedupStage(); }
        if (Current.IsKeyword("top")) { Advance(); return ParseTopStage(); }
        if (Current.IsKeyword("timechart")) { Advance(); return ParseTimechartStage(); }
        if (Current.IsKeyword("fieldalias")) { Advance(); return ParseFieldAliasStage(); }
        if (Current.IsKeyword("transaction")) { Advance(); return ParseTransactionStage(); }
        if (Current.IsKeyword("lookup")) { Advance(); return ParseLookupStage(); }
        if (Current.IsKeyword("append")) { Advance(); return ParseAppendStage(); }
        if (Current.IsKeyword("join")) { Advance(); return ParseJoinStage(); }
        if (Current.IsKeyword("macro")) { Advance(); return ParseMacroStage(); }
        if (Current.IsKeyword("subsearch")) { Advance(); return ParseSubsearchStage(); }
        if (Current.IsKeyword("multisearch")) { Advance(); return ParseMultisearchStage(); }
        if (Current.IsAnyKeyword("search", "find")) { Advance(); return ParseSearchStageInPipeline(); }

        throw new BqlParseException($"Unknown stage: {Current.Value}", $"position {Current.Position}");
    }

    // ─── WHERE (with AND/OR/NOT/parentheses) ─────────────────

    private WhereStage ParseWhereStage()
    {
        var expr = ParseOrExpression();
        return new WhereStage(expr);
    }

    private BqlExpression ParseOrExpression()
    {
        var left = ParseAndExpression();

        while (Current.IsKeyword("or"))
        {
            Advance();
            var right = ParseAndExpression();
            left = Bounded(new LogicalExpression(left, "OR", right));
        }

        return left;
    }

    private BqlExpression ParseAndExpression()
    {
        var left = ParseNotExpression();

        while (Current.IsKeyword("and"))
        {
            Advance();
            var right = ParseNotExpression();
            left = Bounded(new LogicalExpression(left, "AND", right));
        }

        return left;
    }

    private BqlExpression ParseNotExpression()
    {
        // In a loop: recursing here would let a long chain of "not" overflow the stack.
        var negations = 0;
        while (Current.IsKeyword("not"))
        {
            Advance();
            negations++;
        }

        var expression = ParseComparison();
        return Bounded(negations % 2 == 0 ? negations == 0 ? expression : new NotExpression(new NotExpression(expression)) : new NotExpression(expression));
    }

    private BqlExpression ParseComparison()
    {
        var left = ParseAdditive();

        // IN expression
        if (Current.IsKeyword("in"))
        {
            Advance();
            Expect(BqlTokenKind.OpenParen, "(");
            var values = new List<BqlExpression>();
            values.Add(LevelLiteral(left, ParsePrimary()));
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                values.Add(LevelLiteral(left, ParsePrimary()));
            }
            Expect(BqlTokenKind.CloseParen, ")");
            return Bounded(new InExpression(left, values));
        }

        // LIKE expression
        if (Current.IsKeyword("like"))
        {
            Advance();
            if (Current.Kind != BqlTokenKind.StringLiteral)
                throw new BqlParseException("Expected string pattern after LIKE", $"position {Current.Position}");
            var pattern = Current.Value;
            Advance();
            return Bounded(new LikeExpression(left, pattern));
        }

        // Comparison operators
        if (IsComparisonOperator(Current.Kind))
        {
            var op = Current.Value;
            Advance();
            var right = ParseAdditive();
            return Bounded(new ComparisonExpression(left, op, LevelLiteral(left, right)));
        }

        return left;
    }

    private BqlExpression ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.Kind is BqlTokenKind.Plus or BqlTokenKind.Minus or BqlTokenKind.Dot)
        {
            var op = Current.Value;
            Advance();
            var right = ParseMultiplicative();
            left = Bounded(new ArithmeticExpression(left, op, right));
        }

        return left;
    }

    private BqlExpression ParseMultiplicative()
    {
        var left = ParseUnary();

        while (Current.Kind is BqlTokenKind.Star or BqlTokenKind.Slash or BqlTokenKind.Percent)
        {
            var op = Current.Value;
            Advance();
            var right = ParseUnary();
            left = Bounded(new ArithmeticExpression(left, op, right));
        }

        return left;
    }

    private BqlExpression ParseUnary()
    {
        if (RelativeTimeLiteral() is { } relative)
        {
            return relative;
        }

        if (Current.Kind == BqlTokenKind.Minus)
        {
            Advance();
            var operand = ParsePrimary();
            return Bounded(new ArithmeticExpression(new LiteralExpression(0), "-", operand));
        }

        return ParsePrimary();
    }

    // A fixed, anchored pattern with no nested repetition; the timeout only guards
    private static readonly Regex RelativeTime = new(
        @"\G(?:[+-]\d+[smhdwMy](?:@[smhdwMy])?|@[smhdwMy])(?![\w@])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// <c>-7d</c>, <c>+1h</c>, <c>-1d@d</c> or <c>@w</c> where an operand starts: a time, from now
    /// when the query is parsed, as <c>earliest=</c> reads it. A sign or a snap is required, so
    /// <c>7d</c> alone is not one, and <c>x - 7</c> and <c>x-7d</c> are arithmetic as before.
    /// </summary>
    private LiteralExpression? RelativeTimeLiteral()
    {
        if (!_unitLiterals || Current.Kind is not (BqlTokenKind.Minus or BqlTokenKind.Plus or BqlTokenKind.Symbol)
            || Current.Position < 0 || RelativeTime.Match(_source, Current.Position) is not { Success: true } match)
        {
            return null;
        }

        var at = Current.Position;
        var end = match.Index + match.Length;
        var time = BqlTime.ApplyRelative(DateTime.UtcNow, match.Value)
            ?? throw new BqlParseException($"'{match.Value}' is outside the times BQL can hold", $"position {at}");

        while (Current.Kind != BqlTokenKind.Eof && Current.Position < end)
        {
            Advance();
        }

        return new LiteralExpression(time);
    }

    /// <summary>
    /// A number written right against a size unit, as in <c>10MB</c>, is that many bytes. Units
    /// count in 1024s, as file managers on Windows show sizes. Null when no unit follows.
    /// </summary>
    private LiteralExpression? SizeLiteral(string number, int at)
    {
        if (!_unitLiterals || Current.Kind != BqlTokenKind.Identifier || Current.Position != at + number.Length
            || !BqlSizes.TryGetMultiplier(Current.Value, out var multiplier))
        {
            return null;
        }

        var unit = Current.Value;
        Advance();
        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount > long.MaxValue / (decimal)multiplier)
        {
            throw new BqlParseException($"'{number}{unit}' is more bytes than BQL can hold", $"position {at}");
        }

        var bytes = amount * multiplier;
        return bytes == decimal.Truncate(bytes)
            ? new LiteralExpression((long)bytes)
            : new LiteralExpression((double)bytes);
    }

    private BqlExpression Bounded(BqlExpression expression) =>
        expression.Depth <= MaxExpressionDepth
            ? expression
            : throw new BqlParseException($"Expression too long or too deeply nested (maximum depth: {MaxExpressionDepth}); use IN (...) for a long list of values");

    private BqlExpression ParsePrimary()
    {
        // Parenthesized expression with recursion depth guard
        if (Current.Kind == BqlTokenKind.OpenParen)
        {
            if (++_recursionDepth > MaxRecursionDepth)
                throw new BqlParseException("Query too deeply nested (maximum depth: 100)");
            try
            {
                Advance();
                var expr = ParseOrExpression();
                Expect(BqlTokenKind.CloseParen, ")");
                return expr;
            }
            finally
            {
                _recursionDepth--;
            }
        }

        // String literal
        if (Current.Kind == BqlTokenKind.StringLiteral)
        {
            var val = Current.Value;
            Advance();
            return new LiteralExpression(val);
        }

        // A relative time after an operator that isn't arithmetic, as in IN (-1d@d, @d)
        if (RelativeTimeLiteral() is { } relative)
        {
            return relative;
        }

        // Number literal
        if (Current.Kind == BqlTokenKind.NumberLiteral)
        {
            var val = Current.Value;
            var at = Current.Position;
            Advance();
            if (SizeLiteral(val, at) is { } size)
            {
                return size;
            }

            if (val.Contains('.'))
            {
                return double.TryParse(val, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real)
                    ? new LiteralExpression(real)
                    : throw new BqlParseException($"'{val}' isn't a number BQL can hold", $"position {at}");
            }

            return long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
                ? new LiteralExpression(whole)
                : throw new BqlParseException($"'{val}' is too large; whole numbers go up to {long.MaxValue}", $"position {at}");
        }

        // Star (wildcard)
        if (Current.Kind == BqlTokenKind.Star)
        {
            Advance();
            return new LiteralExpression("*");
        }

        // Boolean literals
        if (Current.IsKeyword("true")) { Advance(); return new LiteralExpression(true); }
        if (Current.IsKeyword("false")) { Advance(); return new LiteralExpression(false); }
        if (Current.IsKeyword("null")) { Advance(); return new LiteralExpression(null); }

        // Identifier — could be field reference or function call
        if (Current.Kind == BqlTokenKind.Identifier)
        {
            var name = Current.Value;
            Advance();

            // Function call: name(args)
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();

                // Arguments nest like parentheses, so they count toward the same limit
                if (++_recursionDepth > MaxRecursionDepth)
                    throw new BqlParseException($"Query too deeply nested (maximum depth: {MaxRecursionDepth})");
                try
                {
                    var args = new List<BqlExpression>();
                    if (Current.Kind != BqlTokenKind.CloseParen)
                    {
                        args.Add(ParseOrExpression());
                        while (Current.Kind == BqlTokenKind.Comma)
                        {
                            Advance();
                            args.Add(ParseOrExpression());
                        }
                    }
                    Expect(BqlTokenKind.CloseParen, ")");
                    return Bounded(new FunctionExpression(name, args));
                }
                finally
                {
                    _recursionDepth--;
                }
            }

            return new FieldExpression(name);
        }

        throw new BqlParseException($"Unexpected token: {Current.Value}", $"position {Current.Position}");
    }

    // ─── STATS / GROUP BY ────────────────────────────────────

    private StatsStage ParseStatsStage()
    {
        var stage = new StatsStage();

        // Parse aggregation functions
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    private StatsStage ParseGroupByStage()
    {
        Advance(); // skip "group"

        // "group by field1, field2" → stats count by field1, field2
        if (Current.IsKeyword("by"))
            Advance();

        var stage = new StatsStage();
        stage.Aggregations.Add(new AggregateFunction("count"));

        stage.GroupByFields.Add(ExpectIdentifier());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
        }

        return stage;
    }

    private AggregateFunction ParseAggregateFunction()
    {
        var name = ExpectIdentifier();

        // Function with field: avg(duration), sum(bytes)
        if (Current.Kind == BqlTokenKind.OpenParen)
        {
            Advance();

            // count(eval(status >= 500)): what an expression gives, row by row
            if (Current.IsKeyword("eval") && Peek(1).Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                Advance();
                var start = Current.Position;
                var expression = ParseOrExpression();
                var text = SourceText(start, Current);
                Expect(BqlTokenKind.CloseParen, ")");
                Expect(BqlTokenKind.CloseParen, ")");
                var evaluated = new AggregateFunction(name, $"eval({text})") { Expression = expression };
                if (Current.IsKeyword("as"))
                {
                    Advance();
                    evaluated.Alias = ExpectIdentifier();
                }

                return evaluated;
            }

            var field = ExpectIdentifier();
            Expect(BqlTokenKind.CloseParen, ")");
            var agg = new AggregateFunction(name, field);

            // Optional alias: "as alias_name"
            if (Current.IsKeyword("as"))
            {
                Advance();
                agg.Alias = ExpectIdentifier();
            }

            return agg;
        }

        // Plain function (no parens): "count"
        var plainAgg = new AggregateFunction(name);
        if (Current.IsKeyword("as"))
        {
            Advance();
            plainAgg.Alias = ExpectIdentifier();
        }

        return plainAgg;
    }

    // ─── SORT ────────────────────────────────────────────────

    private SortStage ParseSortStage()
    {
        Advance(); // skip "sort"
        var stage = new SortStage();

        // BQL style: "sort by field asc/desc"
        if (Current.IsKeyword("by"))
        {
            Advance();
            ParseBqlSortFields(stage);
        }
        else
        {
            // SPL style: "sort ±field1, ±field2"
            ParseSplSortFields(stage);
        }

        return stage;
    }

    private void ParseBqlSortFields(SortStage stage)
    {
        stage.Fields.Add(ParseBqlSortField());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Fields.Add(ParseBqlSortField());
        }
    }

    private SortField ParseBqlSortField()
    {
        var field = ExpectIdentifier();
        var desc = false;

        if (Current.IsKeyword("desc"))
        {
            desc = true;
            Advance();
        }
        else if (Current.IsKeyword("asc"))
        {
            Advance();
        }

        return new SortField(field, desc);
    }

    private void ParseSplSortFields(SortStage stage)
    {
        stage.Fields.Add(ParseSplSortField());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Fields.Add(ParseSplSortField());
        }
    }

    private SortField ParseSplSortField()
    {
        var desc = false;
        if (Current.Kind == BqlTokenKind.Minus)
        {
            desc = true;
            Advance();
        }
        else if (Current.Kind == BqlTokenKind.Plus)
        {
            Advance();
        }

        var field = ExpectIdentifier();
        return new SortField(field, desc);
    }

    // ─── Simple stages ───────────────────────────────────────

    private LimitStage ParseLimitStage()
    {
        var count = ExpectNumber();
        return new LimitStage(count);
    }

    private TailStage ParseTailStage()
    {
        var count = Current.Kind == BqlTokenKind.NumberLiteral ? ExpectNumber() : 10;
        return new TailStage(count);
    }

    private EvalStage ParseEvalStage()
    {
        var assignments = new List<EvalAssignment>();

        var fieldName = ExpectIdentifier();
        Expect(BqlTokenKind.Equals, "=");
        var expression = ParseOrExpression();
        assignments.Add(new EvalAssignment(fieldName, expression));

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            var nextField = ExpectIdentifier();
            Expect(BqlTokenKind.Equals, "=");
            var nextExpr = ParseOrExpression();
            assignments.Add(new EvalAssignment(nextField, nextExpr));
        }

        return new EvalStage(assignments);
    }

    private RexStage ParseRexStage()
    {
        // SPL style: rex field=source "pattern"
        // BQL style: extract "pattern" from source
        string? sourceField = null;

        if (Current.Kind == BqlTokenKind.Identifier && Peek(1).Kind == BqlTokenKind.Equals)
        {
            // SPL: field=source
            var key = Current.Value;
            Advance(); Advance(); // skip "field" and "="
            if (string.Equals(key, "field", StringComparison.OrdinalIgnoreCase))
                sourceField = ExpectIdentifier();
        }

        if (Current.Kind != BqlTokenKind.StringLiteral)
            throw new BqlParseException("Expected regex pattern string", $"position {Current.Position}");

        var pattern = Current.Value;
        ValidatePattern(pattern, "rex");
        Advance();

        // BQL: "from" source
        if (Current.IsKeyword("from"))
        {
            Advance();
            sourceField = ExpectIdentifier();
        }

        return new RexStage(pattern, sourceField);
    }

    private RenameStage ParseRenameStage()
    {
        var mappings = new List<RenameMapping>();

        var oldName = ExpectIdentifier();
        ExpectKeyword("as");
        var newName = ExpectIdentifier();
        mappings.Add(new RenameMapping(oldName, newName));

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            var nextOld = ExpectIdentifier();
            ExpectKeyword("as");
            var nextNew = ExpectIdentifier();
            mappings.Add(new RenameMapping(nextOld, nextNew));
        }

        return new RenameStage(mappings);
    }

    private TableStage ParseTableStage()
    {
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new TableStage(fields);
    }

    private DedupStage ParseDedupStage()
    {
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new DedupStage(fields);
    }

    private BqlStage ParseUniqueByStage()
    {
        Advance(); // skip "unique"
        if (Current.IsKeyword("by"))
            Advance();
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new DedupStage(fields);
    }

    private TopStage ParseTopStage()
    {
        var count = 10;
        if (Current.Kind == BqlTokenKind.NumberLiteral)
            count = ExpectNumber();
        var field = ExpectIdentifier();
        string? byField = null;
        if (Current.IsKeyword("by"))
        {
            Advance();
            byField = ExpectIdentifier();
        }

        return new TopStage(count, field) { ByField = byField };
    }

    private TimechartStage ParseTimechartStage()
    {
        var span = TimeSpan.FromHours(1);
        var aggregation = "count";
        string? byField = null;

        // Parse span=Xh
        if (Current.Kind == BqlTokenKind.Identifier && Current.Value.StartsWith("span", StringComparison.OrdinalIgnoreCase))
        {
            if (Current.Value.Equals("span", StringComparison.OrdinalIgnoreCase) && Peek(1).Kind == BqlTokenKind.Equals)
            {
                Advance(); Advance(); // skip "span" "="
                if (Current.Kind == BqlTokenKind.TimeSpanLiteral)
                {
                    span = ParseTimeSpanValue(Current.Value)
                        ?? throw new BqlParseException($"span={Current.Value} isn't a span timechart can use: it must be above zero and at most ten years", $"position {Current.Position}");
                    Advance();
                }
            }
        }

        // Parse aggregation
        string? aggField = null;
        if (Current.Kind == BqlTokenKind.Identifier && !Current.IsKeyword("by"))
        {
            aggregation = Current.Value.ToLowerInvariant();
            Advance();

            // Function form: count(field)
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                if (Current.Kind != BqlTokenKind.CloseParen)
                {
                    aggField = Current.Value;
                    Advance();
                }
                Expect(BqlTokenKind.CloseParen, ")");
            }
        }

        // Parse BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            byField = ExpectIdentifier();
        }

        return new TimechartStage(span, aggregation, byField, aggField);
    }

    private LookupStage ParseLookupStage()
    {
        // lookup <source> <key_field> [OUTPUT <field1>, <field2>, ...]
        var source = ExpectIdentifier();
        var keyField = ExpectIdentifier();

        var outputFields = new List<string>();
        if (Current.IsKeyword("output"))
        {
            Advance(); // skip "output"
            outputFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                outputFields.Add(ExpectIdentifier());
            }
        }

        return new LookupStage
        {
            LookupSource = source,
            KeyField = keyField,
            OutputFields = outputFields
        };
    }

    /// <summary>
    /// Reads a bracketed subquery, starting at its "[", and returns its text exactly as written:
    /// re-joining tokens would change spacing (host=game-dev) and lose escaped quotes.
    /// </summary>
    private string ReadBracketed(bool skipSearchKeyword = false)
    {
        Advance(); // skip "["
        if (skipSearchKeyword && Current.IsAnyKeyword("search", "find"))
        {
            Advance();
        }

        var start = Current.Kind == BqlTokenKind.Eof ? -1 : Current.Position;
        var depth = 1;
        while (Current.Kind != BqlTokenKind.Eof)
        {
            if (Current.Kind == BqlTokenKind.OpenBracket)
            {
                // Each level runs inside the one around it, so the nesting has to stop somewhere
                if (++depth > MaxSubsearchDepth)
                {
                    throw new BqlParseException($"Subsearches nested too deeply (maximum depth: {MaxSubsearchDepth})");
                }
            }
            else if (Current.Kind == BqlTokenKind.CloseBracket && --depth == 0)
            {
                var text = SourceText(start, Current);
                Advance(); // skip "]"
                return text;
            }

            Advance();
        }

        throw new BqlParseException("Missing ']' to close the subsearch");
    }

    /// <summary>Reads the rest of the stage, up to the next pipe, exactly as written.</summary>
    private string ReadUntilPipe()
    {
        var start = Current.Kind == BqlTokenKind.Eof ? -1 : Current.Position;
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            Advance();
        }

        return SourceText(start, Current);
    }

    private string SourceText(int start, BqlToken end)
    {
        var stop = end.Kind == BqlTokenKind.Eof || end.Position < 0 || end.Position > _source.Length ? _source.Length : end.Position;
        return start < 0 || stop <= start ? string.Empty : _source[start..stop].Trim();
    }

    private AppendStage ParseAppendStage()
    {
        // append [<subsearch>], or the rest of the stage without brackets
        var text = Current.Kind == BqlTokenKind.OpenBracket ? ReadBracketed() : ReadUntilPipe();
        return new AppendStage { SubsearchQuery = text };
    }

    private JoinStage ParseJoinStage()
    {
        // join [type=<inner|outer|left>] <field> [<subsearch>]
        var joinType = "inner";

        // Check for type= parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("type", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "type"
            Advance(); // skip "="
            joinType = ExpectIdentifier().ToLowerInvariant();
        }

        var joinField = ExpectIdentifier();

        var subsearch = Current.Kind == BqlTokenKind.OpenBracket ? ReadBracketed() : ReadUntilPipe();

        return new JoinStage
        {
            JoinField = joinField,
            JoinType = joinType,
            SubsearchQuery = subsearch
        };
    }

    // ─── MACRO stage ──────────────────────────────────────────

    private MacroStage ParseMacroStage()
    {
        // macro <name> [arg1, arg2, ...]
        var macroName = ExpectIdentifier();
        var arguments = new List<string>();

        // Parse optional arguments (comma-separated values or identifiers)
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            string argValue;
            if (Current.Kind == BqlTokenKind.StringLiteral)
            {
                argValue = Current.Value;
                Advance();
            }
            else if (Current.Kind == BqlTokenKind.NumberLiteral)
            {
                argValue = Current.Value;
                Advance();
            }
            else if (Current.Kind == BqlTokenKind.Identifier)
            {
                argValue = Current.Value;
                Advance();
            }
            else
            {
                break;
            }

            arguments.Add(argValue);

            // Skip optional comma separator between arguments
            if (Current.Kind == BqlTokenKind.Comma)
                Advance();
        }

        return new MacroStage(macroName, arguments);
    }

    // ─── SUBSEARCH stage ──────────────────────────────────────

    private SubsearchStage ParseSubsearchStage()
    {
        // subsearch [<inner_query>]
        if (Current.Kind != BqlTokenKind.OpenBracket)
            throw new BqlParseException("Expected '[' after 'subsearch'", $"position {Current.Position}");

        var text = ReadBracketed();
        if (string.IsNullOrWhiteSpace(text))
            throw new BqlParseException("Subsearch query cannot be empty");

        return new SubsearchStage(text);
    }

    // ─── MULTISEARCH stage ──────────────────────────────────────

    private MultisearchStage ParseMultisearchStage()
    {
        // multisearch [search "q1"] [search "q2"] [search "q3"]
        // Each sub-search is enclosed in brackets and prefixed with "search"
        var searchQueries = new List<string>();

        while (Current.Kind == BqlTokenKind.OpenBracket)
        {
            var query = ReadBracketed(skipSearchKeyword: true);
            if (!string.IsNullOrWhiteSpace(query))
            {
                searchQueries.Add(query);
            }
        }

        if (searchQueries.Count == 0)
            throw new BqlParseException("multisearch requires at least one [search \"query\"] clause", $"position {Current.Position}");

        return new MultisearchStage(searchQueries);
    }

    private SearchStage ParseSearchStageInPipeline()
    {
        var sb = new StringBuilder();
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            AppendSearchToken(sb);
        }
        return new SearchStage(sb.Length > 0 ? sb.ToString() : "*");
    }

    private EvalStage ParseFillStage()
    {
        // fill field with value → eval field = coalesce(field, value)
        var field = ExpectIdentifier();
        ExpectKeyword("with");
        var value = ParsePrimary();
        return new EvalStage(field, new FunctionExpression("coalesce", new List<BqlExpression>
        {
            new FieldExpression(field),
            value
        }));
    }

    private EvalStage ParseExpandStage()
    {
        // expand field → placeholder (expand is not easily representable, use eval identity)
        var field = ExpectIdentifier();
        return new EvalStage(field, new FieldExpression(field));
    }

    // ─── Splunk parity stages ────────────────────────────────

    private FillNullStage ParseFillNullStage()
    {
        // fillnull [value=X] [field1, field2, ...]
        var stage = new FillNullStage();

        // Check for value= parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("value", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "value"
            Advance(); // skip "="
            stage.DefaultValue = Current.Kind == BqlTokenKind.StringLiteral || Current.Kind == BqlTokenKind.NumberLiteral || Current.Kind == BqlTokenKind.Identifier
                ? Current.Value : "0";
            Advance();
        }

        // Parse optional field list
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }

        return stage;
    }

    private BucketStage ParseBucketStage()
    {
        // bucket/bin field [span=X] [as alias]
        var stage = new BucketStage { Field = ExpectIdentifier() };

        // Parse optional parameters
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            if (Current.Value.Equals("span", StringComparison.OrdinalIgnoreCase) && Peek(1).Kind == BqlTokenKind.Equals)
            {
                Advance(); Advance(); // skip "span" and "="
                var spanStr = Current.Value;
                Advance();
                // Try time span (1h, 30m, 1d, etc.), then a number
                stage.Span = ParseTimeSpanValue(spanStr);
                if (!stage.Span.HasValue && double.TryParse(spanStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var numSpan) && double.IsFinite(numSpan) && numSpan > 0)
                    stage.NumericSpan = numSpan;
                if (!stage.Span.HasValue && !stage.NumericSpan.HasValue)
                    throw new BqlParseException($"span={spanStr} isn't a span bin can use: give a time like 1h or a number above zero", $"position {Current.Position}");
            }
            else if (Current.IsKeyword("as"))
            {
                Advance();
                stage.Alias = ExpectIdentifier();
            }
            else break;
        }

        return stage;
    }

    private MakeResultsStage ParseMakeResultsStage()
    {
        // makeresults [count=N]
        var stage = new MakeResultsStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("count", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance(); // skip "count" and "="
            if (int.TryParse(Current.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                stage.Count = count; // BqlLimits.MaxMakeResults caps it when it runs
                Advance();
            }
        }
        else if (Current.Kind == BqlTokenKind.NumberLiteral && int.TryParse(Current.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var directCount))
        {
            stage.Count = directCount;
            Advance();
        }
        return stage;
    }

    private MvExpandStage ParseMvExpandStage()
    {
        // mvexpand field [limit=N]
        var stage = new MvExpandStage { Field = ExpectIdentifier() };
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("limit", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            if (int.TryParse(Current.Value, out var limit))
            {
                stage.Limit = limit;
                Advance();
            }
        }
        return stage;
    }

    private ChartStage ParseChartStage()
    {
        // chart agg(field) [OVER field1] [BY field2]
        var stage = new ChartStage();
        // Parse aggregation(s)
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe &&
               !Current.IsAnyKeyword("over", "by"))
        {
            var aggName = Current.Value.ToLowerInvariant();
            Advance();
            string? aggField = null;
            string? alias = null;
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                if (Current.Kind != BqlTokenKind.CloseParen) { aggField = Current.Value; Advance(); }
                Expect(BqlTokenKind.CloseParen, ")");
            }
            if (Current.IsKeyword("as")) { Advance(); alias = ExpectIdentifier(); }
            var agg = new AggregateFunction(aggName, aggField) { Alias = alias };
            stage.Aggregations.Add(agg);
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }
        if (Current.IsKeyword("over")) { Advance(); stage.OverField = ExpectIdentifier(); }
        if (Current.IsKeyword("by")) { Advance(); stage.ByField = ExpectIdentifier(); }
        return stage;
    }

    private FillDownStage ParseFillDownStage()
    {
        // filldown field1, field2, ...
        var stage = new FillDownStage();
        if (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.Fields.Add(ExpectIdentifier());
            }
        }
        return stage;
    }

    private AddColTotalsStage ParseAddColTotalsStage()
    {
        // addcoltotals [fieldname=label] [field1, field2, ...]
        var stage = new AddColTotalsStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("fieldname", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            stage.LabelField = Current.Value;
            Advance();
        }
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }
        return stage;
    }

    private InputLookupStage ParseInputLookupStage()
    {
        // inputlookup <lookup_name>
        return new InputLookupStage { LookupName = ExpectIdentifier() };
    }

    private RareStage ParseRareStage()
    {
        // rare [N] field — inverse of top
        var count = 10;
        if (Current.Kind == BqlTokenKind.NumberLiteral)
            count = ExpectNumber();
        var field = ExpectIdentifier();
        return new RareStage { Count = count, Field = field };
    }

    private RegexStage ParseRegexStage()
    {
        // regex "pattern" | regex host="pattern" | regex host!="pattern" | regex field=host "pattern"
        var stage = new RegexStage();

        if (Current.Kind == BqlTokenKind.Identifier && Peek(1).Kind is BqlTokenKind.Equals or BqlTokenKind.NotEquals)
        {
            stage.Field = Current.Value;
            stage.Negate = Peek(1).Kind == BqlTokenKind.NotEquals;
            Advance();
            Advance();

            if (stage.Field.Equals("field", StringComparison.OrdinalIgnoreCase)
                && Current.Kind == BqlTokenKind.Identifier
                && Peek(1).Kind == BqlTokenKind.StringLiteral)
            {
                stage.Field = Current.Value;
                Advance();
            }
        }

        if (Current.Kind is not (BqlTokenKind.StringLiteral or BqlTokenKind.Identifier))
            throw new BqlParseException("Expected a pattern after regex", $"position {Current.Position}");

        stage.Pattern = Current.Value;
        ValidatePattern(stage.Pattern, "regex");
        Advance();
        return stage;
    }

    /// <summary>A pattern that doesn't compile is a mistake in the query, reported where it is.</summary>
    private void ValidatePattern(string pattern, string stage)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, PatternCheckTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new BqlParseException($"The {stage} pattern isn't a valid regular expression: {ex.Message}", $"position {Current.Position}");
        }
    }

    /// <summary>Compiling a pattern to check it never matches anything, so any short limit will do.</summary>
    private static readonly TimeSpan PatternCheckTimeout = TimeSpan.FromSeconds(1);

    private FieldSummaryStage ParseFieldSummaryStage()
    {
        // fieldsummary [maxvals=N]
        var stage = new FieldSummaryStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("maxvals", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            if (int.TryParse(Current.Value, out var maxVals))
            {
                stage.MaxValues = maxVals;
                Advance();
            }
        }
        return stage;
    }

    private XyseriesStage ParseXyseriesStage()
    {
        // xyseries row_field column_field value_field
        var row = ExpectIdentifier();
        var col = ExpectIdentifier();
        var val = ExpectIdentifier();
        return new XyseriesStage { RowField = row, ColumnField = col, ValueField = val };
    }

    private UntableStage ParseUntableStage()
    {
        // untable row_field column_field value_field
        var row = ExpectIdentifier();
        var col = ExpectIdentifier();
        var val = ExpectIdentifier();
        return new UntableStage { RowField = row, ColumnField = col, ValueField = val };
    }

    private EvalStage ParseConvertStage()
    {
        // convert ctime(field) [as alias], dur2sec(field) [as alias]
        // Translates to eval assignments using expression functions
        var assignments = new List<EvalAssignment>();
        do
        {
            if (Current.Kind == BqlTokenKind.Comma) Advance();
            var funcName = ExpectIdentifier();
            Expect(BqlTokenKind.OpenParen, "(");
            var fieldExpr = ParseOrExpression();
            Expect(BqlTokenKind.CloseParen, ")");
            var alias = fieldExpr is FieldExpression fe ? fe.Name : funcName + "(value)";
            if (Current.IsKeyword("as"))
            {
                Advance();
                alias = ExpectIdentifier();
            }
            assignments.Add(new EvalAssignment(alias, new FunctionExpression(funcName, new List<BqlExpression> { fieldExpr })));
        } while (Current.Kind == BqlTokenKind.Comma);

        return new EvalStage(assignments);
    }

    private static TimeSpan? ParseTimeSpanValue(string value)
    {
        var match = Regex.Match(value, @"^(\d+)(s|m|h|d|w)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;
        // Use long.TryParse to prevent overflow on very large values, clamp to reasonable max (10 years)
        // A span of zero would divide by zero wherever it is used
        if (!long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;
        const long maxDays = 3650; // ~10 years
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => amount > maxDays * 86400 ? null : TimeSpan.FromSeconds(amount),
            "m" => amount > maxDays * 1440 ? null : TimeSpan.FromMinutes(amount),
            "h" => amount > maxDays * 24 ? null : TimeSpan.FromHours(amount),
            "d" => amount > maxDays ? null : TimeSpan.FromDays(amount),
            "w" => amount > maxDays / 7 ? null : TimeSpan.FromDays(amount * 7),
            _ => null
        };
    }

    private FieldAliasStage ParseFieldAliasStage()
    {
        // fieldalias alias1=original1, alias2=original2
        var aliases = new Dictionary<string, string>();

        var aliasName = ExpectIdentifier();
        Expect(BqlTokenKind.Equals, "=");
        var originalField = ExpectIdentifier();
        aliases[aliasName] = originalField;

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            aliasName = ExpectIdentifier();
            Expect(BqlTokenKind.Equals, "=");
            originalField = ExpectIdentifier();
            aliases[aliasName] = originalField;
        }

        return new FieldAliasStage(aliases);
    }

    private TransactionStage ParseTransactionStage()
    {
        // transaction field1, field2 [maxspan=Xh] [maxevents=N]
        var fields = new List<string>();
        fields.Add(ExpectIdentifier());

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }

        TimeSpan? maxSpan = null;
        int? maxEvents = null;
        string? startsWith = null;
        string? endsWith = null;

        // Parse optional parameters
        while (Current.Kind == BqlTokenKind.Identifier && Current.Kind != BqlTokenKind.Pipe && Current.Kind != BqlTokenKind.Eof)
        {
            if (Current.Value.StartsWith("maxspan", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // skip "maxspan"
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind == BqlTokenKind.TimeSpanLiteral)
                {
                    maxSpan = ParseTimeSpanValue(Current.Value);
                    Advance();
                }
                continue;
            }
            if (Current.IsAnyKeyword("startswith", "endswith"))
            {
                var isStart = Current.IsKeyword("startswith");
                Advance();
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind != BqlTokenKind.StringLiteral)
                    throw new BqlParseException("Expected a quoted string after startswith or endswith", $"position {Current.Position}");

                if (isStart) startsWith = Current.Value;
                else endsWith = Current.Value;
                Advance();
                continue;
            }
            if (Current.Value.StartsWith("maxevents", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // skip "maxevents"
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind == BqlTokenKind.NumberLiteral)
                {
                    maxEvents = Math.Clamp(ExpectNumber(), 1, _limits.MaxTransactionEvents);
                }
                continue;
            }
            break;
        }

        return new TransactionStage(fields, maxSpan, maxEvents) { StartsWith = startsWith, EndsWith = endsWith };
    }

    private EventStatsStage ParseEventStatsStage()
    {
        var stage = new EventStatsStage();

        // Parse aggregation functions (same as stats)
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    private StreamStatsStage ParseStreamStatsStage()
    {
        var stage = new StreamStatsStage();

        // Parse optional window=N parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("window", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "window"
            Advance(); // skip "="
            stage.Window = ExpectNumber();
        }

        // Parse aggregation functions (same as stats)
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    // ─── Helpers ─────────────────────────────────────────────

    private BqlToken Current => _pos < _tokens.Count ? _tokens[_pos] : new BqlToken(BqlTokenKind.Eof, "", -1);

    private BqlToken Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : new BqlToken(BqlTokenKind.Eof, "", -1);
    }

    private void Advance() => _pos++;

    private void Expect(BqlTokenKind kind, string display)
    {
        if (Current.Kind != kind)
            throw new BqlParseException($"Expected '{display}' but got '{Current.Value}'", $"position {Current.Position}");
        Advance();
    }

    private void ExpectKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
            throw new BqlParseException($"Expected '{keyword}' but got '{Current.Value}'", $"position {Current.Position}");
        Advance();
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind != BqlTokenKind.Identifier)
            throw new BqlParseException($"Expected identifier but got '{Current.Value}'", $"position {Current.Position}");
        var name = Current.Value;
        Advance();
        return name;
    }

    private int ExpectNumber()
    {
        if (Current.Kind != BqlTokenKind.NumberLiteral)
            throw new BqlParseException($"Expected number but got '{Current.Value}'", $"position {Current.Position}");
        if (!int.TryParse(Current.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            throw new BqlParseException($"'{Current.Value}' isn't a whole number between {int.MinValue} and {int.MaxValue}", $"position {Current.Position}");
        Advance();
        return num;
    }

    private DateTime ParseDateTimeLiteral()
    {
        if (Current.Kind == BqlTokenKind.StringLiteral)
        {
            var val = Current.Value;
            Advance();
            if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            throw new BqlParseException($"Invalid date/time: '{val}'", $"position {Current.Position}");
        }
        throw new BqlParseException("Expected date/time string literal", $"position {Current.Position}");
    }


    /// <summary>
    /// In <c>severity &gt;= warning</c>, when the schema gives <c>severity</c> named levels, the
    /// name is a level. Elsewhere it's a field.
    /// </summary>
    private BqlExpression LevelLiteral(BqlExpression left, BqlExpression value) =>
        left is FieldExpression { Name: var name }
        && value is FieldExpression { Name: var level }
        && _schema.OrdinalScale(name) is { } scale
        && scale.TryGetValue(level, out var number)
            ? new LiteralExpression(number)
            : value;

    private static bool IsComparisonOperator(BqlTokenKind kind) =>
        kind is BqlTokenKind.Equals or BqlTokenKind.NotEquals
            or BqlTokenKind.Greater or BqlTokenKind.GreaterEquals
            or BqlTokenKind.Less or BqlTokenKind.LessEquals;

    private static bool IsStageKeyword(BqlToken token) =>
        token.IsAnyKeyword(
            "where", "stats", "group", "sort", "head", "tail", "top",
            "limit", "compute", "eval", "extract", "rex", "rename",
            "select", "table", "fields", "dedup", "unique", "timechart",
            "fill", "expand", "fieldalias", "transaction",
            "lookup", "append", "join", "eventstats", "streamstats",
            "fillnull", "bucket", "bin", "makeresults", "mvexpand",
            "chart", "filldown", "addcoltotals", "inputlookup",
            "rare", "regex", "fieldsummary", "xyseries", "untable", "convert",
            "macro", "subsearch", "multisearch", "iplocation", "cluster");

    private ClusterStage ParseClusterStage()
    {
        // cluster [t=0.5] [field=name] [labelonly=true|false]
        double similarity = 0.5;
        string? field = null;
        var labelOnly = false;
        while (Current.Kind == BqlTokenKind.Identifier && Peek(1).Kind == BqlTokenKind.Equals)
        {
            var name = Current.Value.ToLowerInvariant();
            Advance();
            Advance();
            var value = Current.Value;
            Advance();
            switch (name)
            {
                case "t":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out similarity) || similarity <= 0 || similarity > 1)
                        throw new BqlParseException($"cluster t= is between 0 and 1, not '{value}'");
                    break;
                case "field":
                    field = value;
                    break;
                case "labelonly":
                    labelOnly = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
                    break;
                default:
                    throw new BqlParseException($"cluster doesn't take {name}=; it takes t=, field= and labelonly=");
            }
        }

        return new ClusterStage { Similarity = similarity, Field = field, LabelOnly = labelOnly };
    }

    private IplocationStage ParseIplocationStage()
    {
        // iplocation [prefix=x] field
        var prefix = string.Empty;
        if (Current.IsKeyword("prefix") && Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance();
            Advance();
            prefix = Current.Value;
            Advance();
        }

        return new IplocationStage { Field = ExpectIdentifier(), Prefix = prefix };
    }
}
