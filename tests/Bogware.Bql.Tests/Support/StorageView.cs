namespace Bogware.Bql.Tests;

/// <summary>
/// The pushdown seen the way Cabinet's storage context showed it, so the ported optimizer tests
/// read as they did: the same questions, asked of the generic pushdown.
/// </summary>
public sealed class StorageView
{
    private readonly BqlPushdown _pushdown;

    public StorageView(BqlPushdown pushdown)
    {
        _pushdown = pushdown;
    }

    public string? SearchText => _pushdown.TextTerms is { Count: > 0 } terms ? string.Join(' ', terms) : null;

    public IReadOnlyList<string>? AllowedIndexes => _pushdown.Datasets;

    public string? Index => _pushdown.Datasets is [var only] ? only : null;

    public DateTime? StartTime => _pushdown.StartTime;

    public DateTime? EndTime => _pushdown.EndTime;

    public Dictionary<string, object?>? FieldFilters => null;

    public string SortField => _pushdown.Sort is { } sort && LogSchema.Instance.IsTimeField(sort.Field) ? "Timestamp" : _pushdown.Sort?.Field ?? "Timestamp";

    public bool SortDescending => _pushdown.Sort?.Descending ?? true;

    public string? Host => Equal("host");

    public string? SourceType => Equal("sourcetype") ?? Equal("source_type");

    public LogSeverity? MinSeverity
    {
        get
        {
            int? minimum = null;
            foreach (var p in _pushdown.Predicates.Where(p => p.Field.Equals("severity", StringComparison.OrdinalIgnoreCase)))
            {
                var value = Convert.ToDouble(p.Value, System.Globalization.CultureInfo.InvariantCulture);
                var level = p.Operator == ">" ? (int)Math.Floor(value) + 1 : (int)Math.Ceiling(value);
                minimum = Math.Max(level, minimum ?? 0);
            }

            return minimum is { } m ? (LogSeverity)m : null;
        }
    }

    private string? Equal(string field) =>
        _pushdown.Predicates.FirstOrDefault(p => p.Field.Equals(field, StringComparison.OrdinalIgnoreCase) && p.Operator == "=")?.Value as string;
}

public static class OptimizedQueryExtensions
{
    extension(OptimizedQuery optimized)
    {
        /// <summary>The pushdown, read as Cabinet's storage context.</summary>
        public StorageView StorageContext => new(optimized.Pushdown);
    }
}
