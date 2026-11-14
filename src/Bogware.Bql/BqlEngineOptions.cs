namespace Bogware.Bql;

/// <summary>
/// Everything an engine can be tuned by. Each value has a default that suits one machine and an
/// interactive user; hosts expose the ones their users should see.
/// </summary>
public sealed class BqlEngineOptions
{
    /// <summary>Memory limits for one query.</summary>
    public BqlLimits Limits { get; init; } = new();

    /// <summary>Rows returned when the caller doesn't say. Default 10,000.</summary>
    public int DefaultResultLimit { get; init; } = 10_000;

    /// <summary>How long a query may run when the caller doesn't say. Default 60 seconds.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Rows passed through row stages at a time. Default 10,000.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>How much each batch grows over the one before, up to <see cref="BatchSize"/>. Default 4.</summary>
    public int BatchGrowthFactor { get; init; } = 4;

    /// <summary>
    /// The smallest first read when a stage needs only a few rows, so <c>| head 5</c> doesn't
    /// read a full batch. Default 100.
    /// </summary>
    public int MinimumReadHint { get; init; } = 100;

    /// <summary>Rows a <c>join</c>, <c>append</c>, subsearch or multisearch leg may return. Default 50,000.</summary>
    public int SubsearchResultLimit { get; init; } = 50_000;

    /// <summary>
    /// How deeply subsearches (<c>append</c>, <c>join</c>, <c>multisearch</c>, <c>subsearch</c>) and
    /// macros may nest, counted together, so a macro that reaches itself through a subsearch stops
    /// with an error instead of exhausting the stack. Default 10.
    /// </summary>
    public int MaxNestingDepth { get; init; } = 10;

    /// <summary>Longest a macro may expand to, in characters. Default 100,000.</summary>
    public int MaxMacroExpansionLength { get; init; } = 100_000;

    /// <summary>Limits on what the parser accepts.</summary>
    public BqlParserLimits Parser { get; init; } = new();

    /// <summary>
    /// Lookup tables and macros, or null when the host has none. Names come from query text and
    /// are untrusted; see <see cref="IBqlKnowledgeProvider"/>.
    /// </summary>
    public IBqlKnowledgeProvider? Knowledge { get; init; }

    /// <summary>Finds where IP addresses are, for <c>iplocation</c>, or null.</summary>
    public IBqlGeoIpLookup? GeoIp { get; init; }

    /// <summary>Other query languages the engine accepts, by name.</summary>
    public IReadOnlyList<IBqlDialect> Dialects { get; init; } = [];

    /// <summary>Called with things worth logging. The engine itself writes nothing.</summary>
    public Action<BqlDiagnostic>? Diagnostics { get; init; }
}

/// <summary>Limits on what the parser accepts, which keep a hostile query from using up the process.</summary>
public sealed class BqlParserLimits
{
    /// <summary>Longest query, in characters. Default 100,000.</summary>
    public int MaxQueryLength { get; init; } = 100_000;

    /// <summary>Most tokens in one query. Default 10,000.</summary>
    public int MaxTokenCount { get; init; } = 10_000;

    /// <summary>Longest string literal. Default 1,000,000 characters.</summary>
    public int MaxStringLiteralLength { get; init; } = 1_000_000;

    /// <summary>Most stages in one pipeline. Default 100.</summary>
    public int MaxPipelineStages { get; init; } = 100;

    /// <summary>Deepest nesting of parentheses and sub-parsers. Default 100.</summary>
    public int MaxRecursionDepth { get; init; } = 100;

    /// <summary>
    /// Deepest expression. A chain like <c>1+1+...</c> nests one level per operator, and
    /// evaluating it recurses that deep; this keeps it well short of a stack overflow. Default 1,000.
    /// </summary>
    public int MaxExpressionDepth { get; init; } = 1_000;

    /// <summary>Deepest nesting of subsearch brackets in the text. Default 20.</summary>
    public int MaxSubsearchDepth { get; init; } = 20;

    /// <summary>Deepest nesting of parentheses in a search's words, such as <c>(a OR (b AND c))</c>. Default 100.</summary>
    public int MaxSearchDepth { get; init; } = 100;

    /// <summary>Largest <c>transaction maxevents=</c>. Default 100,000.</summary>
    public int MaxTransactionEvents { get; init; } = 100_000;
}

/// <summary>Something the engine noticed that a host may want to log.</summary>
/// <param name="Level">How much it matters.</param>
/// <param name="Message">What happened.</param>
/// <param name="Exception">The error, if there was one.</param>
public sealed record BqlDiagnostic(BqlDiagnosticLevel Level, string Message, Exception? Exception = null);

/// <summary>How much a diagnostic matters.</summary>
public enum BqlDiagnosticLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>
/// Lookup tables and macros a host keeps for its users.
/// <para>
/// Names come straight from query text: treat them as untrusted input. Never use one as a file
/// path or in a query of your own without checking it. The caller argument says who the query
/// runs for, so a provider shared by several callers can return only what that caller may see.
/// </para>
/// </summary>
public interface IBqlKnowledgeProvider
{
    /// <summary>A lookup table by name, or null when there is none or the caller may not read it.</summary>
    Task<BqlLookupTable?> GetLookupAsync(string name, BqlCaller caller, CancellationToken cancellationToken = default);

    /// <summary>A macro by name, or null when there is none or the caller may not use it.</summary>
    Task<BqlMacro?> GetMacroAsync(string name, BqlCaller caller, CancellationToken cancellationToken = default);
}

/// <summary>A table of values, for <c>lookup</c> and <c>inputlookup</c>.</summary>
public sealed class BqlLookupTable
{
    /// <summary>Its name.</summary>
    public required string Name { get; init; }

    /// <summary>Its columns, in order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Its rows, by column name.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];
}

/// <summary>A named piece of pipeline, with <c>$parameter$</c> placeholders.</summary>
public sealed class BqlMacro
{
    /// <summary>Its name.</summary>
    public required string Name { get; init; }

    /// <summary>The pipeline it stands for, such as <c>where status &gt;= 500 | stats count by path</c>.</summary>
    public required string Definition { get; init; }

    /// <summary>Its parameters, in the order arguments are given.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];
}

/// <summary>Finds where an IP address is, from a GeoIP database the host supplies.</summary>
public interface IBqlGeoIpLookup
{
    /// <summary>Where the address is, or null for one the database doesn't know, such as a private one.</summary>
    BqlGeoLocation? Lookup(string address);
}

/// <summary>Where an IP address is. Any part the database doesn't have is null.</summary>
public sealed record BqlGeoLocation(string? City, string? Region, string? Country, string? CountryCode, double? Latitude, double? Longitude);

/// <summary>
/// Another query language, read into BQL. A dialect may build the query itself, or write BQL text
/// and parse it with <see cref="BqlDialectContext.Parse"/>.
/// </summary>
public interface IBqlDialect
{
    /// <summary>The name callers pass in <see cref="BqlExecutionContext.Dialect"/>, such as <c>sql</c>.</summary>
    string Name { get; }

    /// <summary>Reads text in this language. Returns false, with reasons, when it can't.</summary>
    bool TryTranslate(string text, BqlDialectContext context, out BqlQuery? query, out IReadOnlyList<string> diagnostics);
}

/// <summary>What a dialect can use while it translates.</summary>
public sealed class BqlDialectContext
{
    private readonly BqlParser _parser;

    internal BqlDialectContext(BqlSchema schema, BqlParser parser)
    {
        Schema = schema;
        _parser = parser;
    }

    /// <summary>The source's schema.</summary>
    public BqlSchema Schema { get; }

    /// <summary>Parses BQL text the dialect wrote.</summary>
    public BqlQuery Parse(string bql) => _parser.ParseBql(bql);
}
