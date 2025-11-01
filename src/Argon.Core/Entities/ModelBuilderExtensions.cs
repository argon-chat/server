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
}

public sealed class UlongToLongConverter() : ValueConverter<ulong, long>(v => unchecked((long)v),
    v => unchecked((ulong)v))
{
    public static readonly UlongToLongConverter Instance = new();
}