namespace Bogware.Bql;

/// <summary>
/// Decides whether a pipeline can do without the text field, so a source may leave it out of the
/// rows it reads. It answers false only when it can show that no stage reads the text and that
/// the text can't reach the output. Anything it doesn't know counts as reading the text: saying
/// "not needed" wrongly would drop data without a word.
/// </summary>
internal static class BqlTextUse
{
    private enum Use
    {
        // The stage neither reads the text nor removes it: look at the next one
        Passes,

        // The stage reads the text, or may
        Reads,

        // The stage makes new rows from named fields, none of them the text: what follows can't see it
        Drops,
    }

    /// <summary>Whether the rows the stages read need the schema's text field.</summary>
    public static bool Needed(IReadOnlyList<BqlStage> stages, BqlSchema schema)
    {
        // Rows are bounded and narrowed by these; if one is also the text, the text is needed
        if (schema.IsTextField(schema.TimeField) || schema.TimeFieldSynonyms.Any(schema.IsTextField)
            || (schema.DatasetField is { } dataset && schema.IsTextField(dataset)))
        {
            return true;
        }

        foreach (var stage in stages)
        {
            switch (Of(stage, schema))
            {
                case Use.Reads:
                    return true;
                case Use.Drops:
                    return false;
            }
        }

        // Rows reach the output as they are, text and all
        return true;
    }

    private static Use Of(BqlStage stage, BqlSchema schema)
    {
        bool Names(string? field) => field != null && (IsWildcard(field) || schema.IsTextField(field));
        bool NamesAny(IEnumerable<string?> fields) => fields.Any(Names);
        bool Reads(BqlExpression? expression) => expression != null && ExpressionReads(expression, schema);
        bool Aggregates(IEnumerable<AggregateFunction> aggregations) => aggregations.Any(a => Names(a.Field) || Reads(a.Expression));

        return stage switch
        {
            BoundsStage => Use.Passes,
            WhereStage where => Reads(where.Condition) ? Use.Reads : Use.Passes,
            EvalStage eval => eval.Assignments.Any(a => Reads(a.Expression) || IsWildcard(a.FieldName)) ? Use.Reads : Use.Passes,
            SortStage sort => NamesAny(sort.Fields.Select(f => f.Field)) ? Use.Reads : Use.Passes,
            LimitStage or TailStage => Use.Passes,
            DedupStage dedup => dedup.Fields.Count == 0 || NamesAny(dedup.Fields) ? Use.Reads : Use.Passes,
            RexStage rex => rex.SourceField == null || Names(rex.SourceField) ? Use.Reads : Use.Passes,
            RegexStage regex => regex.Field == null || Names(regex.Field) ? Use.Reads : Use.Passes,
            RenameStage rename => NamesAny(rename.Mappings.Select(m => m.OldName)) || rename.Mappings.Any(m => IsWildcard(m.NewName)) ? Use.Reads : Use.Passes,
            FieldAliasStage alias => NamesAny(alias.Aliases.Values) || alias.Aliases.Keys.Any(IsWildcard) ? Use.Reads : Use.Passes,
            EventStatsStage events => Aggregates(events.Aggregations) || NamesAny(events.GroupByFields) ? Use.Reads : Use.Passes,
            StreamStatsStage stream => Aggregates(stream.Aggregations) || NamesAny(stream.GroupByFields) ? Use.Reads : Use.Passes,
            FillNullStage fill => fill.Fields.Count == 0 || NamesAny(fill.Fields) ? Use.Reads : Use.Passes,
            FillDownStage fill => fill.Fields.Count == 0 || NamesAny(fill.Fields) ? Use.Reads : Use.Passes,
            AddColTotalsStage totals => totals.Fields.Count == 0 || NamesAny(totals.Fields) || Names(totals.LabelField) ? Use.Reads : Use.Passes,
            BucketStage bucket => Names(bucket.Field) || Names(bucket.Alias) ? Use.Reads : Use.Passes,
            MvExpandStage expand => Names(expand.Field) ? Use.Reads : Use.Passes,
            IplocationStage location => Names(location.Field) || IsWildcard(location.Prefix) ? Use.Reads : Use.Passes,
            ClusterStage cluster => cluster.Field == null || Names(cluster.Field) ? Use.Reads : cluster.LabelOnly ? Use.Passes : Use.Drops,

            // Stages that make new rows from the fields they name
            TableStage table => table.Fields.Count == 0 || NamesAny(table.Fields) ? Use.Reads : Use.Drops,
            StatsStage stats => Aggregates(stats.Aggregations) || NamesAny(stats.GroupByFields) ? Use.Reads : Use.Drops,
            ChartStage chart => Aggregates(chart.Aggregations) || Names(chart.OverField) || Names(chart.ByField) ? Use.Reads : Use.Drops,
            TimechartStage chart => Names(chart.AggregationField) || Names(chart.ByField) ? Use.Reads : Use.Drops,
            TopStage top => Names(top.Field) || Names(top.ByField) ? Use.Reads : Use.Drops,
            RareStage rare => Names(rare.Field) ? Use.Reads : Use.Drops,
            XyseriesStage xy => Names(xy.RowField) || Names(xy.ColumnField) || Names(xy.ValueField) ? Use.Reads : Use.Drops,

            // Searches read the text; untable, fieldsummary and transaction read every field;
            // subsearches, lookups, macros and anything else aren't worth guessing about
            _ => Use.Reads,
        };
    }

    private static bool IsWildcard(string name) => name.Contains('*', StringComparison.Ordinal);

    /// <summary>Whether evaluating the expression may read the text field.</summary>
    private static bool ExpressionReads(BqlExpression expression, BqlSchema schema) => expression switch
    {
        LiteralExpression => false,
        FieldExpression field => IsWildcard(field.Name) || schema.IsTextField(field.Name),
        FunctionExpression function => BqlExpressionEvaluator.ReadsTheWholeRow(function.Name)
            || function.Arguments.Any(argument => ExpressionReads(argument, schema)),
        ComparisonExpression comparison => ExpressionReads(comparison.Left, schema) || ExpressionReads(comparison.Right, schema),
        ArithmeticExpression arithmetic => ExpressionReads(arithmetic.Left, schema) || ExpressionReads(arithmetic.Right, schema),
        LogicalExpression logical => ExpressionReads(logical.Left, schema) || ExpressionReads(logical.Right, schema),
        NotExpression not => ExpressionReads(not.Operand, schema),
        InExpression @in => ExpressionReads(@in.Field, schema) || @in.Values.Any(value => ExpressionReads(value, schema)),
        LikeExpression like => ExpressionReads(like.Field, schema),
        _ => true,
    };
}
