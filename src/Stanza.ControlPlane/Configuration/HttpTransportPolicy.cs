namespace Stanza.ControlPlane.Configuration;

// Bootstrap-only opt-in: a browser request or configuration import cannot weaken transport security.
public sealed record HttpTransportPolicy(bool AllowInsecureHttp = false)
{
    public bool AllowsAgentRequest(HttpContext context, IHostEnvironment environment) =>
        context.Request.IsHttps || AllowInsecureHttp ||
        environment.IsDevelopment() && context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);
}
