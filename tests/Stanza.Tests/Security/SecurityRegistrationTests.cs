using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Security;

public sealed class SecurityRegistrationTests
{
    [Fact]
    public async Task RegistrationUsesNpgsqlAndDoesNotReplaceAnExistingClock()
    {
        var services = new ServiceCollection();
        var clock = new TestClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddStanzaSecurity("Host=localhost;Database=stanza;Username=stanza;Password=not-used");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        Assert.Same(clock, scope.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", scope.ServiceProvider.GetRequiredService<StanzaDbContext>().Database.ProviderName);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccountService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccessService>());
    }

    [Fact]
    public async Task RegistrationDefaultsToSystemClockAndRejectsBlankConnectionString()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddStanzaSecurity(" "));
        services.AddStanzaSecurity("Host=localhost;Database=stanza");
        await using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task InitializerCreatesFreshSchemaAndIsIdempotentWithoutSeedingCredentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<StanzaDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        await provider.InitializeStanzaSecurityAsync();
        await provider.InitializeStanzaSecurityAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
        Assert.True(await db.IsStanzaSecurityReadyAsync());
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.Groups.ToListAsync());
        Assert.Empty(await db.Settings.ToListAsync());
    }

    private sealed class TestClock : TimeProvider { }
}
