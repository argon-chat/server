namespace Argon.Api.Features.Orleans.Consul;

public class ConsulOrleansTableVersion
{
    public const string Path = "orleans/lock";

    public string ETag { get; set; } = string.Empty;
    public int Version { get; set; }

    public static ConsulOrleansTableVersion Create(TableVersion version) => new()
    {
        Version = version.Version,
        ETag    = version.VersionEtag
    };

    public static TableVersion Create(ConsulOrleansTableVersion version) => new(version.Version, version.ETag);

    public TableVersion ToTable() => Create(this);
}