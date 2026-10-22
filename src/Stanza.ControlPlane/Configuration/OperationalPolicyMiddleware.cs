namespace Stanza.ControlPlane.Configuration;

public static class OperationalPolicyMiddleware
{
    public static OperationalPolicy RequestPolicy(HttpContext context) => context.Items.TryGetValue("stanza-policy", out var value) && value is OperationalPolicy policy ? policy : new();
    public static IApplicationBuilder UseOperationalPolicy(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/setup") || context.Request.Path.StartsWithSegments("/api/agent-enrollment"))
            context.Items["stanza-policy"] = (await context.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.RequestAborted)).Policy;
        await next();
    });
}
