namespace Argon.Features.Licensing;

public class LicenseOptions
{
    public string Issuer         { get; set; } = "ice.argon.zone";
    public string PublicKeyPem   { get; set; } = "";
    public string InstanceIdFile { get; set; } = "";
    public string LicenseFile    { get; set; } = "";
}