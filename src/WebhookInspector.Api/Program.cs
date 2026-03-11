using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Data;
using WebhookInspector.Api.Security;

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
builder.Services.AddCaptureRateLimiting();

builder.Services.AddAppAuthentication(builder.Configuration);
builder.Services.AddAppAntiforgery();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(tags: ["ready"]);

// Behind the Azure Container Apps ingress the client IP arrives in X-Forwarded-For.
// ForwardLimit = 1 takes only the entry appended by the ingress, so a client cannot
// spoof its IP by sending its own X-Forwarded-For header.
var useForwardedHeaders = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (useForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (useForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseSecurityHeaders();
app.UseRateLimiter();

// The capture route is a separate pipeline (PLAN.md section 2): it never reads cookies,
// never needs antiforgery, and its errors are handled inside the route so that request
// data cannot end up in exception logs. Everything else gets the full pipeline.
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
app.MapApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
