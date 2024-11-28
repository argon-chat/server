namespace Argon.Contracts.Models;

using MessagePack;
using Reinforced.Typings.Attributes;

[MessagePackObject(true), TsInterface]
public sealed record ShortUser(Guid userId, string Username, string DisplayName, string? AvatarFileId)
{
    public static implicit operator ShortUser(User user) => new(user.Id, user.Username, user.DisplayName, user.AvatarFileId);
}