namespace Argon.Api.Extensions;

using Microsoft.OpenApi.Models;

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
        }).AddEndpointsApiExplorer();

        return builder;
    }
}