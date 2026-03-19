using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

namespace WebhookInspector.Api.Telemetry;

public static class TelemetrySetup
{
    /// <summary>
    /// OpenTelemetry tracing with capture-route redaction. Exports to Application Insights
    /// when APPLICATIONINSIGHTS_CONNECTION_STRING is set (Azure); otherwise traces are only
    /// collected in-process (local development and tests).
    /// </summary>
    public static WebApplicationBuilder AddAppTelemetry(this WebApplicationBuilder builder)
    {
        var otel = builder.Services.AddOpenTelemetry();

        // Registered first so it runs before any exporter's processor sees the span.
        otel.WithTracing(tracing => tracing.AddProcessor(new CaptureRedactionProcessor()));

        // Health probes run every few seconds and carry no information worth paying for.
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health"));

        var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrEmpty(connectionString))
        {
            otel.UseAzureMonitor(options =>
            {
                options.ConnectionString = connectionString;
                options.SamplingRatio = builder.Configuration.GetValue("Telemetry:SamplingRatio", 0.25f);
                options.TracesPerSecond = null;
                // Live Metrics streams request data outside the normal exporter pipeline,
                // so it is turned off rather than trusted to see only redacted spans.
                options.EnableLiveMetrics = false;
            });
        }
        else
        {
            otel.WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());
        }

        return builder;
    }
}
