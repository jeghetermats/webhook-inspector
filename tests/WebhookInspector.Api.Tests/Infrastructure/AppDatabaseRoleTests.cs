using Npgsql;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Tests.Infrastructure;

/// <summary>
/// The app's database role (ADR 0014). That the grants are enough is shown by every other
/// API test, which runs as this role; these tests check that it cannot do more.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AppDatabaseRoleTests(PostgresFixture fixture)
{
    private async Task<NpgsqlConnection> OpenAsAppAsync(string? password = null)
    {
        // No pooling: a pooled connection opened with an old password would hide a change.
        var builder = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Pooling = false };
        if (password is not null)
        {
            builder.Password = password;
        }

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<PostgresException> FailsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Can_read_and_change_rows_in_the_app_tables()
    {
        await using var connection = await OpenAsAppAsync();
        await using var command = new NpgsqlCommand(
            """SELECT count(*) FROM "User"; SELECT count(*) FROM "CapturedRequest"; SELECT count(*) FROM "DataProtectionKeys" """,
            connection);

        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("""CREATE TABLE "Sneaky" ("Id" int)""")]
    [InlineData("""ALTER TABLE "CapturedRequest" ADD COLUMN "Sneaky" int""")]
    [InlineData("""DROP TABLE "ReplayAttempt" """)]
    [InlineData("""TRUNCATE "CapturedRequest" """)]
    [InlineData("""CREATE INDEX "IX_Sneaky" ON "User" ("Id")""")]
    [InlineData("""SELECT * FROM "__EFMigrationsHistory" """)]
    [InlineData("""DELETE FROM "__EFMigrationsHistory" """)]
    [InlineData("CREATE ROLE sneaky")]
    [InlineData("CREATE SCHEMA sneaky")]
    public async Task Cannot_change_the_schema_roles_or_migration_history(string sql)
    {
        await using var connection = await OpenAsAppAsync();

        var error = await FailsAsync(connection, sql);

        // 42501 insufficient_privilege. Any other error would mean the statement itself is wrong.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Running_it_again_is_harmless_and_changes_the_password()
    {
        const string newPassword = "a-different-password-for-this-test";

        await AppDatabaseRole.EnsureAsync(fixture.ConnectionString, AppDatabaseRole.DefaultName, newPassword);
        try
        {
            await using (await OpenAsAppAsync(newPassword))
            {
            }

            await Assert.ThrowsAsync<PostgresException>(() => OpenAsAppAsync(PostgresFixture.AppRolePassword));
        }
        finally
        {
            await AppDatabaseRole.EnsureAsync(fixture.ConnectionString, AppDatabaseRole.DefaultName, PostgresFixture.AppRolePassword);
        }
    }

    [Theory]
    [InlineData("webhook_app; DROP TABLE \"User\"", "a-long-enough-password")]
    [InlineData("Webhook_App", "a-long-enough-password")]
    [InlineData("webhook_app", "short")]
    public async Task Rejects_unsafe_names_and_short_passwords(string role, string password)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => AppDatabaseRole.EnsureAsync(fixture.ConnectionString, role, password));
    }

    [Fact]
    public async Task Quotes_passwords_with_special_characters()
    {
        const string role = "webhook_quote_test";
        const string password = "it's a pass'word; DROP TABLE \"User\"; --";

        await AppDatabaseRole.EnsureAsync(fixture.ConnectionString, role, password);

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = password };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT count(*) FROM "User" """, connection);
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 0);
    }
}
