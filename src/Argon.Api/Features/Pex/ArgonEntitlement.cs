//namespace Argon.ArchetypeModel;

//[Flags]
//public enum ArgonEntitlement : ulong
//{
//    None = 0UL,

//    // base entitlement
//    ViewChannel = 1UL << 0,
//    ReadHistory = 1UL << 1,
//    JoinToVoice = 1UL << 2,

//    Base = ViewChannel | ReadHistory,

//    // chats entitlement
//    SendMessages      = 1UL << 5,
//    SendVoice         = 1UL << 6,
//    AttachFiles       = 1UL << 7,
//    AddReactions      = 1UL << 8,
//    AnyMentions       = 1UL << 9,
//    MentionEveryone   = 1UL << 10,
//    ExternalEmoji     = 1UL << 11,
//    ExternalStickers  = 1UL << 12,
//    UseCommands       = 1UL << 13,
//    PostEmbeddedLinks = 1UL << 14,


//    BaseChat = SendMessages | SendVoice | AttachFiles |
//               AddReactions | AnyMentions | ExternalEmoji |
//               ExternalStickers | UseCommands | PostEmbeddedLinks,

//    // media entitlement
//    Connect = 1UL << 20,
//    Speak   = 1UL << 21,
//    Video   = 1UL << 22,
//    Stream  = 1UL << 23,

//    BaseMedia = Connect | Speak | Video | Stream,


//    BaseMember = Base | BaseChat | BaseMedia,


//    // extended user entitlement

//    UseASIO           = 1UL << 30,
//    AdditionalStreams = 1UL << 31,

//    BaseExtended = UseASIO | AdditionalStreams,

//    // moderation entitlement
//    DisconnectMember = 1UL << 40,
//    MoveMember       = 1UL << 41,
//    BanMember        = 1UL << 42,
//    MuteMember       = 1UL << 43,
//    KickMember       = 1UL << 44,

//    ModerateMembers = DisconnectMember | MoveMember | BanMember | MuteMember | KickMember,


//    // admin entitlement
//    ManageChannels  = 1UL << 50,
//    ManageArchetype = 1UL << 51,
//    ManageBots      = 1UL << 52,
//    ManageEvents    = 1UL << 53,
//    ManageBehaviour = 1UL << 54,
//    ManageServer    = 1UL << 55,


//    ControlServer =
//        ManageChannels | ManageArchetype |
//        ManageBots | ManageEvents |
//        ManageBehaviour | ManageServer,


//    Administrator = ulong.MaxValue
//}

//public readonly record struct EntitlementScopeKey(string key)
//{
//    public static implicit operator string(EntitlementScopeKey k) => k.key;
//    public static implicit operator EntitlementScopeKey(string k) => new(k);


//    public static readonly EntitlementScopeKey All   = new("*");
//    public static readonly EntitlementScopeKey Base  = new("base");
//    public static readonly EntitlementScopeKey Chat  = new("chat");
//    public static readonly EntitlementScopeKey Media = new("media");
//    public static readonly EntitlementScopeKey Moderate = new("moderate");
//    public static readonly EntitlementScopeKey Server = new("server");
//}

//public readonly record struct EntitlementKey(string key)
//{
//    public static implicit operator string(EntitlementKey k) => k.key;
//    public static implicit operator EntitlementKey(string k) => new(k);
//}

//public readonly record struct ArgonEntitlementScope(EntitlementScopeKey scope, EntitlementKey key)
//{
//    public static readonly ArgonEntitlementScope OwnerScope    = new(EntitlementScopeKey.All, "*");
//    public static readonly ArgonEntitlementScope BaseScope     = new(EntitlementScopeKey.Base, "*");
//    public static readonly ArgonEntitlementScope ChatScope     = new(EntitlementScopeKey.Chat, "*");
//    public static readonly ArgonEntitlementScope MediaScope    = new(EntitlementScopeKey.Media, "*");
//    public static readonly ArgonEntitlementScope ModerateScope = new(EntitlementScopeKey.Moderate, "*");
//    public static readonly ArgonEntitlementScope ServerScope   = new(EntitlementScopeKey.Server, "*");


//    public static readonly ArgonEntitlementScope BaseScope_ViewChannel = new(EntitlementScopeKey.Base, "channel:view");
//    public static readonly ArgonEntitlementScope BaseScope_JoinToVoice = new(EntitlementScopeKey.Base, "voice:join");


//}

using static ArgonContracts.ArgonEntitlement;

public static class ArgonEntitlementKit
{
    public static ArgonEntitlement ControlServer =>
        ManageChannels | ManageArchetype |
        ManageBots | ManageEvents |
        ManageBehaviour | ManageServer;
    public static ArgonEntitlement Administrator => (ArgonEntitlement)ulong.MaxValue;

    public static ArgonEntitlement BaseMember => Base | BaseChat | BaseMedia;
    public static ArgonEntitlement BaseMedia  => Connect | Speak | Video | ArgonEntitlement.Stream;

    public static ArgonEntitlement BaseChat => SendMessages | SendVoice | AttachFiles |
                                               AddReactions | AnyMentions | ExternalEmoji |
                                               ExternalStickers | UseCommands | PostEmbeddedLinks;

    public static ArgonEntitlement Base => ViewChannel | ReadHistory;
}