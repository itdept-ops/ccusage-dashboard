using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL container (Testcontainers).
///
/// Program.cs reads its config (connection string, JWT key) <em>eagerly</em>, before the host
/// is built, so a <c>ConfigureAppConfiguration</c> override would lose to the eager reads and to
/// appsettings.Local.json. Instead we set real environment variables here (CreateBuilder folds
/// them in first) and flip SkipLocalSettings so the local secrets file is never loaded. Migrations
/// and the admin seed run on startup, so the tests exercise the genuine auth/permission pipeline.
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Key = "usage-iq-integration-test-signing-key-32-bytes-min!";
    public const string AdminEmail = "admin@test.local";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private static readonly string[] OwnedVars =
    {
        "SkipLocalSettings", "ConnectionStrings__Default", "Jwt__Key", "Jwt__Issuer",
        "Jwt__Audience", "Jwt__ExpiryMinutes", "Google__ClientId", "Auth__AdminEmails__0",
        "AutoSync__Enabled",
    };

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        // Set before the host first builds (lazy, on first CreateClient) so the eager config
        // reads in Program.cs pick these up.
        Environment.SetEnvironmentVariable("SkipLocalSettings", "true");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__Key", Key);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "usage-iq");
        Environment.SetEnvironmentVariable("Jwt__Audience", "usage-iq");
        Environment.SetEnvironmentVariable("Jwt__ExpiryMinutes", "60");
        Environment.SetEnvironmentVariable("Google__ClientId", "test-client-id.apps.googleusercontent.com");
        Environment.SetEnvironmentVariable("Auth__AdminEmails__0", AdminEmail);
        Environment.SetEnvironmentVariable("AutoSync__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var v in OwnedVars)
            Environment.SetEnvironmentVariable(v, null);
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }
}
