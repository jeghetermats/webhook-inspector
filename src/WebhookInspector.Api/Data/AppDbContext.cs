using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace WebhookInspector.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<WebhookEndpoint> Endpoints => Set<WebhookEndpoint>();

    public DbSet<CapturedRequest> CapturedRequests => Set<CapturedRequest>();

    /// <summary>
    /// ASP.NET Core Data Protection keys, so auth and antiforgery cookies stay valid across
    /// restarts and deploys. Not encrypted at the application level in v1 (see PLAN.md section 7).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("User");
            user.HasIndex(u => u.GitHubId).IsUnique();
        });

        modelBuilder.Entity<WebhookEndpoint>(endpoint =>
        {
            endpoint.ToTable("Endpoint");
            endpoint.HasIndex(e => e.CaptureToken).IsUnique();
            endpoint.HasOne(e => e.Owner)
                .WithMany(u => u.Endpoints)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CapturedRequest>(request =>
        {
            request.ToTable("CapturedRequest");

            request.HasOne(r => r.Endpoint)
                .WithMany(e => e.Requests)
                .HasForeignKey(r => r.EndpointId)
                .OnDelete(DeleteBehavior.Cascade);

            // List and cursor pagination: newest first, ties broken by id.
            request.HasIndex(r => new { r.EndpointId, r.ReceivedAt, r.Id })
                .IsDescending(false, true, true);

            // Retention cleanup.
            request.HasIndex(r => r.ReceivedAt);

            request.Property(r => r.SourceIp).HasColumnType("inet");

            request.Property(r => r.Headers)
                .HasColumnType("jsonb")
                .HasConversion(
                    headers => JsonSerializer.Serialize(headers, JsonSerializerOptions.Web),
                    json => JsonSerializer.Deserialize<List<HeaderEntry>>(json, JsonSerializerOptions.Web) ?? new List<HeaderEntry>(),
                    new ValueComparer<List<HeaderEntry>>(
                        (a, b) => a!.SequenceEqual(b!),
                        list => list.Aggregate(0, (hash, h) => HashCode.Combine(hash, h.GetHashCode())),
                        list => list.ToList()));
        });
    }
}
