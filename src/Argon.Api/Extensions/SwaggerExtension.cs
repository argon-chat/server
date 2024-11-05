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
                       Name        = "x-argon-token",
                       In          = ParameterLocation.Header,
                       Description = "access token"
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
               })
               .AddEndpointsApiExplorer();

        return builder;
    }
}