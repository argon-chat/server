namespace Argon.Cassandra.Configuration;

/// <summary>
/// Configuration for connecting to a Cassandra cluster.
/// </summary>
public class CassandraConfiguration
{
    /// <summary>
    /// Gets or sets the contact points (hostnames or IP addresses) for the Cassandra cluster.
    /// </summary>
    public IEnumerable<string> ContactPoints { get; set; } = new List<string> { "127.0.0.1" };

    /// <summary>
    /// Gets or sets the port number for the Cassandra cluster.
    /// </summary>
    public int Port { get; set; } = 9042;

    /// <summary>
    /// Gets or sets the keyspace name to use.
    /// </summary>
    public string? Keyspace { get; set; }

    /// <summary>
    /// Gets or sets the username for Cassandra authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password for Cassandra authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the consistency level for Cassandra operations.
    /// </summary>
    public ConsistencyLevel ConsistencyLevel { get; set; } = ConsistencyLevel.LocalQuorum;

    /// <summary>
    /// Gets or sets the replication factor for the keyspace.
    /// </summary>
    public int ReplicationFactor { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether to use data centers for replication strategy.
    /// </summary>
    public bool UseNetworkTopologyStrategy { get; set; } = false;

    /// <summary>
    /// Gets or sets the data center replication factors when using network topology strategy.
    /// </summary>
    public Dictionary<string, int> DataCenterReplicationFactors { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to automatically create the keyspace if it doesn't exist.
    /// </summary>
    public bool AutoCreateKeyspace { get; set; } = false;

    /// <summary>
    /// Gets or sets the connection timeout in milliseconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the query timeout in milliseconds.
    /// </summary>
    public int QueryTimeout { get; set; } = 12000;

    /// <summary>
    /// Gets or sets whether to enable query tracing.
    /// </summary>
    public bool EnableTracing { get; set; } = false;

    /// <summary>
    /// Gets or sets the retry policy to use.
    /// </summary>
    public IRetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the load balancing policy to use.
    /// </summary>
    public ILoadBalancingPolicy? LoadBalancingPolicy { get; set; }
}
