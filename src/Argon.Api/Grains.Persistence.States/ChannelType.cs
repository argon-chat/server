namespace Argon.Api.Grains.Persistence.States;

[GenerateSerializer]
[Serializable]
[Alias(nameof(ChannelType))]
public enum ChannelType
{
    Text,
    Voice,
    Announcement
}