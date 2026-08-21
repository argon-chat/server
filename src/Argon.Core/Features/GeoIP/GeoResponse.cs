namespace Argon.Features.GeoIP;

public class GeoResponse
{
    public required string IP { get; set; }

    public string? Country   { get; set; }
    public string? Region    { get; set; }
    public string? City      { get; set; }
    public double? Latitude  { get; set; }
    public double? Longitude { get; set; }
    public uint?   ASN       { get; set; }
    public string? ISP       { get; set; }
    public string? Error     { get; set; }
}