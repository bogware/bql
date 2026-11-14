namespace Bogware.Bql;

/// <summary>
/// The size units a number may carry in an expression, as in <c>size &gt; 10MB</c>. They are part
/// of the language's meaning, so they are constants. Units count in 1024s, as file managers on
/// Windows show sizes: <c>KB</c> and <c>KiB</c> are both 1,024 bytes. Case doesn't matter.
/// </summary>
internal static class BqlSizes
{
    private const long Kibi = 1024;

    private static readonly Dictionary<string, long> Multipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1,
        ["KB"] = Kibi,
        ["KiB"] = Kibi,
        ["MB"] = Kibi * Kibi,
        ["MiB"] = Kibi * Kibi,
        ["GB"] = Kibi * Kibi * Kibi,
        ["GiB"] = Kibi * Kibi * Kibi,
        ["TB"] = Kibi * Kibi * Kibi * Kibi,
        ["TiB"] = Kibi * Kibi * Kibi * Kibi,
        ["PB"] = Kibi * Kibi * Kibi * Kibi * Kibi,
        ["PiB"] = Kibi * Kibi * Kibi * Kibi * Kibi,
    };

    /// <summary>The bytes in one of a unit, if the word is a size unit.</summary>
    public static bool TryGetMultiplier(string unit, out long bytes) => Multipliers.TryGetValue(unit, out bytes);
}
