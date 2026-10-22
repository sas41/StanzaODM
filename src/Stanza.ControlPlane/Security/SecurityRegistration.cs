using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stanza.ControlPlane.Security;

public static class SecurityRegistration
{
    public static IServiceCollection AddStanzaSecurity(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddStanzaSecurity(_ => connectionString);
    }

    public static IServiceCollection AddStanzaSecurity(this IServiceCollection services, Func<IServiceProvider, string> connectionStringFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        services.AddDbContext<StanzaDbContext>((provider, options) => options.UseNpgsql(connectionStringFactory(provider)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AccountService>();
        services.AddScoped<AccessService>();
        return services;
    }

    public static async Task InitializeStanzaSecurityAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StanzaDbContext>().Database.EnsureCreatedAsync(cancellationToken);
    }

    public static Task<bool> IsStanzaSecurityReadyAsync(this StanzaDbContext context,
        CancellationToken cancellationToken = default) => context.Database.CanConnectAsync(cancellationToken);
}
