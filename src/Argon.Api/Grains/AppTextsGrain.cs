namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Grains.Interfaces;
using ion.runtime;
using Orleans.Concurrency;

[StatelessWorker]
public sealed class AppTextsGrain(IDbContextFactory<ApplicationDbContext> context) : Grain, IAppTextsGrain
{
    public async Task<AppTexts?> GetAsync(IAppRef app)
    {
        await using var db = await context.CreateDbContextAsync();

        IQueryable<DevAppEntity> apps = app switch
        {
            AppById byId       => db.AppEntities.Where(a => a.AppId == byId.appId),
            AppByBotUser byBot => db.BotEntities.Where(b => b.BotAsUserId == byBot.userId),
            _                  => throw new ArgumentOutOfRangeException(nameof(app))
        };

        // One statement, so the version and the texts are read from the same snapshot.
        var found = await apps
           .AsNoTracking()
           .Select(a => new
            {
                a.TextsVersion,
                Texts = a.Texts.Select(t => new { t.Key, t.Locale, t.Value }).ToList()
            })
           .FirstOrDefaultAsync();

        return found is null
            ? null
            : new AppTexts(found.TextsVersion,
                new IonArray<LocalizedText>(found.Texts.Select(t => new LocalizedText(t.Key, t.Locale, t.Value))));
    }
}
