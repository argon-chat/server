namespace Argon.Features.Clustering;

[DebuggerDisplay("{Value}")]
public readonly record struct ArgonRoleId(string Value)
{
    public static readonly ArgonRoleId Core       = new("core");
    public static readonly ArgonRoleId Voice      = new("voice");
    public static readonly ArgonRoleId Media      = new("media");
    public static readonly ArgonRoleId Moderation = new("moderation");
    public static readonly ArgonRoleId Commerce   = new("commerce");
    public static readonly ArgonRoleId Jobs       = new("jobs");
    public static readonly ArgonRoleId EntryPoint = new("entrypoint");
    public static readonly ArgonRoleId BotApi     = new("botapi");
    public static readonly ArgonRoleId Admin      = new("admin");
    public static readonly ArgonRoleId Account    = new("account");

    /// <summary>Every other role in one process. For running the product on one machine.</summary>
    public static readonly ArgonRoleId Dev = new("dev");

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Value);

    public override string ToString()
        => Value;

    public bool Equals(ArgonRoleId other)
        => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
}
