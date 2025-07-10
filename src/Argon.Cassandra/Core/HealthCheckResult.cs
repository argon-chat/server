namespace Argon.Cassandra.Core;

public class HealthCheckResult
{
    public bool       IsHealthy        { get; set; }
    public TimeSpan   ResponseTime     { get; set; }
    public string?    CassandraVersion { get; set; }
    public string     Message          { get; set; } = string.Empty;
    public Exception? Exception        { get; set; }

    public override string ToString()
        => $"Healthy: {IsHealthy}, ResponseTime: {ResponseTime.TotalMilliseconds:F0}ms, Message: {Message}";
}