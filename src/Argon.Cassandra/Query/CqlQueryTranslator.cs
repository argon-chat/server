namespace Argon.Cassandra.Query;

using Core;
using Mapping;

public class CqlQueryTranslator : ExpressionVisitor
{
    private readonly List<Expression>                       whereClauses = new();
    private readonly List<(string Column, bool Descending)> orderBy      = new();
    private readonly List<object>                           parameters   = new();
    private          EntityMetadata?                        metadata;
    private          int?                                   limit;
    private          CqlResultType                          resultType = CqlResultType.Enumerable;

    public EntityMetadata? GetMetadata() => metadata;

    public CqlQueryInfo Translate(Expression expression)
    {
        Visit(expression);

        var cql = new StringBuilder();
        cql.Append(resultType == CqlResultType.Count ? "SELECT COUNT(*) FROM " : "SELECT * FROM ");

        if (metadata == null)
            throw new InvalidOperationException("Entity metadata not initialized");

        cql.Append(metadata.GetFullTableName());

        if (whereClauses.Any())
        {
            cql.Append(" WHERE ");
            var visitor    = new CqlPredicateVisitor(metadata, parameters);
            var predicates = whereClauses.Select(expr => visitor.Translate(expr));
            cql.Append(string.Join(" AND ", predicates));
        }

        if (orderBy.Any())
        {
            cql.Append(" ORDER BY ");
            cql.Append(string.Join(", ", orderBy.Select(o => o.Column + (o.Descending ? " DESC" : ""))));
        }

        if (limit.HasValue)
        {
            cql.Append(" LIMIT ").Append(limit.Value);
        }

        return new CqlQueryInfo(cql.ToString(), parameters, resultType);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Where":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambda1 })
                    whereClauses.Add(lambda1.Body);
                break;

            case "OrderBy":
            case "OrderByDescending":
            case "ThenBy":
            case "ThenByDescending":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is UnaryExpression { Operand: LambdaExpression { Body: MemberExpression { Member: PropertyInfo prop } } })
                {
                    if (metadata == null) throw new InvalidOperationException();
                    var column = metadata.GetColumnName(prop);
                    orderBy.Add((column, node.Method.Name.EndsWith("Descending")));
                }

                break;

            case "Take":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is ConstantExpression constant)
                    limit = Convert.ToInt32(constant.Value);
                break;

            case "First":
                resultType = CqlResultType.First;
                Visit(node.Arguments[0]);
                limit ??= 1;
                break;

            case "FirstOrDefault":
                resultType = CqlResultType.FirstOrDefault;
                Visit(node.Arguments[0]);
                limit ??= 1;
                break;

            case "Single":
                resultType = CqlResultType.Single;
                Visit(node.Arguments[0]);
                limit ??= 2;
                break;

            case "SingleOrDefault":
                resultType = CqlResultType.SingleOrDefault;
                Visit(node.Arguments[0]);
                limit ??= 2;
                break;

            case "Count":
                resultType = CqlResultType.Count;
                Visit(node.Arguments[0]);
                if (node.Arguments.Count > 1 && node.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambdaCount })
                    whereClauses.Add(lambdaCount.Body);
                break;

            case "Any":
                resultType = CqlResultType.Any;
                Visit(node.Arguments[0]);
                if (node.Arguments.Count > 1 && node.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambdaAny })
                    whereClauses.Add(lambdaAny.Body);
                limit ??= 1;
                break;

            default:
                return base.VisitMethodCall(node);
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable q)
            metadata = EntityMetadataCache.GetMetadata(GetEntityTypeFromQueryable(q));
        return base.VisitConstant(node);
    }

    private static Type GetEntityTypeFromQueryable(object queryable)
    {
        var qt = queryable.GetType();
        if (qt.IsGenericType && qt.GetGenericTypeDefinition() == typeof(CassandraDbSet<>))
            return qt.GetGenericArguments()[0];

        return qt
           .GetInterfaces()
           .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>))
           ?.GetGenericArguments()[0] ?? typeof(object);
    }
}