namespace Argon.Features.NatsStreaming;

using NATS.Client.Core;

public class NatsConfiguration
{
    private Func<NatsOpts, NatsOpts> configure = opts => opts;
    
    public void AddConfigurator(Func<NatsOpts, NatsOpts> cfg)
        => this.configure += cfg;

    public NatsOpts Configure(NatsOpts opts)
        => configure(opts);
}