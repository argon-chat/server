namespace Argon.Features.BotApi;

/// <summary>
/// Public Bot API DTO types — versioned, decoupled from internal Ion-generated types.
/// All types here are part of the public Bot API contract and must not expose internal details (file IDs, etc.).
/// </summary>

// ─── User ────────────────────────────────────────────────

[BotDtoVersion(1)]
public sealed record BotUserV1(
    Guid     UserId,
    string   Username,
    string   DisplayName,
    string?  AvatarUrl,
    UserFlag Flags);

// ─── Archetype ───────────────────────────────────────────

/// <summary>
/// Public archetype DTO — by default only exposes id/name/colour.
/// Entitlement (permissions bitmask) is only included when the bot
/// has the ViewArchetypePermissions privilege.
/// </summary>
[BotDtoVersion(1)]
public sealed record BotArchetypeV1(
    Guid    ArchetypeId,
    Guid    SpaceId,
    string  Name,
    int     Colour,
    bool    IsMentionable,
    bool    IsDefault,
    long?   Permissions = null);

// ─── Channel ─────────────────────────────────────────────

public enum BotChannelType
{
    Text         = 0,
    Voice        = 1,
    Announcement = 2,
}

[BotDtoVersion(1)]
public sealed record BotChannelBaseV1(
    Guid           ChannelId,
    Guid           SpaceId,
    BotChannelType Type,
    string         Name);

[BotDtoVersion(1)]
public sealed record BotChannelFullV1(
    Guid           ChannelId,
    Guid           SpaceId,
    BotChannelType Type,
    string         Name,
    string?        Description);

// ─── Message Entities ────────────────────────────────────

public enum BotEntityType
{
    Hashtag          = 0,
    Mention          = 1,
    MentionEveryone  = 2,
    MentionRole      = 3,
    Email            = 4,
    Url              = 5,
    Monospace        = 6,
    Quote            = 7,
    Spoiler          = 8,
    Strikethrough    = 9,
    Bold             = 10,
    Italic           = 11,
    Underline        = 12,
    Fraction         = 13,
    Ordinal          = 14,
    Capitalized      = 15,
    SystemCallStarted  = 16,
    SystemCallEnded    = 17,
    SystemCallTimeout  = 18,
    SystemUserJoined   = 19,
    Attachment         = 20,
}

/// <summary>
/// Flattened message entity — discriminated by <see cref="Type"/>.
/// Variant-specific fields are nullable; only the fields relevant to the entity type are populated.
/// </summary>
[BotDtoVersion(1)]
public sealed record BotMessageEntityV1
{
    public required BotEntityType Type   { get; init; }
    public required int           Offset { get; init; }
    public required int           Length { get; init; }

    // Mention
    public Guid?   UserId       { get; init; }

    // MentionRole
    public Guid?   ArchetypeId  { get; init; }

    // Email
    public string? Email        { get; init; }

    // Hashtag
    public string? Hashtag      { get; init; }

    // Quote
    public Guid?   QuotedUserId { get; init; }

    // Underline
    public int?    Colour       { get; init; }

    // Url
    public string? Domain       { get; init; }
    public string? Path         { get; init; }

    // Fraction
    public int?    Numerator    { get; init; }
    public int?    Denominator  { get; init; }

    // SystemCall*
    public Guid?   CallerId     { get; init; }
    public Guid?   CallId       { get; init; }
    public int?    DurationSeconds { get; init; }

    // SystemUserJoined
    public Guid?   InviterId    { get; init; }

    // Attachment
    public string? FileName     { get; init; }
    public long?   FileSize     { get; init; }
    public string? ContentType  { get; init; }
    public int?    Width        { get; init; }
    public int?    Height       { get; init; }
    public string? ThumbHash    { get; init; }
}

// ─── Message ─────────────────────────────────────────────

[BotDtoVersion(1)]
public sealed record BotMessageV1(
    long                      MessageId,
    long?                     ReplyId,
    Guid                      ChannelId,
    Guid                      SpaceId,
    string                    Text,
    List<BotMessageEntityV1>  Entities,
    DateTime                  TimeSent,
    BotUserV1?                Sender,
    List<ControlRowV1>?       Controls = null);

// ─── Presence ────────────────────────────────────────────

public enum BotUserStatus
{
    Offline      = 0,
    Online       = 1,
    Away         = 2,
    InGame       = 3,
    Listen       = 4,
    TouchGrass   = 5,
    DoNotDisturb = 6,
}

public enum BotActivityKind
{
    Game      = 0,
    Software  = 1,
    Streaming = 2,
    Listen    = 3,
}

[BotDtoVersion(1)]
public sealed record BotActivityV1(
    BotActivityKind Kind,
    uint            StartTimestampSeconds,
    string          TitleName);

[BotDtoVersion(1)]
public sealed record BotPresenceV1(
    BotUserStatus  Status,
    BotActivityV1? Activity);

// ─── Commands ────────────────────────────────────────────

public enum BotCommandOptionType
{
    String  = 0,
    Integer = 1,
    Boolean = 2,
    User    = 3,
    Channel = 4,
    Role    = 5,
    Number  = 6,
}

[BotDtoVersion(1)]
public sealed record BotCommandOptionValueV1(
    string               Name,
    BotCommandOptionType Type,
    object               Value);

// ─── OKLCH Color ─────────────────────────────────────────

/// <summary>
/// OKLCH color for interactive controls (buttons, etc.).
/// Uses perceptually uniform lightness so both dark and light themes can
/// shift L ±0.10 and get a readable result without hue/chroma drift.
/// <para>Validation ranges:</para>
/// <list type="bullet">
///   <item><b>L</b> (lightness): [0.40, 0.80] — visible on both dark/light backgrounds</item>
///   <item><b>C</b> (chroma):    [0.00, 0.37] — full sRGB gamut, including achromatic</item>
///   <item><b>H</b> (hue):       [0.0, 360.0) — any hue angle</item>
/// </list>
/// </summary>
[BotDtoVersion(1)]
public sealed record OklchColor(float L, float C, float H)
{
    public const float MinL = 0.40f;
    public const float MaxL = 0.80f;
    public const float MinC = 0.00f;
    public const float MaxC = 0.37f;
    public const float MinH = 0.0f;
    public const float MaxH = 360.0f;

    public void Validate()
    {
        if (L < MinL || L > MaxL)
            throw new ArgumentOutOfRangeException(nameof(L), L, $"Lightness must be in [{MinL}, {MaxL}]");
        if (C < MinC || C > MaxC)
            throw new ArgumentOutOfRangeException(nameof(C), C, $"Chroma must be in [{MinC}, {MaxC}]");
        if (H < MinH || H >= MaxH)
            throw new ArgumentOutOfRangeException(nameof(H), H, $"Hue must be in [{MinH}, {MaxH})");
    }
}

// ─── Controls ────────────────────────────────────────────

public enum ControlType
{
    Button          = 0,
    StringSelect    = 1,
    UserSelect      = 2,
    ArchetypeSelect = 3,
    ChannelSelect   = 4,
}

public enum ButtonVariant
{
    /// <summary>Interactive button — sends a ControlInteraction event with the control's Id.</summary>
    Callback = 0,
    /// <summary>Navigation button — opens a URL. Does not trigger an interaction event.</summary>
    Link     = 1,
}

/// <summary>
/// An option in a <see cref="ControlType.StringSelect"/> control.
/// </summary>
[BotDtoVersion(1)]
public sealed record SelectOptionV1
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Description   { get; init; }
    public bool?   Default       { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > 100)
            throw new ArgumentException("Select option label is required and must be ≤100 characters.");
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > 100)
            throw new ArgumentException("Select option value is required and must be ≤100 characters.");
        if (Description is { Length: > 100 })
            throw new ArgumentException("Select option description must be ≤100 characters.");
    }
}

/// <summary>
/// Flattened interactive control — discriminated by <see cref="Type"/>.
/// <para><b>Button</b>: Variant, Label, Id (Callback), Url (Link), Colour, Disabled.</para>
/// <para><b>Selects</b>: CustomId, Placeholder, MinValues, MaxValues, Disabled.
/// StringSelect also requires Options.</para>
/// </summary>
[BotDtoVersion(1)]
public sealed record BotControlV1
{
    public required ControlType Type { get; init; }

    // ── Button fields ──

    /// <summary>Button variant (Callback or Link).</summary>
    public ButtonVariant? Variant { get; init; }

    /// <summary>Text displayed on the button. Max 80 characters.</summary>
    public string? Label { get; init; }

    /// <summary>Localized labels keyed by BCP-47 language tag (e.g. "ru", "en-US").</summary>
    public Dictionary<string, string>? LabelLocalizations { get; init; }

    /// <summary>Developer-defined identifier. Required for Callback buttons. Max 100 characters.</summary>
    public string? Id { get; init; }

    /// <summary>URL for Link buttons. Max 512 characters.</summary>
    public string? Url { get; init; }

    /// <summary>OKLCH accent color for the button.</summary>
    public OklchColor? Colour { get; init; }

    /// <summary>Whether the control is non-interactive. Defaults to false.</summary>
    public bool? Disabled { get; init; }

    // ── Select fields ──

    /// <summary>Developer-defined identifier for select menus. 1–100 characters. Required for all selects.</summary>
    public string? CustomId { get; init; }

    /// <summary>Placeholder text shown when nothing is selected. Max 150 characters.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Minimum number of items that must be chosen. Default 1, min 0, max 25.</summary>
    public int? MinValues { get; init; }

    /// <summary>Maximum number of items that can be chosen. Default 1, max 25.</summary>
    public int? MaxValues { get; init; }

    /// <summary>Options for StringSelect. Max 25 options.</summary>
    public List<SelectOptionV1>? Options { get; init; }

    // ── Shared fields ──

    /// <summary>
    /// When set, only members with this archetype can interact with the control.
    /// Server administrators (ManageServer) bypass this constraint.
    /// The client uses this for visibility filtering; the backend enforces it on interaction.
    /// </summary>
    public Guid? RequiredArchetypeId { get; init; }

    public void Validate()
    {
        switch (Type)
        {
            case ControlType.Button:
                ValidateButton();
                break;
            case ControlType.StringSelect:
                ValidateSelect();
                if (Options is null or { Count: 0 })
                    throw new ArgumentException("StringSelect must have at least one option.");
                if (Options.Count > 25)
                    throw new ArgumentException("StringSelect can have at most 25 options.");
                var values = new HashSet<string>();
                foreach (var opt in Options)
                {
                    opt.Validate();
                    if (!values.Add(opt.Value))
                        throw new ArgumentException($"Duplicate option value '{opt.Value}' in StringSelect.");
                }
                break;
            case ControlType.UserSelect:
            case ControlType.ArchetypeSelect:
            case ControlType.ChannelSelect:
                ValidateSelect();
                if (Options is not null)
                    throw new ArgumentException($"{Type} must not have Options (they are auto-populated).");
                break;
            default:
                throw new ArgumentException($"Unknown control type: {Type}");
        }
    }

    private void ValidateButton()
    {
        if (Variant is null)
            throw new ArgumentException("Button must have a Variant.");
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > 80)
            throw new ArgumentException("Button label is required and must be ≤80 characters.");

        // Select fields must not be set on buttons
        if (CustomId is not null || Placeholder is not null || MinValues is not null || MaxValues is not null || Options is not null)
            throw new ArgumentException("Button must not have select fields (CustomId, Placeholder, MinValues, MaxValues, Options).");

        switch (Variant)
        {
            case ButtonVariant.Callback:
                if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100)
                    throw new ArgumentException("Callback button must have an Id (1–100 characters).");
                if (Url is not null)
                    throw new ArgumentException("Callback button must not have a Url.");
                break;

            case ButtonVariant.Link:
                if (string.IsNullOrWhiteSpace(Url) || Url.Length > 512)
                    throw new ArgumentException("Link button must have a Url (1–512 characters).");
                if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    throw new ArgumentException("Link button Url must be a valid HTTP(S) URL.");
                if (Id is not null)
                    throw new ArgumentException("Link button must not have an Id.");
                break;
        }

        Colour?.Validate();
    }

    private void ValidateSelect()
    {
        if (string.IsNullOrWhiteSpace(CustomId) || CustomId.Length > 100)
            throw new ArgumentException($"{Type} must have a CustomId (1–100 characters).");
        if (Placeholder is { Length: > 150 })
            throw new ArgumentException("Placeholder must be ≤150 characters.");
        if (MinValues is < 0 or > 25)
            throw new ArgumentException("MinValues must be 0–25.");
        if (MaxValues is < 1 or > 25)
            throw new ArgumentException("MaxValues must be 1–25.");
        if (MinValues is not null && MaxValues is not null && MinValues > MaxValues)
            throw new ArgumentException("MinValues must be ≤ MaxValues.");

        // Button fields must not be set on selects
        if (Variant is not null || Label is not null || Id is not null || Url is not null || Colour is not null)
            throw new ArgumentException($"{Type} must not have button fields (Variant, Label, Id, Url, Colour).");
    }

    /// <summary>Returns the developer-defined identifier: Id for buttons, CustomId for selects.</summary>
    public string? GetInteractionId() => Type == ControlType.Button ? Id : CustomId;
}

/// <summary>
/// A row of interactive controls. Max 5 controls per row;
/// a message can have up to 5 rows (25 controls total).
/// A row can contain either up to 5 buttons OR exactly 1 select (not mixed).
/// </summary>
[BotDtoVersion(1)]
public sealed record ControlRowV1(List<BotControlV1> Controls)
{
    public const int MaxControlsPerRow = 5;
    public const int MaxRows           = 5;

    public void Validate()
    {
        if (Controls is null || Controls.Count == 0)
            throw new ArgumentException("Control row must have at least one control.");
        if (Controls.Count > MaxControlsPerRow)
            throw new ArgumentException($"Control row can have at most {MaxControlsPerRow} controls.");

        var hasButton = false;
        var hasSelect = false;
        foreach (var c in Controls)
        {
            if (c.Type == ControlType.Button) hasButton = true;
            else hasSelect = true;
        }

        if (hasButton && hasSelect)
            throw new ArgumentException("A row must contain either buttons or a single select, not both.");
        if (hasSelect && Controls.Count > 1)
            throw new ArgumentException("A row with a select must contain exactly one control.");

        var ids = new HashSet<string>();
        foreach (var c in Controls)
        {
            c.Validate();
            var interactionId = c.GetInteractionId();
            if (interactionId is not null && !ids.Add(interactionId))
                throw new ArgumentException($"Duplicate control identifier '{interactionId}' in row.");
        }
    }

    public static void ValidateRows(List<ControlRowV1>? rows)
    {
        if (rows is null or { Count: 0 }) return;
        if (rows.Count > MaxRows)
            throw new ArgumentException($"Message can have at most {MaxRows} control rows.");

        var allIds = new HashSet<string>();
        foreach (var row in rows)
        {
            row.Validate();
            foreach (var c in row.Controls)
            {
                var interactionId = c.GetInteractionId();
                if (interactionId is not null && !allIds.Add(interactionId))
                    throw new ArgumentException($"Duplicate control identifier '{interactionId}' across rows.");
            }
        }
    }
}

// ─── Modals ──────────────────────────────────────────────

public enum TextInputStyle
{
    Short     = 0,
    Paragraph = 1,
}

public enum ModalComponentType
{
    TextInput       = 0,
    StringSelect    = 1,
    UserSelect      = 2,
    ArchetypeSelect = 3,
    ChannelSelect   = 4,
    Checkbox        = 5,
}

/// <summary>
/// A single component inside a modal popup — discriminated by <see cref="Type"/>.
/// </summary>
[BotDtoVersion(1)]
public sealed record ModalComponentV1
{
    public required ModalComponentType Type { get; init; }

    /// <summary>Developer-defined identifier. 1–100 characters. Required for all components.</summary>
    public required string CustomId { get; init; }

    /// <summary>Label displayed above the component. Max 45 characters.</summary>
    public required string Label { get; init; }

    // ── TextInput ──
    public TextInputStyle? Style       { get; init; }
    public string?         Placeholder { get; init; }
    public int?            MinLength   { get; init; }
    public int?            MaxLength   { get; init; }
    public bool?           Required    { get; init; }
    public string?         Value       { get; init; }

    // ── StringSelect ──
    public List<SelectOptionV1>? Options   { get; init; }
    public int?                  MinValues { get; init; }
    public int?                  MaxValues { get; init; }

    // ── Checkbox ──
    public bool? Default { get; init; }

    // ── Description (for selects and checkboxes) ──
    public string? Description { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CustomId) || CustomId.Length > 100)
            throw new ArgumentException("Modal component must have a CustomId (1–100 characters).");
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > 45)
            throw new ArgumentException("Modal component label is required and must be ≤45 characters.");

        switch (Type)
        {
            case ModalComponentType.TextInput:
                if (Style is null)
                    throw new ArgumentException("TextInput must have a Style.");
                if (MinLength is < 0 or > 4000)
                    throw new ArgumentException("TextInput MinLength must be 0–4000.");
                if (MaxLength is < 1 or > 4000)
                    throw new ArgumentException("TextInput MaxLength must be 1–4000.");
                if (Placeholder is { Length: > 100 })
                    throw new ArgumentException("TextInput placeholder must be ≤100 characters.");
                if (Value is { Length: > 4000 })
                    throw new ArgumentException("TextInput value must be ≤4000 characters.");
                break;

            case ModalComponentType.StringSelect:
                if (Options is null or { Count: 0 })
                    throw new ArgumentException("StringSelect must have at least one option.");
                if (Options.Count > 25)
                    throw new ArgumentException("StringSelect can have at most 25 options.");
                foreach (var opt in Options) opt.Validate();
                if (MinValues is < 0 or > 25) throw new ArgumentException("MinValues must be 0–25.");
                if (MaxValues is < 1 or > 25) throw new ArgumentException("MaxValues must be 1–25.");
                break;

            case ModalComponentType.UserSelect:
            case ModalComponentType.ArchetypeSelect:
            case ModalComponentType.ChannelSelect:
                if (MinValues is < 0 or > 25) throw new ArgumentException("MinValues must be 0–25.");
                if (MaxValues is < 1 or > 25) throw new ArgumentException("MaxValues must be 1–25.");
                break;

            case ModalComponentType.Checkbox:
                break;

            default:
                throw new ArgumentException($"Unknown modal component type: {Type}");
        }
    }
}

/// <summary>
/// Modal popup definition — sent by a bot in response to an interaction.
/// Max 5 components, title max 45 characters.
/// </summary>
[BotDtoVersion(1)]
public sealed record ModalDefinitionV1
{
    public required string                CustomId   { get; init; }
    public required string                Title      { get; init; }
    public required List<ModalComponentV1> Components { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CustomId) || CustomId.Length > 100)
            throw new ArgumentException("Modal CustomId is required and must be ≤100 characters.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 45)
            throw new ArgumentException("Modal title is required and must be ≤45 characters.");
        if (Components is null or { Count: 0 })
            throw new ArgumentException("Modal must have at least one component.");
        if (Components.Count > 5)
            throw new ArgumentException("Modal can have at most 5 components.");

        var ids = new HashSet<string>();
        foreach (var c in Components)
        {
            c.Validate();
            if (!ids.Add(c.CustomId))
                throw new ArgumentException($"Duplicate component CustomId '{c.CustomId}' in modal.");
        }
    }
}

/// <summary>
/// A single value submitted from a modal component.
/// Text → single value, Select → selected values, Checkbox → ["true"/"false"].
/// </summary>
[BotDtoVersion(1)]
public sealed record ModalSubmitValueV1(string CustomId, List<string> Values);
