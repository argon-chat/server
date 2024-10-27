namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(ServerRole))]
public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}