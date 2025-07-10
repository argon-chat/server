namespace Argon.Cassandra.Query;

using Mapping;
using System.Text.RegularExpressions;
using static System.Text.RegularExpressions.Regex;

public static class CqlQueryAnalyzer
{
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
        if (cql.Contains("ALLOW FILTERING", StringComparison.OrdinalIgnoreCase) &&
            hasPartitionKeyFilter &&
            !cql.Contains("!=", StringComparison.OrdinalIgnoreCase) &&
            !IsMatch(cql, @"\bIN\s*\(", RegexOptions.IgnoreCase) &&
            !cql.Contains("CONTAINS", StringComparison.OrdinalIgnoreCase) &&
            !IsMatch(cql, @"\b(<=|>=|<|>)\b", RegexOptions.IgnoreCase))
            result.AddSuggestion("ALLOW FILTERING is used, but might be unnecessary.");

        if ((cql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
             cql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) &&
            !cql.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            result.AddError("UPDATE or DELETE query without WHERE clause may affect all rows in the partition or is invalid.");

        
        // LIMIT clause
        if (!cql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            result.AddSuggestion("Query does not specify a LIMIT, which may result in large result sets.");
        }

        // Explicit ALLOW FILTERING
        if (cql.Contains("ALLOW FILTERING", StringComparison.OrdinalIgnoreCase))
        {
            result.AddSuggestion("Query explicitly includes ALLOW FILTERING.");
            result.RequiresAllowFiltering = true;
        }

        // IN clause
        if (IsMatch(cql, @"\bIN\s*\(", RegexOptions.IgnoreCase))
        {
            result.AddWarning("Query uses IN clause, which can lead to performance issues if misused.");
        }

        // CONTAINS usage
        if (cql.Contains("CONTAINS", StringComparison.OrdinalIgnoreCase))
        {
            result.AddWarning("Query uses CONTAINS, which may require ALLOW FILTERING and can impact performance.");
            result.RequiresAllowFiltering = true;
        }

        // Range without partition key
        if (IsMatch(cql, @"\b(<=|>=|<|>)\b", RegexOptions.IgnoreCase) && !hasPartitionKeyFilter)
        {
            result.AddWarning("Range queries without partition key may require ALLOW FILTERING.");
            result.RequiresAllowFiltering = true;
        }

        // SELECT * usage
        if (IsMatch(cql, @"SELECT\s+\*", RegexOptions.IgnoreCase))
        {
            result.AddWarning("Query uses SELECT *, which may lead to unnecessary data transfer.");
        }

        // Check for ordering by non-clustering columns
        if (!cql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)) return result;
        var orderByPart = cql[cql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        var isValidOrdering = metadata.ClusteringKeys.Any(ck =>
            metadata.ColumnMappings.TryGetValue(ck, out var columnName) &&
            orderByPart.Contains(columnName, StringComparison.OrdinalIgnoreCase));

        if (isValidOrdering)
            return result;
        result.AddWarning("Ordering by non-clustering columns may require ALLOW FILTERING.");
        result.RequiresAllowFiltering = true;
        return result;
    }
}