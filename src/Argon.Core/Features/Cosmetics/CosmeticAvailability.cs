namespace Argon.Features.Cosmetics;

using System.Linq.Expressions;
using Argon.Entities;

/// <summary>
/// Whether a catalogue row may be served at all right now: published, switched on, and inside its
/// availability window.
/// </summary>
/// <remarks>
/// <para><b>One rule, because it is one rule.</b> The catalogue serves exactly these rows, equipping
/// refuses everything else, an axis option asks the same question, and a key may only be spent on
/// something the answer is true for. Stated separately in each of those places it drifts — one copy
/// learns that a closed window is not servable and another goes on rendering it — and the visible
/// result is a cosmetic that cannot be picked but is still worn.</para>
///
/// <para><b>Two dialects, one statement.</b> <see cref="ServableAt"/> is for the database, which has
/// to filter before it reads; <see cref="IsServable"/> is for a row already in hand. Both are
/// derived from <see cref="Rule"/>, so neither can come to mean something the other does not.</para>
///
/// <para>The moment is a parameter rather than a read of the clock: the answer changes during a
/// request, and every caller is already deciding several things as of one instant.</para>
/// </remarks>
public static class CosmeticAvailability
{
    private static readonly Expression<Func<CosmeticItemEntity, DateTimeOffset, bool>> Rule =
        (item, now) => item.IsPublished
                    && item.IsEnabled
                    && (item.AvailableFrom == null || item.AvailableFrom <= now)
                    && (item.AvailableUntil == null || item.AvailableUntil > now);

    private static readonly Func<CosmeticItemEntity, DateTimeOffset, bool> Compiled = Rule.Compile();

    public static bool IsServable(CosmeticItemEntity item, DateTimeOffset now) => Compiled(item, now);

    /// <summary>The same rule as a predicate a query can carry, with <paramref name="now"/> bound into it.</summary>
    public static Expression<Func<CosmeticItemEntity, bool>> ServableAt(DateTimeOffset now)
        => Rule.BindTrailing(now);
}

/// <summary>
/// Turns a rule of two things into a rule of one, by fixing the second.
/// </summary>
/// <remarks>
/// <para>EF has no way to translate a call to an ordinary method, however pure it is, so a rule a
/// query can carry has to be an expression tree — and a rule that must also run in memory would
/// otherwise be written twice and drift. Writing it once with the varying part as a parameter, and
/// fixing that parameter here, is what keeps the two readings the same text.</para>
///
/// <para>The value goes in as a captured local rather than as a constant deliberately: a constant is
/// written into the SQL as a literal, so every distinct instant would earn its own entry in the
/// query cache and its own plan. A capture is the shape an ordinary closure already has, and EF
/// turns it into a parameter.</para>
/// </remarks>
public static class CosmeticRuleExpressions
{
    public static Expression<Func<T, bool>> BindTrailing<T, TFixed>(
        this Expression<Func<T, TFixed, bool>> rule,
        TFixed value)
    {
        Expression<Func<TFixed>> captured = () => value;

        return Expression.Lambda<Func<T, bool>>(
            new Substitute(rule.Parameters[1], captured.Body).Visit(rule.Body)!,
            rule.Parameters[0]);
    }

    private sealed class Substitute(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == parameter ? replacement : node;
    }
}
