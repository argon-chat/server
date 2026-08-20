namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// The desired state: what the live EF model says each table's row-level TTL should be.
/// </summary>
/// <remarks>
/// <para>Read from <c>DbContext.Model</c> at runtime, and from nowhere else. Not from a migration, not
/// from a <c>.Designer.cs</c> snapshot, not from <c>IDesignTimeModel</c> — those are the reason the
/// defect exists. The generator emits the TTL clause only inside <c>CreateTableOperation</c>, so an
/// annotation added to a table that already exists produces no operation, never reaches a migration,
/// and is therefore absent from every snapshot in the history. The declaration only exists in the
/// model, which is why the model is what gets read.</para>
///
/// <para>Custom annotations survive EF Core 10's runtime-model pruning: <c>RuntimeModelConvention</c>
/// strips the keys it knows about, and <c>Job:Expiration</c> is not one of them. So this needs no
/// design-time package and no scaffolding, and it can be exercised against the real
/// <c>ApplicationDbContext</c> in a test suite with no database in it.</para>
/// </remarks>
public static class SchemaTtlModel
{
    /// <summary>
    /// What the model declares, keyed by the table it is declared on.
    /// </summary>
    /// <remarks>
    /// <para>Keyed by <see cref="IReadOnlyEntityType.GetTableName"/> rather than by the <c>DbSet</c>
    /// property or the CLR type, and the difference is not theoretical in this model: two of the three
    /// TTL entities have no <c>ToTable</c> at all, so <c>SpaceInvite</c> lands in <c>Invites</c> and
    /// <c>DevTeamMemberInvite</c> in <c>TeamInvites</c>. A reconciler keyed on anything else would
    /// issue <c>ALTER TABLE "SpaceInvite"</c> against a table that does not exist — and a missing table
    /// is reported as "not created yet", which reads like good news.</para>
    ///
    /// <para>Two entity types on one table declaring two different TTLs throws rather than picking one.
    /// The model has TPH inheritance, so several entity types genuinely do map to one table, and
    /// <c>MultiregionalMigrationsSqlGenerator</c>'s <c>FirstOrDefault</c> resolves that by model-build
    /// order — which would make when rows get deleted depend on the order entity configurations were
    /// registered in. Silence with the wrong answer is the failure mode; a build that stops with both
    /// names in the message is the fix.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Two entity types on one table declare different TTLs, or an annotation payload cannot be read.
    /// </exception>
    public static IReadOnlyDictionary<TableRef, TtlSettings> ReadDesiredState(IModel model)
    {
        var declarations = new Dictionary<TableRef, (string Owner, TtlSettings Settings)>();

        foreach (var entityType in model.GetEntityTypes())
        {
            // Owned types, query types and anything mapped to a view have no table to alter.
            if (entityType.GetTableName() is not { Length: > 0 } table)
                continue;

            if (entityType.FindAnnotation(ExpirationJobAnnotation.AnnotationKey)?.Value is not string payload)
                continue;

            var key      = new TableRef(entityType.GetSchema() ?? TableRef.DefaultSchema, table);
            var declared = Parse(payload, entityType.Name);

            if (!declarations.TryGetValue(key, out var existing))
            {
                declarations[key] = (entityType.Name, declared);
                continue;
            }

            if (existing.Settings == declared)
                continue;

            throw new InvalidOperationException(
                $"'{existing.Owner}' and '{entityType.Name}' both map to table {key} and declare different " +
                $"row-level TTLs ({Describe(existing.Settings)} vs {Describe(declared)}). One table has one " +
                "TTL; resolve the disagreement in the entity configurations rather than letting model-build " +
                "order decide which rows get deleted.");
        }

        return declarations.ToDictionary(pair => pair.Key, pair => pair.Value.Settings);
    }

    /// <summary>
    /// The annotation the model carries, as the declaration it stands for.
    /// </summary>
    /// <remarks>
    /// The payload is the JSON <c>WithTTL</c> wrote. It is deserialised rather than pattern-matched
    /// because it is the same shape the generator reads, and two readers of one payload disagreeing
    /// about its shape is the class of bug this whole exercise is about.
    /// </remarks>
    private static TtlSettings Parse(string payload, string owner)
    {
        ExpirationJobAnnotation? annotation;

        try
        {
            annotation = JsonConvert.DeserializeObject<ExpirationJobAnnotation>(payload);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"'{owner}' carries a '{ExpirationJobAnnotation.AnnotationKey}' annotation that is not a " +
                $"readable {nameof(ExpirationJobAnnotation)}: {payload}", e);
        }

        if (annotation is null)
            throw new InvalidOperationException(
                $"'{owner}' carries an empty '{ExpirationJobAnnotation.AnnotationKey}' annotation.");

        if (string.IsNullOrWhiteSpace(annotation.TimestampKey))
            throw new InvalidOperationException(
                $"'{owner}' declares a row-level TTL with no expiration column. CockroachDB has nothing to " +
                "compare a row against and would refuse the statement.");

        return TtlSettings.Declared(
            annotation.TimestampKey,
            annotation.CronValue?.value,
            annotation.SelectBatchSize,
            annotation.DeleteBatchSize,
            annotation.DeleteRateLimit);
    }

    /// <summary>One line a human can read in an exception or a log, not a serialisation format.</summary>
    public static string Describe(TtlSettings settings)
    {
        if (!settings.Enabled)
            return "no TTL";

        var parts = new List<string>
        {
            $"expires on \"{settings.ExpirationExpression}\"",
            $"cron '{settings.JobCron}'"
        };

        if (settings.SelectBatchSize is { } select)
            parts.Add($"select batch {select}");
        if (settings.DeleteBatchSize is { } delete)
            parts.Add($"delete batch {delete}");
        if (settings.DeleteRateLimit is { } rate)
            parts.Add($"delete rate limit {rate}");

        return string.Join(", ", parts);
    }
}
