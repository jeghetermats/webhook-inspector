using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Data;
using WebhookInspector.Api.Live;
using WebhookInspector.Api.Retention;
using WebhookInspector.Api.Security;
using WebhookInspector.Api.Spa;
using WebhookInspector.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestHeaderCount = 64;
    kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    // Default for every route. The capture route lowers it further per request.
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddDataProtection()
    .SetApplicationName("webhook-inspector")
    .PersistKeysToDbContext<AppDbContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

builder.Services.Configure<CaptureOptions>(builder.Configuration.GetSection(CaptureOptions.SectionName));
builder.Services.AddScoped<CaptureStore>();
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddScoped<RetentionCleanup>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<RequestNotifier>();
builder.Services.AddCaptureRateLimiting();

builder.Services.AddAppAuthentication(builder.Configuration);
builder.Services.AddAppAntiforgery(builder.Environment);

builder.AddAppTelemetry();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(tags: ["ready"]);

builder.Services.AddAppForwardedHeaders(builder.Configuration);

var app = builder.Build();

// `dotnet WebhookInspector.Api.dll cleanup`: run the retention cleanup once and exit. Used by
// the scheduled cleanup job, which runs the same image with this argument (ADR 0009).
if (args is ["cleanup", ..])
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider.GetRequiredService<RetentionCleanup>().RunAsync(CancellationToken.None);
    }

    // Disposing the app flushes telemetry, so the run shows up in Application Insights.
    await app.DisposeAsync();
    return;
}

app.UseSecurityHeaders();
app.UseSpaStaticFiles();
app.UseRateLimiter();

// The capture route is a separate pipeline (PLAN.md section 2): it never reads cookies,
// never needs antiforgery, and its errors are handled inside the route so that request
// data cannot end up in exception logs. Everything else gets the full pipeline.
//
// WebApplication inserts UseAuthentication/UseAuthorization at the very start of the
// pipeline unless it sees them on the main builder, and it cannot see calls inside a
// UseWhen branch. Its copy would run before the branch check (reading the session cookie
// on /h) and handled the GitHub callback before anything else, which made the OAuth
// redirect_uri wrong in production. These two flags (the keys the framework checks) tell
// it that both are placed deliberately. AuthPipelineTests fail if that stops working.
var pipeline = (IApplicationBuilder)app;
pipeline.Properties["__AuthenticationMiddlewareSet"] = true;
pipeline.Properties["__AuthorizationMiddlewareSet"] = true;
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/h"),
    branch =>
    {
        branch.UseExceptionHandler();
        branch.UseAuthentication();
        branch.UseAuthorization();
        branch.UseMiddleware<AntiforgeryValidationMiddleware>();
    });

app.MapCapture();
app.MapAuth();
app.MapTestSignIn();
app.MapApi();
app.MapHub<RequestsHub>(RequestsHub.Path);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapSpaFallback();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
