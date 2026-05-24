using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WebhookInspector.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<WebhookEndpoint> Endpoints => Set<WebhookEndpoint>();

    public DbSet<CapturedRequest> CapturedRequests => Set<CapturedRequest>();

    public DbSet<ReplayAttempt> ReplayAttempts => Set<ReplayAttempt>();

    /// <summary>
    /// ASP.NET Core Data Protection keys, so auth and antiforgery cookies stay valid across
    /// restarts and deploys. Not encrypted at the application level in v1 (see PLAN.md section 7).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    /// <summary>
    /// Maps to Postgres <c>substring(bytea, start, count)</c>, so list queries can return a
    /// short body preview without loading whole bodies. Only usable inside LINQ queries.
    /// </summary>
    public static byte[] BytesPrefix(byte[] bytes, int start, int count) =>
        throw new NotSupportedException("Only for use in database queries.");

    private static readonly ValueConverter<List<HeaderEntry>, string> HeaderListConverter = new(
        headers => JsonSerializer.Serialize(headers, JsonSerializerOptions.Web),
        json => JsonSerializer.Deserialize<List<HeaderEntry>>(json, JsonSerializerOptions.Web) ?? new List<HeaderEntry>());

    private static readonly ValueComparer<List<HeaderEntry>> HeaderListComparer = new(
        (a, b) => a!.SequenceEqual(b!),
        list => list.Aggregate(0, (hash, h) => HashCode.Combine(hash, h.GetHashCode())),
        list => list.ToList());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(AppDbContext).GetMethod(nameof(BytesPrefix))!)
            .HasName("substring")
            .IsBuiltIn();

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
                .HasConversion(HeaderListConverter, HeaderListComparer);
        });

        modelBuilder.Entity<ReplayAttempt>(attempt =>
        {
            attempt.ToTable("ReplayAttempt");

            // Deleted with the captured request, so retention cleanup covers replays too.
            attempt.HasOne(a => a.CapturedRequest)
                .WithMany()
                .HasForeignKey(a => a.CapturedRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            attempt.HasIndex(a => new { a.CapturedRequestId, a.StartedAt, a.Id })
                .IsDescending(false, true, true);

            attempt.Property(a => a.ResponseHeaders)
                .HasColumnType("jsonb")
                .HasConversion(HeaderListConverter, HeaderListComparer);
        });
    }
}
