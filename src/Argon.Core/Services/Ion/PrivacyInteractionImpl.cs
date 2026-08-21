namespace Argon.Services.Ion;

using Argon.Core.Entities.Data;
using Argon.Core.Features.CoreLogic.Privacy;
using Grains.Interfaces;
using ion.runtime;

/// <summary>
/// Ion service for the flexible "about-me" privacy rules. Generic over a string key
/// (e.g. "stream.draw"); the client only surfaces the keys it knows. Reads/writes the
/// caller's own rules via their <see cref="IPrivacyPolicyGrain"/>.
/// </summary>
public class PrivacyInteractionImpl : IPrivacyInteraction
{
    public async Task<PrivacyRuleView> GetPrivacyRule(string key, Guid? spaceId, CancellationToken ct = default)
    {
        var grain = this.GetGrain<IPrivacyPolicyGrain>(this.GetUserId());
        var rule  = await grain.GetRuleAsync(key, spaceId);

        if (rule is null)
            return new PrivacyRuleView(
                key,
                ToIon(PrivacyKeys.DefaultModeFor(key)),
                spaceId,
                new IonArray<Guid>(new List<Guid>()),
                new IonArray<Guid>(new List<Guid>()));

        return new PrivacyRuleView(
            rule.Key,
            ToIon(rule.Mode),
            rule.ScopeSpaceId,
            new IonArray<Guid>(rule.AllowExceptions),
            new IonArray<Guid>(rule.DenyExceptions));
    }

    public async Task<bool> SetPrivacyRule(string key, PrivacyRuleMode mode, Guid? spaceId,
        IonArray<Guid> allow, IonArray<Guid> deny, CancellationToken ct = default)
    {
        var grain = this.GetGrain<IPrivacyPolicyGrain>(this.GetUserId());
        await grain.SetRuleAsync(new PrivacyRuleInput(
            key, ToEntity(mode), spaceId, allow.Values.ToList(), deny.Values.ToList()));
        return true;
    }

    private static PrivacyRuleMode ToIon(PrivacyMode m) => m switch
    {
        PrivacyMode.Everybody => PrivacyRuleMode.EVERYBODY,
        PrivacyMode.Contacts  => PrivacyRuleMode.CONTACTS,
        PrivacyMode.Nobody    => PrivacyRuleMode.NOBODY,
        _                     => PrivacyRuleMode.EVERYBODY,
    };

    private static PrivacyMode ToEntity(PrivacyRuleMode m) => m switch
    {
        PrivacyRuleMode.EVERYBODY => PrivacyMode.Everybody,
        PrivacyRuleMode.CONTACTS  => PrivacyMode.Contacts,
        PrivacyRuleMode.NOBODY    => PrivacyMode.Nobody,
        _                         => PrivacyMode.Everybody,
    };
}
