namespace WebhookInspector.Api.Replay;

public static class ReplaySetup
{
    public static IServiceCollection AddReplay(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReplayOptions>(configuration.GetSection(ReplayOptions.SectionName));
        services.AddSingleton<IReplayAddressPolicy, PublicAddressPolicy>();
        services.AddSingleton<IReplayHostResolver, DnsHostResolver>();
        // A plain singleton, not IHttpClientFactory: the factory's logging writes request URLs.
        services.AddSingleton<ReplayClient>();
        services.AddSingleton<ReplayLimiter>();
        return services;
    }
}
