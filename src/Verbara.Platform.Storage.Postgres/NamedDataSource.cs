using Npgsql;

namespace Verbara.Platform.Storage.Postgres;

/// <summary>
/// Builds an <see cref="NpgsqlDataSource"/> that carries an explicit diagnostics name.
/// </summary>
/// <remarks>
/// Npgsql labels every pool and operation instrument of a data source (meter <c>Npgsql</c>) with
/// <c>db.client.connection.pool.name</c>, taken from the data source's <c>Name</c>. Without one it falls
/// back to the connection string minus the password — host, port, database and user — which the Api would
/// then publish on its unauthenticated <c>/metrics</c>. The composition root builds every pool it shares
/// through this helper, so each pool series carries a fixed name instead.
/// </remarks>
public static class NamedDataSource
{
    /// <summary>Builds a data source for <paramref name="connectionString"/> named <paramref name="name"/>.</summary>
    /// <param name="connectionString">The Npgsql connection string.</param>
    /// <param name="name">
    /// The fixed diagnostics name, e.g. <c>core</c> or <c>dialer</c>. Never connection-string material.
    /// </param>
    public static NpgsqlDataSource Build(string connectionString, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new NpgsqlDataSourceBuilder(connectionString) { Name = name }.Build();
    }
}
