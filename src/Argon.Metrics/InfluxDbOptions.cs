namespace Argon.Metrics;

public class InfluxDbOptions
{
    public required bool    IsEnabled { get; set; } = false;
    public required string  Url       { get; set; }
    public required string  Token     { get; set; }
    public          string? Database  { get; set; }
}