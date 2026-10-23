using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Transport;
using Stanza.ControlPlane.Recovery;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Terminals;
using Stanza.ControlPlane.Logs;

namespace Stanza.ControlPlane;

public sealed class ControlPlaneEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var httpTransport = new HttpTransportPolicy(builder.Configuration.GetValue<bool>("Stanza:AllowInsecureHttp"));
        builder.Services.AddSingleton(httpTransport);
        builder.Services.AddStanzaSecurity(provider => DatabaseConfiguration.GetConnectionString(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddScoped<InstanceSettingsService>();
        builder.Services.AddScoped<AgentAdministrationService>();
        builder.Services.AddScoped<DeploymentDraftService>();
        builder.Services.AddScoped<OperationalPolicyService>();
        builder.Services.AddScoped<AgentPolicyService>();
        builder.Services.AddSingleton<InstanceMaintenance>();
        builder.Services.AddScoped<IRecoveryAuditSink, RecoveryAuditAdapter>();
        builder.Services.AddStanzaRecovery();
        builder.Services.AddScoped<IImageDigestProbe, AgentImageDigestProbe>();
        builder.Services.AddStanzaAutomation();
        builder.Services.AddStanzaPortability();
        builder.Services.AddStanzaTerminals();
        builder.Services.AddStanzaAgentTerminalBridge();
        builder.Services.AddStanzaLogs();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<SourceService>();
        builder.Services.AddScoped<ISourceSnapshotProvider>(provider => provider.GetRequiredService<SourceService>());
        builder.Services.AddScoped<DeploymentEditorAdapters>();
        builder.Services.AddScoped<IDeploymentEditorLimitsProvider>(provider => provider.GetRequiredService<DeploymentEditorAdapters>());
        builder.Services.AddScoped<IDeploymentWorkspaceReservations>(provider => provider.GetRequiredService<DeploymentEditorAdapters>());
        builder.Services.AddScoped<IDeploymentRevisionStatusProvider>(provider => provider.GetRequiredService<DeploymentEditorAdapters>());
        builder.Services.AddScoped<OperationService>();
        builder.Services.AddScoped<DeploymentRuntimeStatusService>();
        builder.Services.AddScoped<IOperationService>(provider => provider.GetRequiredService<OperationService>());
        builder.Services.AddStanzaAgentTransport();
        builder.Services.AddHostedService<OperationWorker>();
        builder.Services.AddStanzaLiveStatus();
        builder.Services.AddDataProtection().SetApplicationName("Stanza.ControlPlane");
        builder.Services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, ILoggerFactory>((options, configuration, logging) =>
        {
            var directory = new DirectoryInfo(Path.Combine(Path.GetFullPath(configuration["Stanza:DataRoot"] ?? "data/control-plane"), "keys"));
            directory.Create();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.XmlRepository = new FileSystemXmlRepository(directory, logging);
        });
        builder.Services.AddControllersWithViews(options =>
        {
            options.Filters.Add(new AuthorizeFilter());
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "Stanza.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Secure by default even behind HTTP proxy backends. An explicit bootstrap opt-in
                // allows LAN HTTP; SameAsRequest still protects cookies on recognized HTTPS requests.
                // TestServer's existing logical-HTTP exception is isolated to the Testing environment.
                options.Cookie.SecurePolicy = httpTransport.AllowInsecureHttp || builder.Environment.IsEnvironment("Testing")
                    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.LoginPath = "/account/login";
                options.AccessDeniedPath = "/account/denied";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events.OnSigningIn = async context =>
                {
                    var policy = (await context.HttpContext.RequestServices.GetRequiredService<OperationalPolicyService>().GetAsync(context.HttpContext.RequestAborted)).Policy;
                    context.Properties.ExpiresUtc = (context.Properties.IssuedUtc ?? TimeProvider.System.GetUtcNow()).AddHours(policy.SessionHours);
                };
                options.Events.OnValidatePrincipal = async context =>
                {
                    var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountService>();
                    var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var stamp = context.Principal?.FindFirstValue("security_stamp");
                    var account = Guid.TryParse(id, out var parsed) ? await accounts.FindAsync(parsed, context.HttpContext.RequestAborted) : null;
                    if (account is null || !account.Enabled || account.SecurityStamp != stamp)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("enrollment", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    (context.Connection.RemoteIpAddress?.ToString() ?? "unknown") + ":" + OperationalPolicyMiddleware.RequestPolicy(context).EnrollmentRequestsPerMinute,
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = OperationalPolicyMiddleware.RequestPolicy(context).EnrollmentRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                    }));
            options.AddPolicy("authentication", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    (context.Connection.RemoteIpAddress?.ToString() ?? "unknown") + ":" + OperationalPolicyMiddleware.RequestPolicy(context).LoginAttempts + ":" + OperationalPolicyMiddleware.RequestPolicy(context).LoginWindowMinutes,
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = OperationalPolicyMiddleware.RequestPolicy(context).LoginAttempts,
                        Window = TimeSpan.FromMinutes(OperationalPolicyMiddleware.RequestPolicy(context).LoginWindowMinutes),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
        var app = builder.Build();
        // Fresh-schema initialization only: this application intentionally has no migrations.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            await database.Database.EnsureCreatedAsync();
        }
        app.UseExceptionHandler("/error");
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next();
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.UseStanzaForwardedHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseOperationalPolicy();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.RequestServices.GetRequiredService<InstanceMaintenance>().RestartRequired && !context.Request.Path.StartsWithSegments("/recovery"))
            { context.Response.StatusCode = 503; await context.Response.WriteAsync("Recovery completed. Restart the Control Plane before using restored data."); return; }
            await next();
        });
        app.UseStanzaAudit();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", component = "control-plane" })).AllowAnonymous();
        app.MapGet("/health/ready", async (StanzaDbContext database, CancellationToken token) =>
            await database.Database.CanConnectAsync(token)
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
        app.MapControllers();
        app.MapStanzaLiveStatus();
        app.MapStanzaAgentTransport();
        app.MapStanzaTerminals();
        app.MapStanzaLogs();
        app.MapStanzaOperationLive();
        app.MapStanzaActionLive();
        await app.RunAsync();
    }
}
