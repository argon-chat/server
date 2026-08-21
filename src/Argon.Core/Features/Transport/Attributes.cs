namespace Argon.Services.Transport;

[AttributeUsage(AttributeTargets.Parameter)]
public class AsGrainIdAttribute : Attribute;
[AttributeUsage(AttributeTargets.Method)]
public class UseRandomGrainIdAttribute : Attribute;