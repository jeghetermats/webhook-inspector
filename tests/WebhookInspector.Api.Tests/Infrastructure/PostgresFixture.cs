using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Tests.Infrastructure;

/// <summary>
/// One real Postgres container for the whole test run, migrated with the app's own
/// migrations. Tests isolate themselves by creating their own users and endpoints.
///
/// Like production, the app under test connects as the least-privilege role (ADR 0014), so
/// every API test also checks that the role's grants are enough. Seeding and assertions use
/// the administrator connection.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppRolePassword = "test-app-role-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Administrator connection: migrations, seeding and checks.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>The connection the app uses, as the least-privilege role.</summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = AppDatabaseRole.DefaultName,
        Password = AppRolePassword,
        // Postgres allows 100 connections and the administrator pool needs some of them too.
        // Production caps the app's pool at 20 for the same reason.
        MaxPoolSize = 40,
    }.ConnectionString;

    /// <summary>Shared app instance with rate limits high enough not to interfere.</summary>
    public ApiFactory Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await AppDatabaseRole.EnsureAsync(ConnectionString, AppDatabaseRole.DefaultName, AppRolePassword);

        Api = CreateApi();
    }

    public ApiFactory CreateApi(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(AppConnectionString, settings);

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
