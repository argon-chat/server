namespace Argon.Cassandra.MaterializedViews;

/// <summary>
/// Builder for creating materialized views
/// </summary>
public class MaterializedViewBuilder(string viewName, string baseTable)
{
    private readonly string       _viewName  = viewName ?? throw new ArgumentNullException(nameof(viewName));
    private readonly string       _baseTable = baseTable ?? throw new ArgumentNullException(nameof(baseTable));
    private          string?      _keyspace;
    private          string?      _whereClause;
    private readonly List<string> _selectedColumns = new();
    private readonly List<string> _partitionKeys   = new();
    private readonly List<string> _clusteringKeys  = new();

    /// <summary>
    /// Sets the keyspace for the view
    /// </summary>
    public MaterializedViewBuilder InKeyspace(string keyspace)
    {
        _keyspace = keyspace;
        return this;
    }

    /// <summary>
    /// Adds columns to select
    /// </summary>
    public MaterializedViewBuilder SelectColumns(params string[] columns)
    {
        _selectedColumns.AddRange(columns);
        return this;
    }

    /// <summary>
    /// Sets the WHERE clause
    /// </summary>
    public MaterializedViewBuilder Where(string whereClause)
    {
        _whereClause = whereClause;
        return this;
    }

    /// <summary>
    /// Sets partition keys
    /// </summary>
    public MaterializedViewBuilder WithPartitionKeys(params string[] keys)
    {
        _partitionKeys.AddRange(keys);
        return this;
    }

    /// <summary>
    /// Sets clustering keys
    /// </summary>
    public MaterializedViewBuilder WithClusteringKeys(params string[] keys)
    {
        _clusteringKeys.AddRange(keys);
        return this;
    }

    /// <summary>
    /// Builds the CREATE MATERIALIZED VIEW CQL statement
    /// </summary>
    public string BuildCreateCql()
    {
        var cql = new System.Text.StringBuilder();
        
        cql.Append("CREATE MATERIALIZED VIEW ");
        if (!string.IsNullOrEmpty(_keyspace))
        {
            cql.Append($"{_keyspace}.{_viewName}");
        }
        else
        {
            cql.Append(_viewName);
        }

        cql.Append(" AS SELECT ");
        if (_selectedColumns.Any())
        {
            cql.Append(string.Join(", ", _selectedColumns));
        }
        else
        {
            cql.Append("*");
        }

        cql.Append($" FROM {_baseTable}");

        if (!string.IsNullOrEmpty(_whereClause))
        {
            cql.Append($" WHERE {_whereClause}");
        }

        // Add primary key definition
        if (_partitionKeys.Any() || _clusteringKeys.Any())
        {
            cql.Append(" PRIMARY KEY (");
            
            if (_partitionKeys.Any())
            {
                if (_partitionKeys.Count == 1)
                {
                    cql.Append(_partitionKeys[0]);
                }
                else
                {
                    cql.Append($"({string.Join(", ", _partitionKeys)})");
                }

                if (_clusteringKeys.Any())
                {
                    cql.Append($", {string.Join(", ", _clusteringKeys)}");
                }
            }
            else if (_clusteringKeys.Any())
            {
                cql.Append(string.Join(", ", _clusteringKeys));
            }

            cql.Append(")");
        }

        cql.Append(";");
        return cql.ToString();
    }

    /// <summary>
    /// Builds the DROP MATERIALIZED VIEW CQL statement
    /// </summary>
    public string BuildDropCql()
    {
        var viewNameWithKeyspace = !string.IsNullOrEmpty(_keyspace) 
            ? $"{_keyspace}.{_viewName}" 
            : _viewName;
        
        return $"DROP MATERIALIZED VIEW IF EXISTS {viewNameWithKeyspace};";
    }
}