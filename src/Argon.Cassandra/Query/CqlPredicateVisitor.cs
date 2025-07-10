namespace Argon.Cassandra.Query;

using Mapping;

public class CqlPredicateVisitor(EntityMetadata metadata, List<object> parameters) : ExpressionVisitor
{
    private StringBuilder builder = new();
    
    public string Translate(Expression expr)
    {
        builder = new StringBuilder();
        Visit(expr);
        return builder.ToString();
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        Visit(node.Left);

        builder.Append(node.NodeType switch
        {
            ExpressionType.Equal              => " = ",
            ExpressionType.NotEqual           => " != ",
            ExpressionType.GreaterThan        => " > ",
            ExpressionType.GreaterThanOrEqual => " >= ",
            ExpressionType.LessThan           => " < ",
            ExpressionType.LessThanOrEqual    => " <= ",
            ExpressionType.AndAlso            => " AND ",
            ExpressionType.OrElse             => " OR ",

            ExpressionType.And         => throw new NotSupportedException("Bitwise AND not supported in CQL"),
            ExpressionType.Or          => throw new NotSupportedException("Bitwise OR not supported in CQL"),
            ExpressionType.ExclusiveOr => throw new NotSupportedException("Bitwise XOR not supported in CQL"),
            ExpressionType.Add         => throw new NotSupportedException("Add operation not supported in CQL WHERE"),
            ExpressionType.Subtract    => throw new NotSupportedException("Subtract operation not supported in CQL WHERE"),
            ExpressionType.Multiply    => throw new NotSupportedException("Multiply operation not supported in CQL WHERE"),
            ExpressionType.Divide      => throw new NotSupportedException("Divide operation not supported in CQL WHERE"),

            _ => throw new NotSupportedException($"Operator {node.NodeType} not supported")
        });

        Visit(node.Right);
        return node;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression?.NodeType == ExpressionType.Parameter)
        {
            var prop = node.Member as PropertyInfo;
            var name = prop != null ? metadata.GetColumnName(prop) : node.Member.Name.ToLowerInvariant();
            builder.Append(name);
        }
        else
        {
            var value = Expression.Lambda(node).Compile().DynamicInvoke();
            parameters.Add(value ?? DBNull.Value);
            builder.Append('?');
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        parameters.Add(node.Value ?? DBNull.Value);
        builder.Append('?');
        return node;
    }
}