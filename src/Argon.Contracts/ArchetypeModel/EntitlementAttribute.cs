namespace Argon.ArchetypeModel;

[AttributeUsage(AttributeTargets.Method)]
public class EntitlementAttribute(ArgonEntitlement entitlements) : Attribute
{
    public ArgonEntitlement Entitlements { get; } = entitlements;
}