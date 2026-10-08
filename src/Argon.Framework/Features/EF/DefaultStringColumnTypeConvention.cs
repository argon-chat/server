namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

public class DefaultStringColumnTypeConvention : IModelFinalizingConvention
{
    private const string DefaultNonIndexedType = "text";

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.ClrType != typeof(string))
                    continue;

                if (property.GetColumnType() is not null)
                    continue;

                var isIndexed = entityType.GetDeclaredIndexes()
                   .Any(idx => idx.Properties.Contains(property));

                if (!isIndexed)
                {
                    property.Builder.HasColumnType(DefaultNonIndexedType);
                    continue;
                }

                var maxLength = property.GetMaxLength();

                if (maxLength is null or <= 0)
                    throw new InvalidOperationException(
                        $"String property [{entityType.DisplayName()}.{property.Name}] participates in the index, but there is no length set for it. " +
                        $"Specify [MaxLength(N)] or .HasMaxLength(N), so that indexed strings are stored as varchar(N).");

                property.Builder.HasColumnType($"varchar({maxLength})");
            }
        }
    }
}