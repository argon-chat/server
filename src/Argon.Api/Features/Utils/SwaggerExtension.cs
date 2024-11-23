namespace Argon.Api.Extensions;

using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public static class SwaggerExtension
{
    public static WebApplicationBuilder AddSwaggerWithAuthHeader(this WebApplicationBuilder builder)
    {
        builder.Services.AddSwaggerGen(c =>
        {
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                In          = ParameterLocation.Header,
                Description = "Please insert JWT with Bearer into field",
                Name        = "Authorization",
                Type        = SecuritySchemeType.ApiKey
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id   = "Bearer"
                        }
                    },
                    []
                }
            });
            c.OperationFilter<AddHeaderParameterOperationFilter>();
        }).AddEndpointsApiExplorer();

        return builder;
    }
}

public class AddHeaderParameterOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name        = "X-Host-Name",
            In          = ParameterLocation.Header,
            Description = "Host Name",
            Required    = true,
            Schema = new OpenApiSchema
            {
                Type = "string"
            }
        });
    }
}