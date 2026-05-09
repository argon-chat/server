namespace Argon.Features.Admin;

using Vault;
using Microsoft.Extensions.DependencyInjection;

public static class AdminAuthFeature
{
    public static IServiceCollection AddOperatorAuth(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<OperatorAuthOptions>(
            builder.Configuration.GetSection(OperatorAuthOptions.SectionName));
        builder.Services.Configure<VaultPkiOptions>(
            builder.Configuration.GetSection(VaultPkiOptions.SectionName));
        builder.Services.AddSingleton<IVaultPkiService, VaultPkiService>();
        builder.Services.AddScoped<IOperatorCertificateService, OperatorCertificateService>();

        return builder.Services;
    }
}
