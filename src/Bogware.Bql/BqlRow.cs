namespace Bogware.Bql;

/// <summary>
/// One row flowing through a BQL pipeline. Field names match without regard to case, and through
/// the aliases of the schema that made the row, so <c>host</c>, <c>Host</c> and <c>HOST</c> are
/// the same field in every stage.
/// </summary>
public sealed class BqlRow : Dictionary<string, object?>
{
    /// <summary>A row that matches names ignoring case, with no aliases.</summary>
    public BqlRow()
        : base(BqlFieldComparer.Plain)
    {
    }

    /// <summary>A row that matches names with the given comparer, usually a schema's.</summary>
    public BqlRow(IEqualityComparer<string> comparer)
        : base(comparer)
    {
    }
}

/// <summary>Matches field names ignoring case, and through a set of aliases.</summary>
public sealed class BqlFieldComparer : IEqualityComparer<string>
{
    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<int> _aliasLengths;

    /// <summary>Ignores case, with no aliases.</summary>
    public static BqlFieldComparer Plain { get; } = new(new Dictionary<string, string>());

    /// <summary>A comparer for these aliases: each alias maps to its canonical name.</summary>
    public BqlFieldComparer(IReadOnlyDictionary<string, string> aliases)
    {
        _aliases = new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
        _aliasLengths = _aliases.Keys.Select(k => k.Length).ToHashSet();
    }

    // Called for every field of every row. Checking the length first saves hashing most names twice.
    private string Canonical(string name) =>
        _aliasLengths.Contains(name.Length) && _aliases.TryGetValue(name, out var canonical) ? canonical : name;

    /// <inheritdoc />
    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(Canonical(x), Canonical(y));
    }

    /// <inheritdoc />
    public int GetHashCode(string name) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical(name));
}
