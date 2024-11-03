namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public enum ChannelType : ushort
{
    Text,
    Voice,
    Announcement
}

public class Channel : ApplicationRecord
{
    [MaxLength(255), Id(3)] public string Name { get; set; } = string.Empty;
    [MaxLength(255), Id(4)] public string Description { get; set; } = string.Empty;
    [Id(5)] public Guid UserId { get; set; } = Guid.Empty;
    [Id(6)] public ChannelType ChannelType { get; set; } = ChannelType.Text;
    [Id(7)] public ServerRole AccessLevel { get; set; } = ServerRole.User;
    [Id(8)] public Guid ChannelId { get; set; } = Guid.Empty;
}