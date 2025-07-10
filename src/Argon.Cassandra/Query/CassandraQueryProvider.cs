namespace Argon.Cassandra.Query;

using Core;
using Mapping;
using Microsoft.EntityFrameworkCore.Query;

public class CassandraQueryProvider<T>(CassandraDbContext context, ILogger<CassandraQueryProvider<T>> logger) : IAsyncQueryProvider where T : class
{
    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = GetElementType(expression.Type);
        var queryType   = typeof(CassandraQuery<>).MakeGenericType(elementType);
        return (IQueryable)Activator.CreateInstance(queryType, this, expression)!;
    }
    IQueryable<TElement> IQueryProvider.CreateQuery<TElement>(Expression expression)
    {
        if (!typeof(TElement).IsClass)
            throw new NotSupportedException($"Type {typeof(TElement).Name} must be a reference type");
        var queryType = typeof(CassandraQuery<>).MakeGenericType(typeof(TElement));
        return (IQueryable<TElement>)Activator.CreateInstance(queryType, this, expression)!;
    }
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) where TElement : class
        => new CassandraQuery<TElement>((CassandraQueryProvider<TElement>)(object)this, expression);

    public object? Execute(Expression expression)
        => ExecuteInternal(expression);

    public TResult? Execute<TResult>(Expression expression)
        => (TResult?)ExecuteInternal(expression);

    public async Task<TResult?> DoExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var result = ExecuteInternalAsync(expression, cancellationToken);

        if (typeof(TResult).GetGenericTypeDefinition() == typeof(Task<>))
            return (TResult)(object)result;

        return (TResult?)await result;
    }

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<object>();

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await ExecuteInternalAsync(expression, cancellationToken);
                tcs.SetResult(result!);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, cancellationToken);

        var resultTask = tcs.Task;

        var resultType = typeof(TResult).GetGenericArguments().Single();

        var castedTask = typeof(Task)
           .GetMethod(nameof(Task.FromResult))!
           .MakeGenericMethod(resultType)
           .Invoke(null, [resultTask.Result]);

        return (TResult)castedTask!;
    }

    private object? ExecuteInternal(Expression expression)
    {
        var translator = new CqlQueryTranslator();
        var queryInfo  = translator.Translate(expression);

        logger.LogInformation("Created cassandra query, {cql}", queryInfo.Cql);
        #if DEBUG
        CqlQueryAnalyzer.AnalyzeQuery(queryInfo.Cql, translator.GetMetadata()!).PrintToLog(logger);
        #endif
        var result   = context.ExecuteCql(queryInfo.Cql, queryInfo.Parameters.ToArray());
        var metadata = EntityMetadataCache.GetMetadata<T>();

        return ProcessResult(result, queryInfo, metadata);
    }

    private async Task<object?> ExecuteInternalAsync(Expression expression, CancellationToken ct = default)
    {
        var translator = new CqlQueryTranslator();
        var queryInfo  = translator.Translate(expression);


        logger.LogInformation("Created cassandra query, {cql}", queryInfo.Cql);
        #if DEBUG
        CqlQueryAnalyzer.AnalyzeQuery(queryInfo.Cql, translator.GetMetadata()!).PrintToLog(logger);
        #endif

        var result   = await context.ExecuteCqlAsync(queryInfo.Cql, queryInfo.Parameters.ToArray()).ConfigureAwait(false);
        var metadata = EntityMetadataCache.GetMetadata<T>();

        return ProcessResult(result, queryInfo, metadata);
    }

    private object? ProcessResult(RowSet result, CqlQueryInfo queryInfo, EntityMetadata metadata)
    {
        var entities = result.Select(row => MapRowToEntity(row, metadata)).ToList();

        return queryInfo.ResultType switch
        {
            CqlResultType.Enumerable      => entities,
            CqlResultType.Single          => entities.Single(),
            CqlResultType.SingleOrDefault => entities.SingleOrDefault(),
            CqlResultType.First           => entities.First(),
            CqlResultType.FirstOrDefault  => entities.FirstOrDefault(),
            CqlResultType.Count           => entities.Count,
            CqlResultType.Any             => entities.Any(),
            _                             => entities
        }; ;
    }

    private T MapRowToEntity(Row row, EntityMetadata metadata)
    {
        var entity = Activator.CreateInstance<T>();
        foreach (var property in metadata.Properties)
        {
            var columnName = metadata.GetColumnName(property);

            try
            {
                if (!row.IsNull(columnName))
                {
                    if (metadata.Converters.TryGetValue(property, out var converter))
                    {
                        var v = row.GetValue(converter.FromType, columnName);
                        property.SetValue(entity, converter.BoxedConvertFrom(v));
                    }
                    else
                    {
                        var value = row.GetValue(property.PropertyType, columnName);
                        property.SetValue(entity, value);
                    }
                }
            }
            catch (ArgumentException) { }
        }

        context.TrackEntity(entity, EntityState.Unchanged);

        return entity;
    }

    private static Type GetElementType(Type type)
    {
        while (true)
        {
            if (type.HasElementType)
                return type.GetElementType()!;

            if (type.IsGenericType)
                foreach (var arg in type.GetGenericArguments())
                {
                    var iType = typeof(IEnumerable<>).MakeGenericType(arg);
                    if (iType.IsAssignableFrom(type))
                        return arg;
                }

            var interfaces = type.GetInterfaces();
            if (interfaces.Length > 0)
                foreach (var @interface in interfaces)
                {
                    var elementType = GetElementType(@interface);
                    if (elementType != typeof(object))
                        return elementType;
                }

            if (type.BaseType == null || type.BaseType == typeof(object))
                return typeof(object);
            type = type.BaseType;
        }
    }
}