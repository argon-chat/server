namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Operations;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

#pragma warning disable EF1001

public class MultiRegionAnnotation
{
    public required string   Primary { get; init; }
    public required string[] Regions { get; init; }
    public required string   Survive { get; init; }
}

public class ExpirationJobAnnotation
{
    // ttl_expiration_expression 
    public required string TimestampKey { get; init; }

    // ttl_job_cron 
    public CronValue CronValue { get; set; }

    // ttl_select_batch_size 
    public int SelectBatchSize { get; set; }
    // ttl_delete_batch_size 
    public int DeleteBatchSize { get; set; }
    // ttl_range_concurrency 
    public int RangeConcurrency { get; set; }
    // ttl_delete_rate_limit 
    public int DeleteRateLimit { get; set; }
}

public record CronValue(string value)
{
    public static readonly CronValue Daily   = "0 0 * * *";
    public static readonly CronValue Weekly  = "0 0 * * 0";
    public static readonly CronValue Monthly = "0 0 1 * *";
    public static readonly CronValue Yearly  = "0 0 1 1 *";

    public static implicit operator CronValue(string v) => new(v);
}

public static class DbLocalityExtensions
{
    public static ModelBuilder UseMultiRegionDatabase(
        this ModelBuilder modelBuilder,
        string primaryRegion,
        string[]? additionalRegions = null,
        string survive = "REGION FAILURE")
    {
        var payload = new MultiRegionAnnotation
        {
            Primary = primaryRegion,
            Regions = additionalRegions ?? [],
            Survive = survive
        };

        modelBuilder.HasAnnotation("Regional:MultiRegion", JsonConvert.SerializeObject(payload));
        return modelBuilder;
    }

    public static EntityTypeBuilder PlacementGlobal(this EntityTypeBuilder builder)
        => builder.AddLocalityAnnotation("GLOBAL");

    public static EntityTypeBuilder PlacementRegional(this EntityTypeBuilder builder, string? region = null)
        => builder.AddLocalityAnnotation(region != null
            ? $"REGIONAL BY TABLE IN \"{region}\""
            : "REGIONAL BY TABLE");

    public static EntityTypeBuilder PlacementRegionalByRow(this EntityTypeBuilder builder)
        => builder.AddLocalityAnnotation("REGIONAL BY ROW");

    private static EntityTypeBuilder AddLocalityAnnotation(this EntityTypeBuilder builder, string locality)
    {
        builder.HasAnnotation("Regional:Locality", locality);
        return builder;
    }

    public static EntityTypeBuilder WithTTL<T>(this EntityTypeBuilder<T> builder, Expression<Func<T, object>> propertyExpression, CronValue cronValue,
        int batchSize = 0, int rangeConcurrency = 0, int deleteRateLimit = 0) where T : class
        => AddExpirationJob(builder, new ExpirationJobAnnotation()
        {
            TimestampKey     = GetColumnName(builder, propertyExpression),
            CronValue        = cronValue,
            DeleteBatchSize  = batchSize,
            SelectBatchSize  = batchSize,
            DeleteRateLimit  = deleteRateLimit,
            RangeConcurrency = rangeConcurrency
        });

    public static PropertyBuilder<DateOnly> AsTTlField(this PropertyBuilder<DateOnly> builder)
        => builder.HasConversion(
                v => v.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                v => DateOnly.FromDateTime(v.ToUniversalTime())
            )
           .HasColumnType("timestamptz");

    private static string GetColumnName<T>(
        this EntityTypeBuilder<T> builder,
        Expression<Func<T, object>> propertyExpression)
        where T : class
    {
        if (propertyExpression == null)
            throw new ArgumentNullException(nameof(propertyExpression));

        var memberExpr = propertyExpression.Body switch
        {
            MemberExpression m => m,
            UnaryExpression { Operand: MemberExpression m } => m,
            _ => throw new ArgumentException("Expression must be a property access", nameof(propertyExpression))
        };

        var property = builder.Metadata.FindProperty(memberExpr.Member.Name)
                       ?? throw new InvalidOperationException($"Property '{memberExpr.Member.Name}' not found in entity '{builder.Metadata.Name}'.");

        var storeObject = StoreObjectIdentifier.Table(builder.Metadata.GetTableName()!, builder.Metadata.GetSchema());

        return property.GetColumnName(storeObject)!;
    }

    private static EntityTypeBuilder AddExpirationJob(this EntityTypeBuilder builder, ExpirationJobAnnotation jobDetails)
    {
        builder.HasAnnotation("Job:Expiration", JsonConvert.SerializeObject(jobDetails));
        return builder;
    }

    public static DbContextOptionsBuilder UseMultiregionalCompatibility(this DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, MultiregionalMigrationsSqlGenerator>();
        return optionsBuilder;
    }
}

public class MultiregionalMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, INpgsqlSingletonOptions npgsqlSingletonOptions)
    : Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
{
    private string DelimitIdentifier(string identifier)
        => this.Dependencies.SqlGenerationHelper.DelimitIdentifier(identifier);

    protected override void Generate(NpgsqlCreateDatabaseOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        var ann = (model ?? Dependencies.CurrentContext.Context.Model)
           .FindAnnotation("Regional:MultiRegion")?.Value as string;

        builder
           .Append("CREATE DATABASE ")
           .Append(DelimitIdentifier(operation.Name));

        if (!string.IsNullOrEmpty(operation.Template))
        {
            builder
               .AppendLine()
               .Append("TEMPLATE ")
               .Append(DelimitIdentifier(operation.Template));
        }

        if (!string.IsNullOrEmpty(operation.Tablespace))
        {
            builder
               .AppendLine()
               .Append("TABLESPACE ")
               .Append(DelimitIdentifier(operation.Tablespace));
        }

        if (!string.IsNullOrEmpty(operation.Collation))
        {
            builder
               .AppendLine()
               .Append("LC_COLLATE ")
               .Append(DelimitIdentifier(operation.Collation));
        }

        if (HasAnnotation<string>(model ?? Dependencies.CurrentContext.Context.Model, "Regional:MultiRegion", out var localityAnnotation))
        {
            var cfg     = JsonConvert.DeserializeObject<MultiRegionAnnotation>(localityAnnotation)!;
            var regions = string.Join(", ", cfg.Regions.Select(DelimitIdentifier));

            if (!string.IsNullOrEmpty(cfg.Primary))
            {
                builder.AppendLine()
                   .Append($"PRIMARY REGION {DelimitIdentifier(cfg.Primary)}");

                if (regions.Length > 0)
                    builder.Append($" REGIONS {regions}");
                //builder.Append($" SURVIVE {cfg.Survive}");
            }
        }

        

        builder.AppendLine(";");

        EndStatement(builder, suppressTransaction: true);
    }


    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        base.Generate(operation, model, builder, false);

        if (HasAnnotation<string>(model ?? Dependencies.CurrentContext.Context.Model, operation, "Job:Expiration", out var expirationJobAnnotation))
        {
            var cfg = JsonConvert.DeserializeObject<ExpirationJobAnnotation>(expirationJobAnnotation)!;
            builder.AppendLine()
               .Append($"WITH (ttl = 'on', ttl_expiration_expression = '{DelimitIdentifier(cfg.TimestampKey)}', ttl_job_cron = '{cfg.CronValue.value}'");
            if (cfg.SelectBatchSize != 0)
                builder.Append($", ttl_select_batch_size = {cfg.SelectBatchSize}");

            if (cfg.DeleteBatchSize != 0)
                builder.Append($", ttl_delete_batch_size = {cfg.DeleteBatchSize}");

            if (cfg.RangeConcurrency != 0)
                builder.Append($", ttl_range_concurrency = {cfg.RangeConcurrency}");

            if (cfg.DeleteRateLimit != 0)
                builder.Append($", ttl_delete_rate_limit = {cfg.DeleteRateLimit}");

            builder.Append(")");
        }

        if (HasAnnotation<string>(model, operation, "Regional:Locality", out var locality))
            builder.AppendLine().Append("LOCALITY ").Append(locality);

        builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
        EndStatement(builder);
    }

    private static bool HasAnnotation<T>(
        IModel? model,
        string key,
        [NotNullWhen(true)] out T? value)
    {
        value = default;

        var annotation = model?.FindAnnotation(key);
        if (annotation is null)
            return false;

        value = (T?)annotation.Value;
        return value is not null;
    }

    private static bool HasAnnotation<T>(
        IModel? model,
        CreateTableOperation operation,
        string key,
        [NotNullWhen(true)] out T? value)
    {
        value = default;

        var entityType = model?
           .GetEntityTypes()
           .FirstOrDefault(e =>
                string.Equals(e.GetTableName(), operation.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.GetSchema() ?? "", operation.Schema ?? "", StringComparison.OrdinalIgnoreCase));

        if (entityType is null)
            return false;

        var annotation = entityType.FindAnnotation(key);
        if (annotation is null)
            return false;

        value = (T?)annotation.Value;
        return value is not null;
    }
}