namespace Argon.Entities;

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public static class ModelBuilderExtensions
{
    public static void UseSoftDeleteCompatibility(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
                continue;
            modelBuilder.Entity(entityType.ClrType)
               .HasQueryFilter(GetSoftDeleteFilter(entityType.ClrType));
        }

        return;

        static LambdaExpression GetSoftDeleteFilter(Type type)
        {
            var parameter         = Expression.Parameter(type, "e");
            var isDeletedProperty = Expression.Property(parameter, nameof(ArgonEntity.IsDeleted));
            var notDeleted        = Expression.Not(isDeletedProperty);
            return Expression.Lambda(notDeleted, parameter);
        }
    }

    public static void UseUnsignedLongCompatibility(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(ulong))
                {
                    property.SetValueConverter(UlongToLongConverter.Instance);
                    property.SetColumnType("bigint");
                }
                else if (property.ClrType == typeof(ulong?))
                {
                    property.SetValueConverter(new ValueConverter<ulong?, long?>(
                        v => v.HasValue ? unchecked((long)v.Value) : null,
                        v => v.HasValue ? unchecked((ulong)v.Value) : null));
                    property.SetColumnType("bigint");
                }
            }
        }
    }

    /// <summary>
    /// Stores unsigned enums as <c>integer</c>, the column type they have always had.
    /// </summary>
    /// <remarks>
    /// <para>Ion enums became unsigned (<c>u2</c>, <c>u4</c>) when the contracts were regenerated, and
    /// Npgsql maps a <c>uint</c> to <c>bigint</c>. The columns were created as <c>integer</c> back when
    /// the same enums were signed, and no member comes anywhere near the width of one, so the model
    /// had quietly drifted from the schema: the next migration anyone scaffolded would have carried
    /// an <c>ALTER COLUMN … TYPE bigint</c> for <c>Users.LockdownReason</c> and seven others — a
    /// rewrite of the widest tables in the product, for nothing.</para>
    ///
    /// <para>So the model says what the database has. A property that already carries a converter is
    /// left alone; those were configured on purpose.</para>
    /// </remarks>
    public static void UseUnsignedEnumCompatibility(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (!clrType.IsEnum || property.GetValueConverter() is not null)
                    continue;

                var underlying = Enum.GetUnderlyingType(clrType);

                if (underlying != typeof(uint) && underlying != typeof(ushort) && underlying != typeof(byte))
                    continue;

                var converter = (ValueConverter)Activator.CreateInstance(
                    typeof(EnumToNumberConverter<,>).MakeGenericType(clrType, typeof(int)))!;

                property.SetValueConverter(converter);
                property.SetColumnType("integer");
            }
        }
    }
}

public sealed class UlongToLongConverter() : ValueConverter<ulong, long>(v => unchecked((long)v),
    v => unchecked((ulong)v))
{
    public static readonly UlongToLongConverter Instance = new();
}
