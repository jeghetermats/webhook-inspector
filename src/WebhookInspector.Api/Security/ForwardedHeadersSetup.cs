using Microsoft.AspNetCore.HttpOverrides;

namespace WebhookInspector.Api.Security;

/// <summary>
/// Behind the Azure Container Apps ingress the client IP and scheme arrive in
/// X-Forwarded-For and X-Forwarded-Proto. They must be applied before anything else runs,
/// including middleware the framework inserts on its own, or parts of the pipeline see
/// http instead of https. A startup filter guarantees that position.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddAppForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Only the entry appended by the ingress, so a client cannot spoof its IP or scheme
            // by sending its own X-Forwarded-* headers.
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        services.AddTransient<IStartupFilter, ForwardedHeadersFirst>();
        return services;
    }

    private sealed class ForwardedHeadersFirst : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseForwardedHeaders();
            next(app);
        };
    }
}
