namespace Argon.Api.Features.Mapper;

using System.Runtime.CompilerServices;
using AutoMapper;

public static class FuckingAutoMapperIHateThisLibraryStupidIdiots
{
    public static TMappingExpression ConstructAutoUninitialized<TSource, TDestination, TMappingExpression>(
        this IProjectionExpressionBase<TSource, TDestination, TMappingExpression> fuckingAutoMapperExtensions)
        where TMappingExpression : IProjectionExpressionBase<TSource, TDestination, TMappingExpression> =>
        fuckingAutoMapperExtensions.ConstructUsing(source => (TDestination)RuntimeHelpers.GetUninitializedObject(typeof(TDestination)));
}