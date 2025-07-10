namespace Argon.Cassandra.Query;

public sealed record CqlQueryInfo(string Cql, List<object> Parameters, CqlResultType ResultType);