using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Tests.Infrastructure;

/// <summary>
/// One real Postgres container for the whole test run, migrated with the app's own
/// migrations. Tests isolate themselves by creating their own users and endpoints.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Shared app instance with rate limits high enough not to interfere.</summary>
    public ApiFactory Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        Api = CreateApi();
    }

    public ApiFactory CreateApi(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(ConnectionString, settings);

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
