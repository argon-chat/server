namespace Argon.Features.Admin;

using Argon.Features.Vault;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

public static class AdminAuthFeature
{
    public static IServiceCollection AddOperatorAuth(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<VaultPkiOptions>(
            builder.Configuration.GetSection(VaultPkiOptions.SectionName));
        builder.Services.Configure<OperatorJwtOptions>(
            builder.Configuration.GetSection(OperatorJwtOptions.SectionName));

        builder.Services.AddSingleton<IVaultPkiService, VaultPkiService>();
        builder.Services.AddSingleton<OperatorJwtService>();
        builder.Services.AddScoped<IOperatorCertificateService, OperatorCertificateService>();

        builder.Services.AddAuthentication()
           .AddScheme<AuthenticationSchemeOptions, OperatorCertificateAuthenticationHandler>(
                OperatorCertificateAuthenticationHandler.SchemeName, _ => { });

        return builder.Services;
    }
}
