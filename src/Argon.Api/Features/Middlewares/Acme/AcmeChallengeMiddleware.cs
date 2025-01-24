namespace Argon.Features.Middlewares.Acme;

using System.Drawing;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Env;
using k8s;
using k8s.Models;
using StackExchange.Redis;

public class AcmeChallengeMiddleware(RequestDelegate next, IServiceProvider provider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.HasValue)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var scope = provider.CreateAsyncScope();

        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db    = redis.GetDatabase();

        var token   = context.Request.Path.Value.Split('/').Last();
        var keyAuth = await db.StringGetAsync(token);

        if (!string.IsNullOrEmpty(keyAuth))
        {
            await context.Response.WriteAsync(keyAuth.ToString());
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}

public class AcmeChallengeOptions
{
    public List<string> Domains { get; set; }
    public List<string> EMails  { get; set; }
}

public class AcmeChallenge(IServiceProvider provider, ILogger<AcmeChallenge> logger, IHostEnvironment env, IOptions<AcmeChallengeOptions> options)
{
    public async Task ConfigureAcmeChallenge()
    {
        if (!env.IsKube())
            return;
        await using var scope = provider.CreateAsyncScope();

        var redis   = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db      = redis.GetDatabase();
        var acme    = new AcmeContext(WellKnownServers.LetsEncryptV2);
        var account = await acme.NewAccount(options.Value.EMails, true);

        var order = await acme.NewOrder(options.Value.Domains);

        foreach (var auth in await order.Authorizations())
        {
            var httpChallenge = await auth.Http();
            var token         = httpChallenge.Token;
            var keyAuth       = httpChallenge.KeyAuthz;

            await db.StringSetAsync(token, keyAuth, TimeSpan.FromMinutes(5));

            logger.LogInformation($"KeyAuth: {keyAuth}, Location: {httpChallenge.Location}");

            await httpChallenge.Validate();
        }


        var orderStatus = await order.Resource();

        while (orderStatus.Status != OrderStatus.Valid)
        {
            logger.LogInformation("Waiting for validation... Current status: {acmeStatus}", orderStatus.Status);
            await Task.Delay(5000);
            orderStatus = await order.Resource();
        }

        var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
        var cert = await order.Generate(new CsrInfo
        {
            CommonName = options.Value.Domains.First()
        }, privateKey);


        await UpdateKubernetesSecret("argon", "argon-tls", cert.ToPem(), privateKey.ToPem());
    }


    private async Task UpdateKubernetesSecret(string namespaceName, string secretName, string certPem, string keyPem)
    {
        var k8sClient = new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name              = secretName,
                NamespaceProperty = namespaceName
            },
            Type = "kubernetes.io/tls",
            Data = new Dictionary<string, byte[]>
            {
                { "tls.crt", Encoding.UTF8.GetBytes(certPem) },
                { "tls.key", Encoding.UTF8.GetBytes(keyPem) }
            }
        };

        try
        {
            var existingSecret = await k8sClient.ReadNamespacedSecretAsync(secretName, namespaceName);
            existingSecret.Data = secret.Data;
            await k8sClient.ReplaceNamespacedSecretAsync(existingSecret, secretName, namespaceName);
            logger.LogInformation("Kubernetes secret updated.");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await k8sClient.CreateNamespacedSecretAsync(secret, namespaceName);
            logger.LogInformation("Kubernetes secret created.");
        }
    }
}