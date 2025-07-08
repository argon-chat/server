using Argon.Cassandra.Mapping;

namespace Argon.Cassandra.Query;

using Mapping;

/// <summary>
/// Advanced CQL generator that converts LINQ expressions to Cassandra Query Language (CQL) statements.
/// </summary>
public class CqlExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _queryBuilder = new();
    private readonly List<object> _parameters = new();
    private readonly EntityMetadata _entityMetadata;
    private string _tableName = string.Empty;
    private bool _isInWhere = false;
    private bool _isInOrderBy = false;

    public CqlExpressionVisitor(EntityMetadata entityMetadata)
    {
        _entityMetadata = entityMetadata ?? throw new ArgumentNullException(nameof(entityMetadata));
        _tableName = _entityMetadata.TableName;
    }

    public string GetCqlQuery() => _queryBuilder.ToString();
    public object[] GetParameters() => _parameters.ToArray();

    /// <summary>
    /// Visits a method call expression and converts it to CQL
    /// </summary>
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Where":
                return VisitWhere(node);
            case "OrderBy":
            case "OrderByDescending":
                return VisitOrderBy(node);
            case "ThenBy":
            case "ThenByDescending":
                return VisitThenBy(node);
            case "Take":
                return VisitTake(node);
            case "Skip":
                throw new NotSupportedException("Skip operations are not supported in Cassandra. Use token-based pagination instead.");
            case "FirstOrDefault":
            case "First":
                return VisitFirst(node);
            case "SingleOrDefault":
            case "Single":
                return VisitSingle(node);
            case "Count":
                return VisitCount(node);
            case "Any":
                return VisitAny(node);
            case "Contains":
                return VisitContains(node);
            default:
                throw new NotSupportedException($"The method '{node.Method.Name}' is not supported in CQL translation.");
        }
    }

    private Expression VisitWhere(MethodCallExpression node)
    {
        if (_queryBuilder.Length == 0)
        {
            _queryBuilder.Append($"SELECT * FROM {_tableName}");
        }

        if (!_isInWhere)
        {
            _queryBuilder.Append(" WHERE ");
            _isInWhere = true;
        }
        else
        {
            _queryBuilder.Append(" AND ");
        }

        // Visit the lambda expression (the predicate)
        var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
        Visit(lambda.Body);

        return node;
    }

    private Expression VisitOrderBy(MethodCallExpression node)
    {
        if (!_isInOrderBy)
        {
            _queryBuilder.Append(" ORDER BY ");
            _isInOrderBy = true;
        }
        else
        {
            _queryBuilder.Append(", ");
        }

        var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
        Visit(lambda.Body);

        if (node.Method.Name == "OrderByDescending")
        {
            _queryBuilder.Append(" DESC");
        }

        return node;
    }

    private Expression VisitThenBy(MethodCallExpression node)
    {
        _queryBuilder.Append(", ");

        var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
        Visit(lambda.Body);

        if (node.Method.Name == "ThenByDescending")
        {
            _queryBuilder.Append(" DESC");
        }

        return node;
    }

    private Expression VisitTake(MethodCallExpression node)
    {
        var limit = (ConstantExpression)node.Arguments[1];
        _queryBuilder.Append($" LIMIT {limit.Value}");
        return node;
    }

    private Expression VisitFirst(MethodCallExpression node)
    {
        _queryBuilder.Append(" LIMIT 1");
        return node;
    }

    private Expression VisitSingle(MethodCallExpression node)
    {
        _queryBuilder.Append(" LIMIT 2"); // Limit 2 to check if more than one exists
        return node;
    }

    private Expression VisitCount(MethodCallExpression node)
    {
        _queryBuilder.Clear();
        _queryBuilder.Append($"SELECT COUNT(*) FROM {_tableName}");
        
        // If there are arguments (i.e., Count(predicate)), handle the predicate
        if (node.Arguments.Count > 1)
        {
            _queryBuilder.Append(" WHERE ");
            _isInWhere = true;
            var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
            Visit(lambda.Body);
        }
        
        return node;
    }

    private Expression VisitAny(MethodCallExpression node)
    {
        _queryBuilder.Clear();
        _queryBuilder.Append($"SELECT * FROM {_tableName}");
        
        if (node.Arguments.Count > 1)
        {
            _queryBuilder.Append(" WHERE ");
            _isInWhere = true;
            var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
            Visit(lambda.Body);
        }
        
        _queryBuilder.Append(" LIMIT 1");
        return node;
    }

    private Expression VisitContains(MethodCallExpression node)
    {
        // Handle collection.Contains(property) scenarios
        if (node.Object != null && node.Arguments.Count == 1)
        {
            Visit(node.Arguments[0]); // The property being checked
            _queryBuilder.Append(" IN (");
            
            // Evaluate the collection
            var collection = Expression.Lambda(node.Object).Compile().DynamicInvoke();
            if (collection is System.Collections.IEnumerable enumerable)
            {
                var items = enumerable.Cast<object>().ToArray();
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0) _queryBuilder.Append(", ");
                    _queryBuilder.Append($"?");
                    _parameters.Add(items[i]);
                }
            }
            
            _queryBuilder.Append(")");
        }
        else
        {
            throw new NotSupportedException("This form of Contains is not supported in CQL translation.");
        }

        return node;
    }

    /// <summary>
    /// Visits a binary expression (comparisons, logical operations)
    /// </summary>
    protected override Expression VisitBinary(BinaryExpression node)
    {
        _queryBuilder.Append("(");
        Visit(node.Left);

        switch (node.NodeType)
        {
            case ExpressionType.Equal:
                _queryBuilder.Append(" = ");
                break;
            case ExpressionType.NotEqual:
                _queryBuilder.Append(" != ");
                break;
            case ExpressionType.LessThan:
                _queryBuilder.Append(" < ");
                break;
            case ExpressionType.LessThanOrEqual:
                _queryBuilder.Append(" <= ");
                break;
            case ExpressionType.GreaterThan:
                _queryBuilder.Append(" > ");
                break;
            case ExpressionType.GreaterThanOrEqual:
                _queryBuilder.Append(" >= ");
                break;
            case ExpressionType.AndAlso:
                _queryBuilder.Append(" AND ");
                break;
            case ExpressionType.OrElse:
                _queryBuilder.Append(" OR ");
                break;
            default:
                throw new NotSupportedException($"The binary operator '{node.NodeType}' is not supported in CQL.");
        }

        Visit(node.Right);
        _queryBuilder.Append(")");

        return node;
    }

    /// <summary>
    /// Visits a member access expression (property access)
    /// </summary>
    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression?.NodeType == ExpressionType.Parameter)
        {
            // This is a property access on the entity
            var columnName = GetColumnName(node.Member.Name);
            _queryBuilder.Append(columnName);
        }
        else
        {
            // This is a member access on a variable or constant - evaluate it
            var lambda = Expression.Lambda(node);
            var compiled = lambda.Compile();
            var value = compiled.DynamicInvoke();
            
            _queryBuilder.Append("?");
            _parameters.Add(value ?? DBNull.Value);
        }

        return node;
    }

    /// <summary>
    /// Visits a constant expression
    /// </summary>
    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value == null)
        {
            _queryBuilder.Append("NULL");
        }
        else if (node.Value is IQueryable)
        {
            // This is the queryable source - ignore it
        }
        else
        {
            _queryBuilder.Append("?");
            _parameters.Add(node.Value);
        }

        return node;
    }

    /// <summary>
    /// Visits a unary expression (like NOT)
    /// </summary>
    protected override Expression VisitUnary(UnaryExpression node)
    {
        switch (node.NodeType)
        {
            case ExpressionType.Not:
                _queryBuilder.Append("NOT ");
                Visit(node.Operand);
                break;
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
                // Ignore conversions in CQL context
                Visit(node.Operand);
                break;
            default:
                throw new NotSupportedException($"The unary operator '{node.NodeType}' is not supported in CQL.");
        }

        return node;
    }    /// <summary>
    /// Gets the column name for a property, considering [Column] attributes
    /// </summary>
    private string GetColumnName(string propertyName)
    {
        var property = _entityMetadata.Properties.FirstOrDefault(p => p.Name == propertyName);
        return property != null && _entityMetadata.ColumnMappings.TryGetValue(property, out var columnName) 
            ? columnName 
            : propertyName.ToLowerInvariant();
    }

    /// <summary>
    /// Removes quotation from expressions
    /// </summary>
    private static Expression StripQuotes(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote)
        {
            e = ((UnaryExpression)e).Operand;
        }
        return e;
    }
}

/// <summary>
/// Query result analyzer for optimizing Cassandra queries
/// </summary>
public static class CqlQueryAnalyzer
{
    /// <summary>
    /// Analyzes if a query is efficiently executable on Cassandra
    /// </summary>
    public static QueryAnalysisResult AnalyzeQuery(string cql, EntityMetadata metadata)
    {
        var result = new QueryAnalysisResult();
          // Check for partition key usage
        var hasPartitionKeyFilter = metadata.PartitionKeys.Any(pk => 
            metadata.ColumnMappings.TryGetValue(pk, out var columnName) && 
            cql.Contains(columnName, StringComparison.OrdinalIgnoreCase));
        
        if (!hasPartitionKeyFilter && cql.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            result.AddWarning("Query does not filter by partition key, which may result in poor performance.");
        }
        
        // Check for ALLOW FILTERING requirement
        if (cql.Contains("!=") || (cql.Contains("WHERE") && !hasPartitionKeyFilter))
        {
            result.RequiresAllowFiltering = true;
            result.AddWarning("Query may require ALLOW FILTERING, which can impact performance.");
        }
        
        // Check for ordering by non-clustering columns
        if (cql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            var orderByPart = cql.Substring(cql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase));            var isValidOrdering = metadata.ClusteringKeys.Any(ck => 
                metadata.ColumnMappings.TryGetValue(ck, out var columnName) &&
                orderByPart.Contains(columnName, StringComparison.OrdinalIgnoreCase));
            
            if (!isValidOrdering)
            {
                result.AddWarning("Ordering by non-clustering columns may require ALLOW FILTERING.");
                result.RequiresAllowFiltering = true;
            }
        }
        
        return result;
    }
}

/// <summary>
/// Result of query analysis
/// </summary>
public class QueryAnalysisResult
{
    public bool RequiresAllowFiltering { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> Suggestions { get; } = new();
    
    public void AddWarning(string warning) => Warnings.Add(warning);
    public void AddSuggestion(string suggestion) => Suggestions.Add(suggestion);
    
    public bool HasIssues => RequiresAllowFiltering || Warnings.Any();
}

/// <summary>
/// Extension methods for LINQ expressions to provide Cassandra-specific functionality
/// </summary>
public static class CassandraLinqExtensions
{
    /// <summary>
    /// Enables token-based pagination for large result sets
    /// </summary>
    public static IQueryable<T> Token<T>(this IQueryable<T> source, object tokenValue) where T : class
    // This would be implemented in the query provider to add TOKEN() function support
        => throw new NotImplementedException("Token-based pagination will be implemented in the query provider.");

    /// <summary>
    /// Allows filtering on non-indexed columns (use with caution)
    /// </summary>
    public static IQueryable<T> AllowFiltering<T>(this IQueryable<T> source) where T : class
    // This would be implemented in the query provider to add ALLOW FILTERING
        => throw new NotImplementedException("Allow filtering will be implemented in the query provider.");

    /// <summary>
    /// Specifies consistency level for the query
    /// </summary>
    public static IQueryable<T> WithConsistency<T>(this IQueryable<T> source, ConsistencyLevel consistency) where T : class
    // This would be implemented in the query provider
        => throw new NotImplementedException("Consistency level specification will be implemented in the query provider.");
}

/// <summary>
/// Consistency levels supported by Cassandra
/// </summary>
public enum ConsistencyLevel
{
    Any,
    One,
    Two,
    Three,
    Quorum,
    All,
    LocalQuorum,
    EachQuorum,
    Serial,
    LocalSerial,
    LocalOne
}
