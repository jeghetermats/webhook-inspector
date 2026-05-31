using System.Text.RegularExpressions;
using Npgsql;

namespace WebhookInspector.Api.Data;

/// <summary>
/// Creates or updates the least-privilege Postgres role the app and the cleanup job connect
/// as (ADR 0014). Runs as the administrator in the migrate job, right after the migrations, so
/// the grants always cover the current tables.
///
/// The role may read and change rows in the app's tables and nothing else: it cannot create,
/// alter or drop tables, create roles, or touch the migration history.
/// </summary>
public static partial class AppDatabaseRole
{
    public const string DefaultName = "webhook_app";

    public static async Task EnsureAsync(
        string adminConnectionString, string roleName, string password, CancellationToken cancellationToken = default)
    {
        if (!RoleNamePattern().IsMatch(roleName))
        {
            throw new ArgumentException($"'{roleName}' is not a valid role name (lowercase letters, digits, _).", nameof(roleName));
        }

        if (string.IsNullOrEmpty(password) || password.Length < 16)
        {
            throw new ArgumentException("The app role password must be at least 16 characters.", nameof(password));
        }

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Statements are built with format(%I, %L) on the server, so the role name and password
        // are quoted by Postgres itself. DDL cannot take bind parameters directly.
        string[] templates =
        [
            // An existing role only gets a new password. A non-superuser administrator (Azure Postgres 16+)
            // is refused any ALTER ROLE that names NOSUPERUSER and the like, even to turn them off.
            // The strict attributes are set by CREATE ROLE below.
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)
                THEN format('ALTER ROLE %I WITH LOGIN PASSWORD %L', @role, @password)
                ELSE format('CREATE ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD %L', @role, @password)
            END
            """,
            "SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), @role)",
            "SELECT format('GRANT USAGE ON SCHEMA public TO %I', @role)",
            "SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', @role)",
            "SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', @role)",
            // The app never reads the migration history; only the migrate job (as admin) does.
            """SELECT format('REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM %I', @role)""",
        ];

        foreach (var template in templates)
        {
            string statement;
            await using (var build = new NpgsqlCommand(template, connection, transaction))
            {
                build.Parameters.AddWithValue("role", roleName);
                build.Parameters.AddWithValue("password", password);
                statement = (string)(await build.ExecuteScalarAsync(cancellationToken))!;
            }

            await using var run = new NpgsqlCommand(statement, connection, transaction);
            await run.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleNamePattern();
}
