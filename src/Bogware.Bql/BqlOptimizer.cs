using System.Text.RegularExpressions;

namespace Bogware.Bql;

/// <summary>
/// Moves filtering into the source where that can't change a query's answer.
/// <para>
/// Two rules keep it correct. Only the leading run of filters is pushed, because a filter after
/// <c>head</c>, <c>eval</c> or <c>stats</c> applies to that stage's output, not to stored rows.
/// And a predicate is only removed from the pipeline when the source says it applies it exactly;
/// anything else stays in the pipeline.
/// </para>
/// </summary>
public sealed class BqlOptimizer
{
    // Longest search text the optimizer splits or rewrites; longer text is left as written.
    private const int MaxSearchFilterLength = 2048;

    // The optimizer's own patterns are fixed and simple; this only guards against a pathological input.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex QuotedText = new("\"[^\"]*\"", RegexOptions.None, RegexTimeout);

    private static readonly Regex FieldComparison = new(
        @"(?<![\w.])[A-Za-z_][\w.]*\s*(==|!=|>=|<=|=|>|<)",
        RegexOptions.None,
        RegexTimeout);

    private static readonly Regex BareEqualityValue = new(
        @"(?<field>(?<![\w.])[A-Za-z_][\w.]*)\s*(?<op>==|!=|=)\s*(?<value>[A-Za-z_][\w.\-]*)",
        RegexOptions.None,
        RegexTimeout);

    private readonly BqlSchema _schema;
    private readonly BqlSourceCapabilities _capabilities;
    private readonly BqlParser _parser;
    private readonly BqlParser _searchConditionParser;

    /// <summary>An optimizer for a source with this schema and these capabilities.</summary>
    public BqlOptimizer(BqlSchema? schema = null, BqlSourceCapabilities? capabilities = null, BqlParser? parser = null)
    {
        _schema = schema ?? BqlSchema.Default;
        _capabilities = capabilities ?? BqlSourceCapabilities.None;
        _parser = parser ?? new BqlParser(_schema);
        _searchConditionParser = _parser.WithoutUnitLiterals();
    }

    /// <summary>
    /// Optimize a BQL query: extract what the source can do, leave the rest as pipeline stages.
    /// </summary>
    public OptimizedQuery Optimize(BqlQuery query, BqlExecutionContext? executionContext = null)
    {
        var pushdown = new BqlPushdown
        {
            Sort = new BqlSortOrder(_schema.DefaultSort.Field, _schema.DefaultSort.Descending),
            AllowedDatasets = executionContext?.AllowedDatasets,
            After = executionContext?.After,
        };

        // Time range from the query, then from the caller, which overrides it
        pushdown.StartTime = executionContext?.StartTime ?? query.TimeRange?.Start;
        pushdown.EndTime = executionContext?.EndTime ?? query.TimeRange?.End;
        var headerStart = pushdown.StartTime;
        var headerEnd = pushdown.EndTime;

        var scopedDataset = executionContext?.Dataset;
        if (!string.IsNullOrEmpty(scopedDataset))
        {
            pushdown.Datasets = [scopedDataset];
        }

        var remainingStages = new List<BqlStage>();
        var canPush = true;

        foreach (var stage in query.Stages.SelectMany(SplitTextAndFieldSearch).Select(RewriteFieldSearch))
        {
            if (canPush && TryPushDown(stage, pushdown))
                continue;

            // Filters commute with each other. Anything else changes what later stages see.
            canPush = canPush && stage is WhereStage or SearchStage;
            remainingStages.Add(stage);
        }

        // What the source isn't trusted to do, the pipeline does first
        var checkTime = !_capabilities.TimeBounds && (headerStart != null || headerEnd != null);
        var checkDataset = !string.IsNullOrEmpty(scopedDataset) && _schema.DatasetField != null;
        if (checkTime || checkDataset)
        {
            remainingStages.Insert(0, new BoundsStage
            {
                Start = checkTime ? headerStart : null,
                End = checkTime ? headerEnd : null,
                DatasetField = checkDataset ? _schema.DatasetField : null,
                Dataset = checkDataset ? scopedDataset : null,
            });
        }

        if (!_capabilities.TextSearch)
        {
            pushdown.TextTerms = null;
        }

        return new OptimizedQuery
        {
            Pushdown = pushdown,
            RemainingStages = remainingStages,
            OriginalQuery = query
        };
    }

    private static readonly Regex FieldTerm = new(
        @"^[A-Za-z_][\w.]*(==|!=|>=|<=|=|>|<)\S",
        RegexOptions.None,
        RegexTimeout);

    /// <summary>
    /// Splits <c>search error host=web</c> into a text search for <c>error</c> and a separate
    /// field search for <c>host=web</c>, which together mean both, as in SPL. Searches with
    /// OR, NOT or parentheses are left whole.
    /// </summary>
    private static IEnumerable<BqlStage> SplitTextAndFieldSearch(BqlStage stage)
    {
        if (stage is not SearchStage search || search.SearchText.Length > MaxSearchFilterLength
            || SplitTerms(search.SearchText) is not { } terms)
        {
            return [stage];
        }

        var fields = terms.Where(term => FieldTerm.IsMatch(term)).ToList();
        if (fields.Count == 0 || fields.Count == terms.Count)
        {
            return [stage];
        }

        return
        [
            new SearchStage(string.Join(' ', terms.Where(term => !FieldTerm.IsMatch(term)))),
            new SearchStage(string.Join(' ', fields)),
        ];
    }

    /// <summary>
    /// The space-separated terms of a search, keeping quoted text together, or null when it uses
    /// boolean operators or grouping.
    /// </summary>
    private static List<string>? SplitTerms(string text)
    {
        var terms = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '(' or ')')
            {
                return null;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    terms.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return quoted || terms.Any(term => term is "OR" or "NOT" or "AND" or "or" or "not" or "and") ? null : terms;
    }

    /// <summary>
    /// Turns SPL-style <c>search status=500</c> or <c>search a=1 OR b=2</c> into the equivalent
    /// <c>where</c>. Bare values are strings, as in SPL. Text that doesn't parse as a condition
    /// stays a text search.
    /// </summary>
    private BqlStage RewriteFieldSearch(BqlStage stage)
    {
        if (stage is not SearchStage search || search.SearchText.Length > MaxSearchFilterLength)
            return stage;

        // Words would read as field names in a where; the search evaluates them itself
        try
        {
            if (BqlTextSearch.Parse(search.SearchText, _parser.Limits.MaxSearchDepth).HasText)
                return stage;
        }
        catch (BqlParseException)
        {
            return stage;
        }

        var withoutQuotedText = QuotedText.Replace(search.SearchText, "\"\"");
        if (!FieldComparison.IsMatch(withoutQuotedText))
            return stage;

        var condition = BareEqualityValue.Replace(search.SearchText, match =>
            _schema.OrdinalScale(match.Groups["field"].Value) is { } scale && scale.TryGetValue(match.Groups["value"].Value, out _)
                ? match.Value
                : $"{match.Groups["field"].Value} {match.Groups["op"].Value} \"{match.Groups["value"].Value}\"");

        try
        {
            // Bare values in search text are text, so 10MB and -7d stay as written
            var parsed = _searchConditionParser.ParseBql($"* | where {condition}");
            return parsed.Stages.OfType<WhereStage>().LastOrDefault() ?? stage;
        }
        catch (BqlParseException)
        {
            return stage;
        }
    }

    /// <summary>
    /// Try to push a stage down to the source. Returns true if the source does all of it, so the
    /// stage can leave the pipeline.
    /// </summary>
    private bool TryPushDown(BqlStage stage, BqlPushdown pushdown)
    {
        switch (stage)
        {
            case SearchStage search:
                return TryPushSearch(search, pushdown);

            case WhereStage where:
                return TryPushCondition(where.Condition, pushdown);

            case SortStage sort when _capabilities.TimeOrder && sort.Fields.Count == 1 && _schema.IsTimeField(sort.Fields[0].Field):
                // Sources sort natively only on time. Other sorts run in the pipeline.
                pushdown.Sort = new BqlSortOrder(_schema.TimeField, sort.Fields[0].Descending);
                return true;

            default:
                return false;
        }
    }

    private bool TryPushSearch(SearchStage search, BqlPushdown pushdown)
    {
        // A source that searches text finds rows with all of some words. That narrows any search
        // whose words are all required; the pipeline checks the rest (OR, NOT, phrases, wildcards).
        var parsed = BqlTextSearch.Parse(search.SearchText, _parser.Limits.MaxSearchDepth);
        if (parsed.RequiredWords != null)
        {
            var terms = SearchTerms.Parse(parsed.RequiredWords);
            pushdown.TextTerms = pushdown.TextTerms == null ? terms : [.. pushdown.TextTerms, .. terms];
        }

        foreach (var (field, value) in parsed.RequiredFieldValues)
        {
            if (_schema.DatasetField is { } datasetField && _schema.FieldComparer.Equals(field, datasetField))
            {
                NarrowToDataset(pushdown, value);
            }
        }

        return parsed.IsPlainWords && (parsed.RequiredWords == null || _capabilities.TextSearch);
    }

    /// <summary>
    /// Narrows what the source reads to one dataset, ignoring case. What the source returns is
    /// then a superset of the answer, and the stage that asked stays to decide exactly. Two
    /// different datasets leave nothing, which is also the pipeline's answer.
    /// </summary>
    internal static void NarrowToDataset(BqlPushdown pushdown, string dataset)
    {
        if (string.IsNullOrEmpty(dataset) || dataset.Contains('*', StringComparison.Ordinal))
        {
            return;
        }

        pushdown.Datasets = pushdown.Datasets is { } already
            ? already.Where(existing => existing.Equals(dataset, StringComparison.OrdinalIgnoreCase)).ToList()
            : [dataset];
    }

    /// <summary>
    /// Pushes what it can of a condition. Returns true only if the whole condition was pushed,
    /// so a partly pushed AND stays in the pipeline for the remainder.
    /// </summary>
    private bool TryPushCondition(BqlExpression expr, BqlPushdown pushdown)
    {
        if (expr is LogicalExpression { Operator: "AND" } and)
        {
            var leftPushed = TryPushCondition(and.Left, pushdown);
            var rightPushed = TryPushCondition(and.Right, pushdown);
            return leftPushed && rightPushed;
        }

        return expr is ComparisonExpression { Left: FieldExpression field, Right: LiteralExpression literal } comparison
            && TryPushComparison(field.Name, comparison.Operator, literal.Value, pushdown);
    }

    private bool TryPushComparison(string field, string op, object? value, BqlPushdown pushdown)
    {
        // A time bound narrows what the source reads. Bounds are inclusive and the pipeline
        // still checks the exact comparison, so the stage stays.
        if (_schema.IsTimeField(field) && op is ">" or ">=" or "<" or "<=" && TimeLiteral(value) is { } time)
        {
            if (op is ">" or ">=")
                pushdown.StartTime = pushdown.StartTime is { } start && start > time ? start : time;
            else
                pushdown.EndTime = pushdown.EndTime is { } end && end < time ? end : time;
            return false;
        }

        // The source narrows; the where stage still compares exactly
        if (_schema.DatasetField is { } datasetField && _schema.FieldComparer.Equals(field, datasetField)
            && op is "=" or "==" && value is string dataset)
        {
            NarrowToDataset(pushdown, dataset);
            return false;
        }

        var predicate = new BqlPredicate(field, op == "==" ? "=" : op, value);
        if (!_capabilities.CanApply(predicate))
        {
            return false;
        }

        pushdown.Predicates.Add(predicate);
        return true;
    }

    /// <summary>A literal as a UTC time: a date string, or epoch seconds as SPL writes them.</summary>
    private static DateTime? TimeLiteral(object? value) => value switch
    {
        DateTime time => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc),
        int or long or double => BqlTime.FromValue(null, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
        string text => BqlTime.Parse(text, null),
        _ => null,
    };
}

/// <summary>
/// Result of query optimization: what the source does, and the pipeline stages left
/// </summary>
public sealed class OptimizedQuery
{
    /// <summary>What the source is asked for.</summary>
    public required BqlPushdown Pushdown { get; init; }
    public required List<BqlStage> RemainingStages { get; init; }
    public required BqlQuery OriginalQuery { get; init; }
}

/// <summary>
/// Checks bounds the source wasn't trusted to apply: the query's time range and the caller's
/// dataset. The optimizer adds it; it can't be written in a query.
/// </summary>
public sealed class BoundsStage : BqlStage
{
    public override string StageName => "bounds";

    /// <summary>Earliest time, inclusive.</summary>
    public DateTime? Start { get; init; }

    /// <summary>Latest time, inclusive.</summary>
    public DateTime? End { get; init; }

    /// <summary>The dataset field, when the dataset is checked.</summary>
    public string? DatasetField { get; init; }

    /// <summary>The dataset rows must be in.</summary>
    public string? Dataset { get; init; }
}
