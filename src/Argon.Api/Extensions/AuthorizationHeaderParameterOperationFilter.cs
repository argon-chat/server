namespace Argon.Api.Extensions;

using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public class AuthorizationHeaderParameterOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters =
        [
            new OpenApiParameter
            {
                Name = "x-argon-token",
                Description = "access token",
                Required = false,
                Schema = new OpenApiSchema
                {
                    Type = "string",
                    Default = new OpenApiString("Bearer ")
                }
            },
        ];
        // var filterPipeline = context.ApiDescription.ActionDescriptor.FilterDescriptors;
        // var isAuthorized = filterPipeline.Select(filterInfo => filterInfo.Filter)
        //     .Any(filter => filter is AuthorizeFilter);
        // var allowAnonymous = filterPipeline.Select(filterInfo => filterInfo.Filter)
        //     .Any(filter => filter is IAllowAnonymousFilter);
        //
        // if (isAuthorized && !allowAnonymous)
        // {
        //     if (operation.Parameters == null)
        //         operation.Parameters = new List<OpenApiParameter>();
        //
        //     operation.Parameters.Add(new OpenApiParameter
        //     {
        //         Name = "x-argon-token",
        //         In = ParameterLocation.Header,
        //         Description = "access token",
        //         Required = false,
        //         Schema = new OpenApiSchema
        //         {
        //             Type = "string",
        //             Default = new OpenApiString("Bearer ")
        //         }
        //     });
        // }
    }
}