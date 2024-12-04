namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Diagnostics;

public class TimeStampAndSoftDeleteInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        HandleSoftDelete(eventData.Context);
        SetTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        HandleSoftDelete(eventData.Context);
        SetTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void SetTimestamps(DbContext? context)
    {
        if (context == null) return;

        var entries = context.ChangeTracker
           .Entries()
           .Where(e => e is { Entity: ArgonEntity, State: EntityState.Added or EntityState.Modified });

        foreach (var entry in entries)
        {
            var entity = (ArgonEntity)entry.Entity;

            if (entry.State == EntityState.Added)
                entity.CreatedAt = DateTime.UtcNow;

            entity.UpdatedAt = DateTime.UtcNow;
        }
    }

    private void HandleSoftDelete(DbContext? context)
    {
        if (context == null) return;

        var entries = context.ChangeTracker
           .Entries()
           .Where(e => e is { Entity: ArgonEntity, State: EntityState.Deleted });

        foreach (var entry in entries)
        {
            var entity = (ArgonEntity)entry.Entity;

            entity.DeletedAt = DateTime.UtcNow;
            entity.IsDeleted = true;

            entry.State = EntityState.Modified;
        }
    }
}