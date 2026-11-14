namespace Bogware.Bql;

/// <summary>
/// What the engine needs to know about a source's rows: which field holds the time, which holds
/// the text that words search, what other names fields go by, and which fields have ordered named
/// levels. Everything here is data. The engine has no field names of its own beyond the SPL
/// defaults <c>_time</c> and <c>_raw</c>.
/// </summary>
public sealed class BqlSchema
{
    /// <summary>The SPL name for a row's time.</summary>
    public const string DefaultTimeField = "_time";

    /// <summary>The SPL name for a row's text.</summary>
    public const string DefaultTextField = "_raw";

    private BqlFieldComparer? _comparer;

    /// <summary>A schema with no fields of its own, using <c>_time</c> and <c>_raw</c>.</summary>
    public static BqlSchema Default { get; } = new();

    /// <summary>
    /// The field holding each row's time. Time bounds, <c>timechart</c>, <c>earliest()</c> and
    /// <c>latest()</c> read it, and <c>timechart</c> writes its buckets under it.
    /// </summary>
    public string TimeField { get; init; } = DefaultTimeField;

    /// <summary>Other fields that hold the same time, such as <c>Timestamp</c> beside <c>_time</c>.</summary>
    public IReadOnlyList<string> TimeFieldSynonyms { get; init; } = [];

    /// <summary>
    /// The field that bare words and phrases search, and that <c>rex</c>, <c>regex</c> and
    /// <c>cluster</c> read when no field is named.
    /// </summary>
    public string TextField { get; init; } = DefaultTextField;

    /// <summary>Other fields that hold the same text, such as <c>RawLine</c> beside <c>_raw</c>.</summary>
    public IReadOnlyList<string> TextFieldSynonyms { get; init; } = [];

    /// <summary>
    /// The field that names a row's dataset, such as <c>index</c>. A search for one value of it
    /// narrows what the source reads. Null when the source has no datasets.
    /// </summary>
    public string? DatasetField { get; init; }

    /// <summary>The fields rows usually have. Used for completion, validation hints and docs.</summary>
    public IReadOnlyList<BqlField> Fields { get; init; } = [];

    /// <summary>
    /// Other names for fields: alias to the canonical name. Rows built with <see cref="NewRow"/>
    /// find a field by any of its names, ignoring case.
    /// </summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Fields whose values are ordered levels with names, such as a severity. In
    /// <c>severity &gt;= warning</c> the name becomes the level's number.
    /// </summary>
    public IReadOnlyDictionary<string, BqlOrdinalScale> OrdinalFields { get; init; } = new Dictionary<string, BqlOrdinalScale>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fields a subsearch never filters by, because every row has them. Defaults to the time and
    /// text fields and their synonyms.
    /// </summary>
    public IReadOnlyCollection<string>? InternalFields { get; init; }

    /// <summary>
    /// The order asked of the source when a query doesn't sort: by time, newest first. A source
    /// that can't sort by time returns rows in its own order, and a query that cares sorts.
    /// </summary>
    public BqlSortOrder DefaultSort { get; init; } = new(DefaultTimeField, Descending: true);

    /// <summary>A comparer that matches field names as rows of this schema do.</summary>
    public IEqualityComparer<string> FieldComparer => _comparer ??= Aliases.Count == 0 ? BqlFieldComparer.Plain : new BqlFieldComparer(Aliases);

    /// <summary>A new, empty row for this schema.</summary>
    public BqlRow NewRow() => new(FieldComparer);

    /// <summary>Whether a name is the time field or one of its synonyms.</summary>
    public bool IsTimeField(string name) =>
        FieldComparer.Equals(name, TimeField) || TimeFieldSynonyms.Any(s => FieldComparer.Equals(name, s));

    /// <summary>Whether a name is the text field or one of its synonyms.</summary>
    public bool IsTextField(string name) =>
        FieldComparer.Equals(name, TextField) || TextFieldSynonyms.Any(s => FieldComparer.Equals(name, s));

    /// <summary>Whether a field is one a subsearch leaves out of its filter.</summary>
    public bool IsInternalField(string name) =>
        InternalFields is { } listed
            ? listed.Any(f => FieldComparer.Equals(f, name))
            : IsTimeField(name) || IsTextField(name);

    /// <summary>The ordered levels of a field, or null if it has none.</summary>
    public BqlOrdinalScale? OrdinalScale(string field) =>
        OrdinalFields.TryGetValue(field, out var scale) ? scale
        : Aliases.TryGetValue(field, out var canonical) && OrdinalFields.TryGetValue(canonical, out scale) ? scale
        : null;

    /// <summary>
    /// A row's time: the time field, which <c>eval</c> or <c>bin</c> may have changed, else a
    /// synonym. Epoch seconds and date text count as times.
    /// </summary>
    public DateTime? TimeOf(IDictionary<string, object?> row)
    {
        if (Read(row, TimeField) is { } time)
        {
            return time;
        }

        foreach (var synonym in TimeFieldSynonyms)
        {
            if (Read(row, synonym) is { } other)
            {
                return other;
            }
        }

        return null;

        static DateTime? Read(IDictionary<string, object?> row, string field)
        {
            if (!row.TryGetValue(field, out var value) || value == null)
            {
                return null;
            }

            var epoch = value switch
            {
                double d => d,
                long l => l,
                int i => i,
                string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (double?)null,
            };

            return BqlTime.FromValue(value, epoch) is { } result && result != DateTime.MinValue ? result : null;
        }
    }

    /// <summary>A row's text: the text field, else a synonym.</summary>
    public string? TextOf(IDictionary<string, object?> row)
    {
        if (row.TryGetValue(TextField, out var text) && text != null)
        {
            return text.ToString();
        }

        foreach (var synonym in TextFieldSynonyms)
        {
            if (row.TryGetValue(synonym, out var other) && other != null)
            {
                return other.ToString();
            }
        }

        return null;
    }
}

/// <summary>A field rows usually have.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Type">What its values are.</param>
/// <param name="Description">One line for people, or null.</param>
public sealed record BqlField(string Name, BqlFieldType Type, string? Description = null);

/// <summary>What a field's values are.</summary>
public enum BqlFieldType
{
    String,
    Integer,
    Number,
    Boolean,
    Time,
    StringList,
    Any,
}

/// <summary>An order to read rows in.</summary>
/// <param name="Field">The field to sort by.</param>
/// <param name="Descending">Largest first.</param>
public sealed record BqlSortOrder(string Field, bool Descending);

/// <summary>Named levels of a field, in order, such as debug &lt; info &lt; warning.</summary>
public sealed class BqlOrdinalScale
{
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes a scale from levels. Each level's names all map to its value.</summary>
    public BqlOrdinalScale(IEnumerable<BqlOrdinalLevel> levels)
    {
        Levels = levels.ToList();
        foreach (var level in Levels)
        {
            foreach (var name in level.Names)
            {
                _byName[name] = level.Value;
            }
        }
    }

    /// <summary>The levels, lowest first.</summary>
    public IReadOnlyList<BqlOrdinalLevel> Levels { get; }

    /// <summary>The value of a level name, if it is one.</summary>
    public bool TryGetValue(string name, out int value) => _byName.TryGetValue(name, out value);
}

/// <summary>One named level.</summary>
/// <param name="Value">Its place in the order.</param>
/// <param name="Names">What it's called; the first is its usual name.</param>
public sealed record BqlOrdinalLevel(int Value, IReadOnlyList<string> Names);
