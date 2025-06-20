namespace Argon.Features.Vault;

using System.Globalization;
using System.Net;
using System.Text.Json;
using VaultSharp.Core;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.Commons;

using VaultSharp;
using Serilog;

/// <summary>
/// Vault configuration provider.
/// </summary>
public class VaultConfigurationProvider : ConfigurationProvider
{
    private IVaultClient? vaultClient;
    private readonly Dictionary<string, int> versionsCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="VaultConfigurationProvider"/> class.
    /// </summary>
    /// <param name="source">Vault configuration source.</param>
    /// <param name="logger">Logger instance.</param>
    public VaultConfigurationProvider(VaultConfigurationSource source)
    {
        this.ConfigurationSource = source ?? throw new ArgumentNullException(nameof(source));
        this.versionsCache = new Dictionary<string, int>();
    }

    /// <summary>
    /// Gets <see cref="VaultConfigurationSource"/>.
    /// </summary>
    internal VaultConfigurationSource ConfigurationSource { get; private set; }
    
    /// <inheritdoc/>
    public override void Load()
    {
        try
        {
            if (this.vaultClient == null)
            {
                IAuthMethodInfo authMethod;
                if (this.ConfigurationSource.Options.AuthMethod != null)
                {
                    authMethod = this.ConfigurationSource.Options.AuthMethod;
                }
                else if (!string.IsNullOrEmpty(this.ConfigurationSource.Options.VaultRoleId) &&
                    !string.IsNullOrEmpty(this.ConfigurationSource.Options.VaultSecret))
                {
                    Log.Logger.Debug("VaultConfigurationProvider: using AppRole authentication");
                    authMethod = new AppRoleAuthMethodInfo(
                        this.ConfigurationSource.Options.VaultRoleId,
                        this.ConfigurationSource.Options.VaultSecret);
                }
                else
                {
                    Log.Logger.Debug("VaultConfigurationProvider: using Token authentication");
                    authMethod = new TokenAuthMethodInfo(this.ConfigurationSource.Options.VaultToken);
                }

                var vaultClientSettings = new VaultClientSettings(this.ConfigurationSource.Options.VaultAddress, authMethod)
                {
                    UseVaultTokenHeaderInsteadOfAuthorizationHeader = true,
                    Namespace = this.ConfigurationSource.Options.Namespace,

                    PostProcessHttpClientHandlerAction = handler =>
                    {
                        if (handler is not HttpClientHandler clientHandler) return;
                        if (this.ConfigurationSource.Options.AcceptInsecureConnection)
                            clientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                        else if (this.ConfigurationSource.Options.ServerCertificateCustomValidationCallback != null)
                            clientHandler.ServerCertificateCustomValidationCallback = this.ConfigurationSource.Options.ServerCertificateCustomValidationCallback;

                        this.ConfigurationSource.Options.PostProcessHttpClientHandlerAction?.Invoke(clientHandler);
                    }
                };
                this.vaultClient = new VaultClient(vaultClientSettings);
            }

            var hasChanges = this.LoadVaultDataAsync(this.vaultClient).GetAwaiter().GetResult();

            if (hasChanges)
                this.OnReload();

        }
        catch (Exception e) when (e is VaultApiException or HttpRequestException)
        {
            Log.Logger.Error(e, "Cannot load configuration from Vault");
            throw;
        }
    }

    /// <summary>
    /// This will fetch the vault token again before the new operation.
    /// Use IConfiguration object: configurationRoot.Providers.OfType&lt;VaultConfigurationProvider&lt;().FirstOrDefault().ResetToken(); 
    /// See https://github.com/rajanadar/VaultSharp/blob/34ab400c2a295f4a81d97fc5d65f38509c7e0f05/README.md?plain=1#L92
    /// </summary>
    public void ResetToken() => this.vaultClient?.V1.Auth.ResetVaultToken();

    private async Task<bool> LoadVaultDataAsync(IVaultClient vaultClient)
    {
        var hasChanges = false;
        await foreach (var secretData in this.ReadKeysAsync(vaultClient, this.ConfigurationSource.BasePath))
        {
            Log.Logger.Information($"VaultConfigurationProvider: got Vault data with key `{secretData.Key}`");

            var key = secretData.Key;
            var len = this.ConfigurationSource.BasePath.TrimStart('/').Length;
            key = key.TrimStart('/').Substring(len).TrimStart('/').Replace('/', ':');
            key = this.ReplaceTheAdditionalCharactersForConfigurationPath(key);

            if (this.ConfigurationSource.Options.KeyPrefix != null)
            {
                if (string.IsNullOrEmpty(key))
                {
                    key = this.ConfigurationSource.Options.KeyPrefix;
                }
                else
                {
                    key = this.ConfigurationSource.Options.KeyPrefix + ":" + key;
                }

            }
            var data = secretData.SecretData.Data;

            var shouldSetValue = true;
            if (this.versionsCache.TryGetValue(key, out var currentVersion))
            {
                shouldSetValue = secretData.SecretData.Metadata.Version > currentVersion;
                var keyMsg = shouldSetValue ? "has new version" : "is outdated";
                Log.Logger.Information($"{nameof(VaultConfigurationProvider)}: Data for key `{secretData.Key}` {keyMsg}");
            }

            if (!shouldSetValue) continue;
            this.SetData(data, this.ConfigurationSource.Options.OmitVaultKeyName ? string.Empty : key);
            hasChanges              = true;
            this.versionsCache[key] = secretData.SecretData.Metadata.Version;
        }

        return hasChanges;
    }

    private void SetData<TValue>(IEnumerable<KeyValuePair<string, TValue>> data, string? key)
    {
        foreach (var pair in data)
        {
            var nestedKey = string.IsNullOrEmpty(key) ? pair.Key : $"{key}:{pair.Key}";
            nestedKey = this.ReplaceTheAdditionalCharactersForConfigurationPath(nestedKey);

            var nestedValue = (JsonElement)(object)pair.Value!;
            this.SetItemData(nestedKey, nestedValue);
        }
    }

    private void SetItemData(string nestedKey, JsonElement nestedValue)
    {
        switch ((nestedValue).ValueKind)
        {
            case JsonValueKind.Object:
            var jObject = nestedValue.EnumerateObject().ToDictionary(x => x.Name, x => x.Value).ToList();
            if (jObject != null)
                this.SetData(jObject, nestedKey);
            break;
            case JsonValueKind.Array:
            var array = nestedValue.EnumerateArray();
            for (var i = 0; i < array.Count(); i++)
            {
                var arrElement = array.ElementAt(i);

                if (arrElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    this.SetData([new KeyValuePair<string, JsonElement?>($"{nestedKey}:{i}", arrElement)], null);
                else
                    this.SetItemData($"{nestedKey}:{i}", arrElement);
            }
            break;
            case JsonValueKind.String:
            this.Set(nestedKey, nestedValue.GetString());
            break;
            case JsonValueKind.Number:
            this.Set(nestedKey, nestedValue.GetDecimal().ToString(CultureInfo.InvariantCulture));
            break;
            case JsonValueKind.True:
            this.Set(nestedKey, true.ToString());
            break;
            case JsonValueKind.False:
            this.Set(nestedKey, false.ToString());
            break;
            case JsonValueKind.Null:
            this.Set(nestedKey, null);
            break;
            default:
            break;
        }
    }

    private async IAsyncEnumerable<KeyedSecretData> ReadKeysAsync(IVaultClient vaultClient, string path)
    {
        Secret<ListInfo>? keys = null;
        var folderPath = path;

        if (this.ConfigurationSource.Options.AlwaysAddTrailingSlashToBasePath && folderPath.EndsWith("/", StringComparison.InvariantCulture) == false)
        {
            folderPath += "/";
        }

        if (folderPath.EndsWith("/", StringComparison.InvariantCulture))
        {
            try
            {
                keys = await vaultClient.V1.Secrets.KeyValue.V2.ReadSecretPathsAsync(path, this.ConfigurationSource.MountPoint);
            }
            catch (VaultApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // this is key, not a folder
            }
        }

        if (keys != null)
        {
            foreach (var key in keys.Data.Keys)
            {
                var keyData = this.ReadKeysAsync(vaultClient, folderPath + key);
                await foreach (var secretData in keyData)
                {
                    yield return secretData;
                }
            }
        }

        var valuePath = path;
        if (valuePath.EndsWith("/", StringComparison.InvariantCulture) == true)
        {
            valuePath = valuePath.TrimEnd('/');
        }

        KeyedSecretData? keyedSecretData = null;
        try
        {
            var secretData = await vaultClient.V1.Secrets.KeyValue.V2.ReadSecretAsync(valuePath, null, this.ConfigurationSource.MountPoint)
                .ConfigureAwait(false);
            keyedSecretData = new KeyedSecretData(valuePath, secretData.Data);
        }
        catch (VaultApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // this is folder, not a key
        }

        if (keyedSecretData != null)
        {
            yield return keyedSecretData;
        }
    }

    private string ReplaceTheAdditionalCharactersForConfigurationPath(string inputKey)
    {
        if (!this.ConfigurationSource.Options.AdditionalCharactersForConfigurationPath.Any())
        {
            return inputKey;
        }

        var outputKey = new StringBuilder(inputKey);

        foreach (var c in this.ConfigurationSource.Options.AdditionalCharactersForConfigurationPath)
        {
            outputKey.Replace(c, ':');
        }

        return outputKey.ToString();
    }

    private class KeyedSecretData(string key, SecretData secretData)
    {
        public string Key { get; } = key;

        public SecretData SecretData { get; } = secretData;
    }
}