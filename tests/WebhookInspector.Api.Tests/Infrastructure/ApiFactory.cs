using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebhookInspector.Api.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings)
    : WebApplicationFactory<Program>
{
    /// <summary>Every log entry the app wrote, using the app's real log level configuration.</summary>
    public TestLogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" loads appsettings.json but not appsettings.Development.json,
        // so log levels are the same as in production.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Capture:PermitsPerTokenPerMinute", "100000");
        builder.UseSetting("Capture:PermitsPerIpPerMinute", "100000");
        builder.UseSetting("Capture:MaxConcurrentCaptures", "1000");
        builder.UseSetting("Capture:CaptureQueueLimit", "1000");
        builder.UseSetting("TestSignIn:Enabled", "true");

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new TestLoggerProvider(Logs)));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Logs);
        });
    }
}
