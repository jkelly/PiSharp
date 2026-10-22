// Decision 0011 (run W2, WU4): how an area adds tables to webagent.db. Each area registers its migrations in DI with ids from its own range,
// so units never edit a shared list and never collide: core 1-99, accounts 100-199, chats 200-299, settings 300-399, spend 1000-1999 (W3),
// evals 2000-2999 (W5). WebAgentDb applies every registered id it has not recorded yet, lowest first, each in its own transaction.
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace PiSharp.WebAgent.Data;

/// <summary>One schema step of webagent.db. Register it with <see cref="WebAgentMigrationExtensions.AddWebAgentMigration(IServiceCollection, IWebAgentMigration)"/>.</summary>
public interface IWebAgentMigration
{
    /// <summary>The migration's id, unique, inside its area's range (<see cref="MigrationArea.All"/>). It never changes once shipped.</summary>
    int Id { get; }

    /// <summary>A short description, recorded in <c>schema_migrations</c> (for example <c>"chats: bindings"</c>).</summary>
    string Name { get; }

    /// <summary>Applies the step inside <paramref name="transaction"/>. It runs once per database; the transaction commits it together with
    /// its <c>schema_migrations</c> row, so a failure leaves nothing behind and the next start tries again.</summary>
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}

/// <summary>A migration that runs one SQL script (one or more statements).</summary>
/// <param name="Id">The id, inside its area's range.</param>
/// <param name="Name">A short description.</param>
/// <param name="Sql">The script.</param>
public sealed record SqlMigration(int Id, string Name, string Sql) : IWebAgentMigration
{
    /// <inheritdoc/>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>A range of migration ids that belongs to one area.</summary>
/// <param name="Name">The area.</param>
/// <param name="First">The lowest id.</param>
/// <param name="Last">The highest id.</param>
public sealed record MigrationArea(string Name, int First, int Last)
{
    /// <summary>Core (hosting and the shell).</summary>
    public static MigrationArea Core { get; } = new("core", 1, 99);

    /// <summary>Accounts and active accounts (Credentials).</summary>
    public static MigrationArea Accounts { get; } = new("accounts", 100, 199);

    /// <summary>Chat bindings (Chat).</summary>
    public static MigrationArea Chats { get; } = new("chats", 200, 299);

    /// <summary>Settings sections (<see cref="Contracts.ISettingsStore"/>, Settings).</summary>
    public static MigrationArea Settings { get; } = new("settings", 300, 399);

    /// <summary>The Spend Guard ledger and caps (W3).</summary>
    public static MigrationArea Spend { get; } = new("spend", 1000, 1999);

    /// <summary>Arena votes, test cases, suites and drift (W5).</summary>
    public static MigrationArea Evals { get; } = new("evals", 2000, 2999);

    /// <summary>Every area, in id order.</summary>
    public static IReadOnlyList<MigrationArea> All { get; } = [Core, Accounts, Chats, Settings, Spend, Evals];

    /// <summary>The area <paramref name="id"/> belongs to, or null when it is in none.</summary>
    public static MigrationArea? Of(int id) => All.FirstOrDefault(area => id >= area.First && id <= area.Last);
}

/// <summary>Registration of webagent.db migrations.</summary>
public static class WebAgentMigrationExtensions
{
    /// <summary>Registers <paramref name="migration"/>; <see cref="WebAgentDb"/> applies it at start.</summary>
    public static IServiceCollection AddWebAgentMigration(this IServiceCollection services, IWebAgentMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        services.AddSingleton(migration);
        return services;
    }

    /// <summary>Registers a <see cref="SqlMigration"/>.</summary>
    public static IServiceCollection AddWebAgentMigration(this IServiceCollection services, int id, string name, string sql) =>
        services.AddWebAgentMigration(new SqlMigration(id, name, sql));
}
