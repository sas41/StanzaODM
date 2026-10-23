using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Stanza.ControlPlane.Configuration;

public static class TrustedProxyPolicy
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        text ??= "";
        if (text.Length > 4096) throw new SettingsOperationException("The trusted proxy list is too long.");
        var entries = text.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var entry in entries)
            if (!IPAddress.TryParse(entry, out _) && (!System.Net.IPNetwork.TryParse(entry, out var network) || network.PrefixLength == 0))
                throw new SettingsOperationException("Trusted proxies must be explicit IP addresses or bounded CIDRs, never a trust-all network.");
        return entries;
    }

    public static IApplicationBuilder UseStanzaForwardedHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Headers.ContainsKey("X-Forwarded-Proto") && !context.Request.Headers.ContainsKey("X-Forwarded-For")) { await next(); return; }
        var policy = (await context.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.RequestAborted)).Policy;
        var entries = Parse(policy.TrustedProxyAddresses);
        if (entries.Count == 0) { await next(); return; }
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = policy.TrustedProxyForwardLimit == 0 ? null : policy.TrustedProxyForwardLimit };
        options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
        foreach (var entry in entries)
            if (IPAddress.TryParse(entry, out var ip)) options.KnownProxies.Add(ip); else options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
        var middleware = new ForwardedHeadersMiddleware(_ => next(), context.RequestServices.GetRequiredService<ILoggerFactory>(), Options.Create(options));
        await middleware.Invoke(context);
    });
}
