namespace Argon.Cassandra.Query;

using Mapping;

public class PredicateVisitor(EntityMetadata metadata, List<object> parameters) : ExpressionVisitor
{
    private readonly StringBuilder builder = new();

    public override string ToString() => builder.ToString();

    protected override Expression VisitBinary(BinaryExpression node)
    {
        Visit(node.Left);

        builder.Append(node.NodeType switch
        {
            ExpressionType.Equal              => " = ",
            ExpressionType.NotEqual           => " != ",
            ExpressionType.LessThan           => " < ",
            ExpressionType.LessThanOrEqual    => " <= ",
            ExpressionType.GreaterThan        => " > ",
            ExpressionType.GreaterThanOrEqual => " >= ",
            ExpressionType.AndAlso            => " AND ",
            ExpressionType.OrElse             => " OR ",
            ExpressionType.And                => throw new NotSupportedException("Bitwise AND not supported in CQL"),
            ExpressionType.Or                 => throw new NotSupportedException("Bitwise OR not supported in CQL"),
            ExpressionType.ExclusiveOr        => throw new NotSupportedException("Bitwise XOR not supported in CQL"),
            ExpressionType.Add                => throw new NotSupportedException("Add operation not supported in CQL WHERE"),
            ExpressionType.Subtract           => throw new NotSupportedException("Subtract operation not supported in CQL WHERE"),
            ExpressionType.Multiply           => throw new NotSupportedException("Multiply operation not supported in CQL WHERE"),
            ExpressionType.Divide             => throw new NotSupportedException("Divide operation not supported in CQL WHERE"),
            _ => throw new NotSupportedException($"Unsupported binary op: {node.NodeType}")
        });

        Visit(node.Right);
        return node;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression?.NodeType == ExpressionType.Parameter)
        {
            var column = metadata.ColumnMappings.FirstOrDefault(kv => kv.Key.Name == node.Member.Name).Value
                         ?? node.Member.Name.ToLowerInvariant();
            builder.Append(column);
        }
        else
        {
            var lambda = Expression.Lambda(node);
            var value  = lambda.Compile().DynamicInvoke();
            builder.Append("?");
            parameters.Add(value ?? DBNull.Value);
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        builder.Append("?");
        parameters.Add(node.Value ?? DBNull.Value);
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node)
        => node.NodeType switch
        {
            ExpressionType.Not                                      => Append("NOT ", () => Visit(node.Operand)),
            ExpressionType.Convert or ExpressionType.ConvertChecked => Visit(node.Operand),
            _                                                       => throw new NotSupportedException($"Unsupported unary: {node.NodeType}")
        };

    private Expression Append(string prefix, Func<Expression> inner)
    {
        builder.Append(prefix);
        return inner();
    }
}