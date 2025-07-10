namespace Argon.Cassandra.Query;

public class QueryAnalysisResult
{
    public bool         RequiresAllowFiltering { get; set; }
    public List<string> Warnings               { get; } = [];
    public List<string> Suggestions            { get; } = [];
    public List<string> Errors                 { get; } = [];

    public void AddError(string warning)         => Errors.Add(warning);
    public void AddWarning(string warning)       => Warnings.Add(warning);
    public void AddSuggestion(string suggestion) => Suggestions.Add(suggestion);

    public bool HasIssues => RequiresAllowFiltering || Warnings.Any() || Errors.Any();


    public void PrintToLog(ILogger logger)
    {
        if (Errors.Count > 0)
        {
            logger.LogError("CQL Query Analysis: {Count} error(s) found.", Errors.Count);
            foreach (var error in Errors)
            {
                logger.LogError("  ❌ {Error}", error);
            }
        }

        if (Warnings.Count > 0)
        {
            logger.LogWarning("CQL Query Analysis: {Count} warning(s) found.", Warnings.Count);
            foreach (var warning in Warnings)
            {
                logger.LogWarning("  ⚠️  {Warning}", warning);
            }
        }

        if (Suggestions.Count > 0)
        {
            logger.LogInformation("CQL Query Analysis: {Count} suggestion(s).", Suggestions.Count);
            foreach (var suggestion in Suggestions)
            {
                logger.LogInformation("  💡 {Suggestion}", suggestion);
            }
        }

        if (RequiresAllowFiltering)
        {
            logger.LogWarning("Query likely requires ALLOW FILTERING.");
        }

        if (!HasIssues)
        {
            logger.LogInformation("✅ Query analysis completed: no issues found.");
        }
    }
}