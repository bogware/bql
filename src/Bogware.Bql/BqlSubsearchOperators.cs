namespace Bogware.Bql;

// Subsearches run through BqlQueryEngine.ExecuteSubsearchAsync, which keeps the outer query's
// caller (AllowedDatasets, Dataset), time bounds and remaining time, and counts nesting against
// BqlEngineOptions.MaxNestingDepth together with macros. A subsearch never sees more than the
// query it sits in.

/// <summary>
/// Append operator — executes a subsearch and appends its results to the current result set.
/// Equivalent to Splunk's | append [subsearch].
/// </summary>
internal sealed class LegacyAppendOperator : BqlOperator
{
    private readonly string _subsearchQuery;
    private readonly BqlQueryEngine _engine;
    private readonly BqlBudget _budget;
    public override string Name => "append";

    public LegacyAppendOperator(
        string subsearchQuery,
        BqlQueryEngine engine,
        BqlBudget budget)
    {
        _budget = budget;
        _subsearchQuery = subsearchQuery;
        _engine = engine;
    }

    private List<dynamic> CapRows(List<dynamic> rows)
    {
        if (rows.Count <= _budget.Limits.MaxRowsInMemory)
        {
            return rows;
        }

        _budget.Truncated = true;
        rows.RemoveRange(_budget.Limits.MaxRowsInMemory, rows.Count - _budget.Limits.MaxRowsInMemory);
        return rows;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        try
        {
            var subResult = await _engine.ExecuteSubsearchAsync(_subsearchQuery, Budget ?? _budget, ct);

            // Append subsearch results to the main results
            var combined = new List<dynamic>(mainResults.Count + subResult.Events.Count);
            combined.AddRange(mainResults);
            combined.AddRange(subResult.Events);
            return CapRows(combined);
        }
        catch (Exception ex) when (!BqlFailures.PassesThrough(ex))
        {
            _engine.Report(BqlDiagnosticLevel.Warning, $"Append subsearch failed: {_subsearchQuery}", ex);
            // On subsearch failure, return original results unchanged (Splunk behavior)
            return mainResults;
        }
    }
}

/// <summary>
/// Multisearch operator — runs multiple independent search queries and unions their results.
/// Equivalent to Splunk's | multisearch [search "q1"] [search "q2"].
/// Unlike append, multisearch ignores the incoming pipeline events and produces
/// a fresh result set from the union of all sub-searches.
/// </summary>
internal sealed class LegacyMultisearchOperator : BqlOperator
{
    private readonly List<string> _searchQueries;
    private readonly BqlQueryEngine _engine;
    private readonly BqlBudget _budget;
    public override string Name => "multisearch";

    public LegacyMultisearchOperator(
        List<string> searchQueries,
        BqlQueryEngine engine,
        BqlBudget budget)
    {
        _budget = budget;
        _searchQueries = searchQueries;
        _engine = engine;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Multisearch replaces the incoming pipeline with the union of all sub-searches
        var combined = new List<dynamic>();

        foreach (var query in _searchQueries)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Prefix with "find" so the sub-query is a valid BQL search
                var subResult = await _engine.ExecuteSubsearchAsync($"find {query}", Budget ?? _budget, ct);
                combined.AddRange(subResult.Events);

                // Each sub-search is limited, but their sum has to be as well.
                if (combined.Count > _budget.Limits.MaxRowsInMemory)
                {
                    _budget.Truncated = true;
                    combined.RemoveRange(_budget.Limits.MaxRowsInMemory, combined.Count - _budget.Limits.MaxRowsInMemory);
                    break;
                }
            }
            catch (Exception ex) when (!BqlFailures.PassesThrough(ex))
            {
                // A missing leg would quietly leave its results out of the answer
                throw Failed("A multisearch search", ex, _engine);
            }
        }

        return combined;
    }

    /// <summary>
    /// A subsearch failed. Our own parse and execution errors are fit to show; anything else (the
    /// host's source, a bug) goes to diagnostics, and the person sees only that it failed.
    /// </summary>
    internal static BqlExecutionException Failed(string what, Exception ex, BqlQueryEngine engine)
    {
        if (ex is BqlParseException own)
        {
            return new BqlExecutionException($"{what} failed: {own.Message}", own);
        }

        engine.Report(BqlDiagnosticLevel.Warning, $"{what} failed", ex);
        return new BqlExecutionException($"{what} failed.", ex);
    }
}

/// <summary>
/// Join operator — executes a subsearch and performs a hash join with the main results.
/// Supports inner, left, and outer join types. Equivalent to Splunk's | join.
/// </summary>
internal sealed class LegacyJoinOperator : BqlOperator
{
    private readonly string _joinField;
    private readonly string _joinType;
    private readonly string _subsearchQuery;
    private readonly BqlQueryEngine _engine;
    private readonly BqlBudget _budget;
    public override string Name => "join";

    public LegacyJoinOperator(
        string joinField,
        string joinType,
        string subsearchQuery,
        BqlQueryEngine engine,
        BqlBudget budget)
    {
        _budget = budget;
        _joinField = joinField;
        _joinType = joinType;
        _subsearchQuery = subsearchQuery;
        _engine = engine;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        BqlResult subResult;
        try
        {
            subResult = await _engine.ExecuteSubsearchAsync(_subsearchQuery, Budget ?? _budget, ct);
        }
        catch (Exception ex) when (!BqlFailures.PassesThrough(ex))
        {
            throw LegacyMultisearchOperator.Failed("The join's search", ex, _engine);
        }

        // Build a hash lookup from subsearch results keyed by the join field
        var subLookup = new Dictionary<string, List<IDictionary<string, object?>>>();
        foreach (var subEvt in subResult.Events)
        {
            if (subEvt is IDictionary<string, object?> subDict)
            {
                var keyVal = GetFieldValue(subDict, _joinField);
                if (keyVal != null)
                {
                    var key = keyVal.ToString() ?? "";
                    if (!subLookup.TryGetValue(key, out var list))
                    {
                        list = new List<IDictionary<string, object?>>();
                        subLookup[key] = list;
                    }
                    list.Add(subDict);
                }
            }
        }

        // Perform the join. A key shared by many rows on both sides multiplies, so the output is
        // held to MaxRowsInMemory like any other stage that keeps rows.
        var maxRows = _budget.Limits.MaxRowsInMemory;
        var results = new List<dynamic>();
        var seen = 0;

        foreach (var mainEvt in mainResults)
        {
            if (++seen % CancellationCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (mainEvt is IDictionary<string, object?> mainDict)
            {
                var keyVal = GetFieldValue(mainDict, _joinField);
                var key = keyVal?.ToString() ?? "";

                if (subLookup.TryGetValue(key, out var matches))
                {
                    // For each match, merge the subsearch fields into the main event
                    foreach (var match in matches)
                    {
                        if (results.Count >= maxRows)
                        {
                            _budget.Truncated = true;
                            return results;
                        }

                        results.Add(MergeEvents(mainDict, match));
                    }
                }
                else if (_joinType is "left" or "outer")
                {
                    if (results.Count >= maxRows)
                    {
                        _budget.Truncated = true;
                        return results;
                    }

                    // Left/outer join: keep main event even without match
                    results.Add(mainEvt);
                }
                // Inner join: skip events without matches
            }
        }

        // Outer join: add unmatched subsearch results
        if (_joinType == "outer")
        {
            var mainKeys = new HashSet<string>();
            foreach (var mainEvt in mainResults)
            {
                if (mainEvt is IDictionary<string, object?> mainDict)
                {
                    var kv = GetFieldValue(mainDict, _joinField);
                    if (kv != null) mainKeys.Add(kv.ToString() ?? "");
                }
            }

            foreach (var kvp in subLookup)
            {
                if (!mainKeys.Contains(kvp.Key))
                {
                    foreach (var subEvt in kvp.Value)
                    {
                        if (results.Count >= maxRows)
                        {
                            _budget.Truncated = true;
                            return results;
                        }

                        var expando = NewRow();
                        var dict = (IDictionary<string, object?>)expando;
                        foreach (var field in subEvt)
                            dict[field.Key] = field.Value;
                        results.Add(expando);
                    }
                }
            }
        }

        return results;
    }

    private static object? GetFieldValue(IDictionary<string, object?> dict, string field)
    {
        if (dict.TryGetValue(field, out var val)) return val;
        // Case-insensitive fallback
        foreach (var kvp in dict)
        {
            if (kvp.Key.Equals(field, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    private dynamic MergeEvents(IDictionary<string, object?> main, IDictionary<string, object?> sub)
    {
        var expando = NewRow();
        var dict = (IDictionary<string, object?>)expando;

        // Copy main event fields first
        foreach (var field in main)
            dict[field.Key] = field.Value;

        // Overlay subsearch fields (don't overwrite existing main fields)
        foreach (var field in sub)
        {
            if (!dict.ContainsKey(field.Key))
                dict[field.Key] = field.Value;
        }

        return expando;
    }
}

/// <summary>
/// Macro operator — expands a named macro from knowledge storage and executes its pipeline stages.
/// The macro definition is a BQL pipeline fragment (e.g., "where status >= 500 | stats count by path")
/// which is parsed, and its stages are executed sequentially on the input events.
/// Supports $param$ substitution for macro parameters. Expansion counts toward
/// <see cref="BqlEngineOptions.MaxNestingDepth"/>, together with any subsearches, so a macro that
/// reaches itself through a subsearch stops with an error.
/// </summary>
internal sealed class LegacyMacroOperator : BqlOperator
{
    private readonly string _macroName;
    private readonly List<string> _arguments;
    private readonly BqlQueryEngine _engine;
    private readonly IBqlKnowledgeProvider _knowledgeStorage;
    public override string Name => "macro";

    public LegacyMacroOperator(
        string macroName,
        List<string> arguments,
        BqlQueryEngine engine,
        IBqlKnowledgeProvider knowledgeStorage)
    {
        _macroName = macroName;
        _arguments = arguments;
        _engine = engine;
        _knowledgeStorage = knowledgeStorage;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var budget = Budget ?? new BqlBudget(_engine.Limits, _engine.Schema);
        return await ExpandAndExecuteAsync(input, _macroName, _arguments, budget, 0, ct);
    }

    private async Task<IEnumerable<dynamic>> ExpandAndExecuteAsync(
        IEnumerable<dynamic> input,
        string macroName,
        List<string> arguments,
        BqlBudget budget,
        int expansion,
        CancellationToken ct)
    {
        if (budget.Depth + expansion >= _engine.Options.MaxNestingDepth)
        {
            throw new BqlLimitExceededException(
                $"Macro '{macroName}' nests more than {_engine.Options.MaxNestingDepth} deep with the macros and subsearches around it; it probably refers to itself.");
        }

        var macro = await _knowledgeStorage.GetMacroAsync(macroName, budget.Caller, ct);
        if (macro == null)
        {
            throw new BqlExecutionException($"There's no macro named '{macroName}'");
        }

        // Substitute parameters: replace $param_name$ with argument values
        var definition = SubstituteParameters(macro.Definition, macro.Parameters, arguments);

        _engine.Report(BqlDiagnosticLevel.Debug, $"Expanding macro '{macroName}' as: {definition}");

        BqlQuery parsed;
        try
        {
            // Parse the macro definition as a BQL pipeline fragment.
            // Prepend "find *" to make it a valid full query, then extract only the pipeline stages.
            parsed = _engine.Parse($"find * | {definition}");
        }
        catch (BqlParseException ex)
        {
            throw new BqlExecutionException($"Macro '{macroName}' doesn't parse: {ex.Message}");
        }

        // Stages inside the macro run one level deeper, so a subsearch in them counts this expansion.
        var inner = budget.Deeper(expansion + 1);
        IEnumerable<dynamic> result = input;
        foreach (var stage in parsed.Stages)
        {
            ct.ThrowIfCancellationRequested();

            // Handle nested macro expansion
            if (stage is MacroStage nestedMacro)
            {
                result = await ExpandAndExecuteAsync(
                    result, nestedMacro.MacroName, nestedMacro.Arguments, budget, expansion + 1, ct);
                continue;
            }

            // Skip the initial search stage we prepended
            if (stage is SearchStage s && s.SearchText == "*" && stage == parsed.Stages[0])
                continue;

            var op = _engine.ConvertStageToOperator(stage, inner);
            if (op != null)
                result = await op.ExecuteAsync(result, ct);
        }

        return result;
    }

    private string SubstituteParameters(string definition, IReadOnlyList<string> parameterNames, List<string> arguments)
    {
        var result = definition;
        for (var i = 0; i < parameterNames.Count && i < arguments.Count; i++)
        {
            // Sanitize argument: strip pipe characters to prevent BQL injection
            var sanitized = arguments[i].Replace("|", "");
            result = result.Replace($"${parameterNames[i]}$", sanitized, StringComparison.OrdinalIgnoreCase);
        }

        // Guard against macro expansion producing excessively long queries
        if (result.Length > _engine.Options.MaxMacroExpansionLength)
            throw new BqlParseException("Macro expansion produced an excessively large query", "macro expansion");

        return result;
    }
}

/// <summary>
/// Subsearch operator — executes an inner query and filters the outer result set
/// by matching field values from the subsearch results.
/// If the subsearch returns a single column, events are filtered where that column's values
/// match. If multiple columns, events must match all field values in at least one subsearch row.
/// </summary>
internal sealed class LegacySubsearchOperator : BqlOperator
{
    private readonly string _subsearchQuery;
    private readonly BqlQueryEngine _engine;
    private readonly BqlBudget _budget;
    public override string Name => "subsearch";

    public LegacySubsearchOperator(
        string subsearchQuery,
        BqlQueryEngine engine,
        BqlBudget budget)
    {
        _subsearchQuery = subsearchQuery;
        _engine = engine;
        _budget = budget;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        BqlResult subResult;
        try
        {
            subResult = await _engine.ExecuteSubsearchAsync(_subsearchQuery, Budget ?? _budget, ct);
        }
        catch (Exception ex) when (!BqlFailures.PassesThrough(ex))
        {
            throw LegacyMultisearchOperator.Failed("The subsearch", ex, _engine);
        }

        if (subResult.Events.Count == 0)
        {
            return Enumerable.Empty<dynamic>();
        }

        // Determine the filter fields from the subsearch results
        var firstEvent = (IDictionary<string, object?>)subResult.Events[0];

        // Use non-internal fields from the subsearch result as filter criteria.
        // Exclude common metadata fields that wouldn't be useful for filtering.
        var filterFields = firstEvent.Keys
            .Where(k => !_engine.Schema.IsInternalField(k))
            .ToList();

        if (filterFields.Count == 0)
        {
            throw new BqlExecutionException("The subsearch returned no fields to filter by; end it with | fields name or | table name");
        }

        // Each subsearch row means "all of these fields have these values"; rows are alternatives
        static string Key(IDictionary<string, object?> row, List<string> fields) =>
            string.Join('\u001f', fields.Select(f => row.TryGetValue(f, out var v) && v != null ? v.ToString()?.ToLowerInvariant() ?? string.Empty : "\u0000"));

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subEvt in subResult.Events)
        {
            if (subEvt is IDictionary<string, object?> subDict)
            {
                allowed.Add(Key(subDict, filterFields));
            }
        }

        return mainResults.Where(evt => evt is IDictionary<string, object?> dict && allowed.Contains(Key(dict, filterFields)));
    }
}
