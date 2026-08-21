namespace Argon.Features.Licensing;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiresFeatureAttribute(string feature) : Attribute
{
    public string Feature { get; } = feature;
}