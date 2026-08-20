namespace Argon.Features.EF;

using Npgsql;
using System.Data.Common;

/// <summary>What one table turned out to be, or why the reader could not say.</summary>
public enum TtlObservationKind
{
    /// <summary>The table is not there yet. Not drift: the migration that creates it carries the TTL clause.</summary>
    Missing,

    /// <summary>The server answered and the answer was understood.</summary>
    Read,

    /// <summary>
    /// The server answered and the answer was not understood, or it refused to answer.
    /// </summary>
    /// <remarks>
    /// A verdict of its own rather than a fall-through to "no TTL", because the two are opposite in
    /// consequence: unreadable means the reconciler must report undetermined and issue nothing, while
    /// "no TTL" is a state it would try to converge. Collapsing them turns a permission error into an
    /// <c>ALTER</c> against a table nobody could see.
    /// </remarks>
    Unreadable
}

/// <summary>One table's observed TTL, or the reason there is none to report.</summary>
public sealed record TtlObservation(TtlObservationKind Kind, ObservedTtl? Ttl, string? Failure)
{
    public static readonly TtlObservation Missing = new(TtlObservationKind.Missing, null, null);

    public static TtlObservation Read(ObservedTtl ttl) => new(TtlObservationKind.Read, ttl, null);

    public static TtlObservation Unreadable(string reason) => new(TtlObservationKind.Unreadable, null, reason);
}

/// <summary>
/// The observed state: what CockroachDB currently says a table's row-level TTL is.
/// </summary>
/// <remarks>
/// <para><b>This parses <c>SHOW CREATE TABLE</c>, and there is no catalog read to prefer instead.</b>
/// That is worth stating plainly rather than dressing up, because the placement half of this design
/// went out of its way to avoid parsing and could not here. Row-level TTL is a <em>table storage
/// parameter</em>, and storage parameters have no representation in <c>information_schema</c> — the
/// standard has no such concept — while CockroachDB's <c>pg_catalog.pg_class.reloptions</c> is a
/// compatibility stub that does not carry them. That leaves <c>crdb_internal</c>, which the design
/// already rejected for three reasons that apply here unchanged: it is documented as not
/// production-safe and free to change between patch releases, it silently omits rows for tables the
/// caller has no privilege on, and its nullable columns cannot distinguish "no privilege" from "not
/// configured" — which is Argon's state on every table today and precisely the case that matters.</para>
///
/// <para>So: the documented statement, whose required privilege is "any privilege on the table" (the
/// app role has that by construction), whose response columns are documented, and which
/// <c>TablePlacementTests</c> already reads the same way — so the acceptance test and the reader share
/// a technique instead of agreeing by coincidence. The parser below is quote-aware rather than a
/// regular expression, because the values it has to skip past are SQL string literals that can contain
/// commas, parentheses and quotes of their own.</para>
/// </remarks>
public static class SchemaTtlCatalog
{
    /// <summary>Relation does not exist. Expected on a database whose migrations have not run yet.</summary>
    private const string UndefinedTable = "42P01";

    /// <summary>Insufficient privilege. Never converted into "no TTL" — see <see cref="TtlObservationKind.Unreadable"/>.</summary>
    private const string InsufficientPrivilege = "42501";

    /// <summary>
    /// Reads one table's storage parameters off the server.
    /// </summary>
    /// <remarks>
    /// A raw <see cref="DbCommand"/> on the connection the caller already opened, rather than
    /// <c>ExecuteSqlRaw</c>. Two reasons, and the second is the one that bites: EF would run this
    /// through the configured execution strategy, and <c>AddPooledDatabase</c> turns on
    /// <c>EnableRetryOnFailure</c> — which is right for queries and wrong for anything in this file,
    /// where a blind retry of a statement whose outcome is unknown is the failure mode the design
    /// spends all of section 5 avoiding. Using one mechanism for the reads and another for the writes
    /// would be worse than using the low-level one for both.
    /// </remarks>
    public async static Task<TtlObservation> ReadAsync(DbConnection connection, TableRef table, CancellationToken ct = default)
    {
        try
        {
            await using var command = connection.CreateCommand();

            // Quoted: Argon's table names are mixed case and an unquoted identifier folds to lower,
            // which would address a table that does not exist and be reported as "not created yet".
            command.CommandText = $"SHOW CREATE TABLE {table.Quoted}";

            // SHOW CREATE TABLE answers with (table_name, create_statement).
            await using var reader = await command.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return TtlObservation.Unreadable("SHOW CREATE TABLE returned no rows");

            return TryParse(reader.GetString(1), out var observed, out var failure)
                ? TtlObservation.Read(observed)
                : TtlObservation.Unreadable($"could not read the TTL clause of {table}: {failure}");
        }
        catch (PostgresException e) when (e.SqlState == UndefinedTable)
        {
            return TtlObservation.Missing;
        }
        catch (PostgresException e) when (e.SqlState == InsufficientPrivilege)
        {
            // Never folded into "no TTL". A reconciler that reports converged because it lacked the
            // permission to look is the worst failure available to it, and it is a silent one.
            return TtlObservation.Unreadable($"no privilege to read {table}: {e.MessageText}");
        }
    }

    /// <summary>
    /// Turns a <c>CREATE TABLE</c> rendering into the TTL it declares.
    /// </summary>
    /// <remarks>
    /// Public because it is the piece most worth testing and it needs no server: every interesting
    /// case is a string. Failure is a returned reason rather than an exception, because "the reader
    /// did not understand the answer" is a verdict the reconciler reports, not a crash on the boot path.
    /// </remarks>
    public static bool TryParse(string createStatement, out ObservedTtl observed, out string? failure)
    {
        observed = ObservedTtl.Off;

        if (!TryCollectStorageParameters(createStatement, out var ttl, out failure))
            return false;

        if (ttl.Count == 0)
            return true;

        var expirationExpression = Take(ttl, "ttl_expiration_expression");
        var expireAfter          = Take(ttl, "ttl_expire_after");
        var jobCron              = Take(ttl, "ttl_job_cron");
        var enabledFlag          = Take(ttl, "ttl");
        var paused               = Take(ttl, "ttl_pause");

        var enabled = expirationExpression is not null
                   || expireAfter is not null
                   || string.Equals(enabledFlag, "on", StringComparison.OrdinalIgnoreCase);

        // The one place this refuses rather than guesses. CockroachDB does not render `ttl = 'on'`
        // without also rendering what rows expire on, so seeing the first without the second means the
        // parser lost the value — and a lost expression compared against a declared one is drift the
        // reconciler would try to "fix" by rewriting a TTL that is already correct.
        if (enabled && expirationExpression is null && expireAfter is null)
        {
            failure = "the table reports ttl = 'on' with neither ttl_expiration_expression nor ttl_expire_after";
            return false;
        }

        if (!enabled)
            return true;

        if (!TryTakeInt(ttl, "ttl_select_batch_size", out var selectBatch, ref failure) ||
            !TryTakeInt(ttl, "ttl_delete_batch_size", out var deleteBatch, ref failure) ||
            !TryTakeInt(ttl, "ttl_delete_rate_limit", out var rateLimit, ref failure))
            return false;

        observed = new ObservedTtl(
            TtlSettings.Observed(expirationExpression, jobCron, selectBatch, deleteBatch, rateLimit),
            Paused: string.Equals(paused, "true", StringComparison.OrdinalIgnoreCase),
            ExpireAfter: expireAfter,
            OtherParameters: ttl);

        return true;
    }

    private static string? Take(Dictionary<string, string> parameters, string key)
        => parameters.Remove(key, out var value) ? value : null;

    private static bool TryTakeInt(Dictionary<string, string> parameters, string key, out int? value, ref string? failure)
    {
        value = null;

        var raw = Take(parameters, key);

        if (raw is null)
            return true;

        if (!int.TryParse(raw, out var parsed))
        {
            failure = $"{key} is '{raw}', which is not a number";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Every <c>ttl_</c> storage parameter in the statement, from whichever <c>WITH (...)</c> carries it.
    /// </summary>
    /// <remarks>
    /// <para>All the <c>WITH</c> clauses rather than a chosen one, because a rendering may carry more
    /// than one — an index can have storage parameters of its own — and picking a group by position
    /// would be a guess about output that is free to change between releases. TTL parameter names are
    /// unique across a table, so the same one appearing twice with two different values means the scan
    /// went wrong and is reported as a parse failure rather than resolved by preference.</para>
    ///
    /// <para>Non-TTL parameters are dropped at the point of collection rather than filtered afterwards,
    /// so that two indexes legitimately carrying the same parameter with different values cannot be
    /// mistaken for that failure.</para>
    /// </remarks>
    private static bool TryCollectStorageParameters(
        string sql, out Dictionary<string, string> parameters, out string? failure)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        failure    = null;

        foreach (var group in EnumerateWithGroups(sql, ref failure))
        {
            foreach (var item in SplitTopLevel(group, ','))
            {
                var separator = IndexOfTopLevel(item, '=');
                if (separator < 0)
                    continue;

                var key = item[..separator].Trim().ToLowerInvariant();

                if (!key.StartsWith("ttl", StringComparison.Ordinal))
                    continue;

                var value = DecodeValue(item[(separator + 1)..]);

                if (parameters.TryGetValue(key, out var existing) && existing != value)
                {
                    failure = $"'{key}' appears twice with different values ('{existing}' and '{value}')";
                    return false;
                }

                parameters[key] = value;
            }
        }

        return failure is null;
    }

    private static List<string> EnumerateWithGroups(string sql, ref string? failure)
    {
        var groups = new List<string>();

        for (var i = 0; i < sql.Length;)
        {
            if (SkipLiteral(sql, ref i, ref failure))
            {
                if (failure is not null)
                    return groups;
                continue;
            }

            if (!IsKeywordAt(sql, i, "with"))
            {
                i++;
                continue;
            }

            var open = i + 4;
            while (open < sql.Length && char.IsWhiteSpace(sql[open]))
                open++;

            if (open >= sql.Length || sql[open] != '(')
            {
                i += 4;
                continue;
            }

            if (!TryReadBalanced(sql, open, out var content, out var end, ref failure))
                return groups;

            groups.Add(content);
            i = end;
        }

        return groups;
    }

    /// <summary>
    /// Advances past a string literal or delimited identifier, reporting whether it moved.
    /// </summary>
    /// <remarks>
    /// The <c>e'…'</c> form is separated from the plain one because the escape rules differ and getting
    /// it backwards ends the literal early: CockroachDB runs with <c>standard_conforming_strings</c> on,
    /// so a backslash inside <c>'…'</c> is an ordinary character and only <c>''</c> ends nothing, while
    /// inside <c>e'…'</c> a backslash escapes the next character. Treating every literal as
    /// backslash-escaped would swallow the closing quote of a value like <c>'a\'</c> and run the scan
    /// off into the rest of the statement.
    /// </remarks>
    private static bool SkipLiteral(string sql, ref int i, ref string? failure)
    {
        var c = sql[i];

        if (c == '"')
        {
            i++;
            while (i < sql.Length)
            {
                if (sql[i] != '"')
                {
                    i++;
                    continue;
                }

                if (i + 1 < sql.Length && sql[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                i++;
                return true;
            }

            failure = "unterminated quoted identifier";
            return true;
        }

        var escaped = (c is 'e' or 'E') && i + 1 < sql.Length && sql[i + 1] == '\'' && !IsWordCharacter(sql, i - 1);

        if (c != '\'' && !escaped)
            return false;

        i += escaped ? 2 : 1;

        while (i < sql.Length)
        {
            if (escaped && sql[i] == '\\' && i + 1 < sql.Length)
            {
                i += 2;
                continue;
            }

            if (sql[i] != '\'')
            {
                i++;
                continue;
            }

            if (i + 1 < sql.Length && sql[i + 1] == '\'')
            {
                i += 2;
                continue;
            }

            i++;
            return true;
        }

        failure = "unterminated string literal";
        return true;
    }

    private static bool TryReadBalanced(string sql, int open, out string content, out int end, ref string? failure)
    {
        content = "";
        end     = open;

        var depth = 0;

        for (var i = open; i < sql.Length;)
        {
            if (SkipLiteral(sql, ref i, ref failure))
            {
                if (failure is not null)
                    return false;
                continue;
            }

            if (sql[i] == '(')
                depth++;
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    content = sql[(open + 1)..i];
                    end     = i + 1;
                    return true;
                }
            }

            i++;
        }

        failure = "unbalanced parentheses in a WITH clause";
        return false;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        string? ignored = null;

        for (var i = 0; i < text.Length;)
        {
            if (SkipLiteral(text, ref i, ref ignored))
                continue;

            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
                depth--;
            else if (text[i] == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }

            i++;
        }

        parts.Add(text[start..]);

        return parts;
    }

    private static int IndexOfTopLevel(string text, char needle)
    {
        var depth = 0;
        string? ignored = null;

        for (var i = 0; i < text.Length;)
        {
            if (SkipLiteral(text, ref i, ref ignored))
                continue;

            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
                depth--;
            else if (text[i] == needle && depth == 0)
                return i;

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The text a storage parameter's value stands for.
    /// </summary>
    /// <remarks>
    /// The <c>:::TYPE</c> suffix is CockroachDB annotating its own rendering — <c>'3 mons':::INTERVAL</c>
    /// — and it is dropped rather than compared, because it is a property of how the server chose to
    /// print the value and not of the value. Keeping it would make every declared string differ from
    /// every observed one.
    /// </remarks>
    private static string DecodeValue(string raw)
    {
        var text = raw.Trim();

        if (text.Length == 0)
            return text;

        var escaped = (text[0] is 'e' or 'E') && text.Length > 1 && text[1] == '\'';
        var quoted  = text[0] == '\'';

        if (!quoted && !escaped)
        {
            var annotation = text.IndexOf(":::", StringComparison.Ordinal);
            return annotation < 0 ? text : text[..annotation].Trim();
        }

        var builder = new StringBuilder();

        for (var i = escaped ? 2 : 1; i < text.Length; i++)
        {
            if (escaped && text[i] == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
                continue;
            }

            if (text[i] != '\'')
            {
                builder.Append(text[i]);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                builder.Append('\'');
                i++;
                continue;
            }

            break;
        }

        return builder.ToString();
    }

    private static bool IsKeywordAt(string sql, int index, string keyword)
    {
        if (index + keyword.Length > sql.Length)
            return false;

        if (string.Compare(sql, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        return !IsWordCharacter(sql, index - 1) && !IsWordCharacter(sql, index + keyword.Length);
    }

    private static bool IsWordCharacter(string sql, int index)
        => index >= 0 && index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$');
}
