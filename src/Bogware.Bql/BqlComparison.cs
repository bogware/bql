namespace Bogware.Bql;

/// <summary>
/// The engine's comparison, for a source that applies predicates itself. A source that claims a
/// predicate through <see cref="BqlSourceCapabilities.ExactPredicates"/> can call this to compare
/// exactly as <c>where</c> does, so its answer can't drift from the pipeline's.
/// </summary>
public static class BqlComparison
{
    /// <summary>The operators <see cref="Holds"/> takes.</summary>
    public static IReadOnlyList<string> Operators { get; } = ["=", "==", "!=", "<>", ">", ">=", "<", "<="];

    /// <summary>
    /// Whether <c>where field op literal</c> keeps a row whose field holds <paramref name="left"/>,
    /// when the literal is <paramref name="right"/> (as <see cref="BqlPredicate.Value"/> carries it).
    /// </summary>
    /// <remarks>
    /// The rules are the engine's, because this is the engine's code: a missing value never
    /// matches, <c>!=</c> included; a multi-value matches when any value does, and <c>!=</c> when
    /// none equals; an empty list counts as missing; numbers compare as numbers, equal within a
    /// small tolerance; a time compared with date text compares as times; other text compares
    /// ignoring case.
    /// </remarks>
    /// <param name="op">One of <see cref="Operators"/>.</param>
    /// <param name="left">The row's value: null when the field is missing, a list for a multi-value.</param>
    /// <param name="right">The literal.</param>
    /// <exception cref="ArgumentException">The operator isn't one of <see cref="Operators"/>.</exception>
    public static bool Holds(string op, object? left, object? right)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (!Operators.Contains(op, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{op}' isn't a comparison operator.", nameof(op));
        }

        return BqlExpressionEvaluator.Compare(op, left, right);
    }
}
