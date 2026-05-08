namespace Argon.Features.Vault;

public record VaultPkiOptions
{
    public const string SectionName = "VaultPki";

    public string MountPoint  { get; set; } = "pki-admin";
    public string RoleName    { get; set; } = "operator";
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromDays(365);
    public TimeSpan MaxTtl     { get; set; } = TimeSpan.FromDays(730);
}
