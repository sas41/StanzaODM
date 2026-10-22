using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Terminals;
using Stanza.Core.Contracts;
using Stanza.Core.Security;
using Stanza.Tests.Live;

namespace Stanza.Tests.Terminals;

internal sealed class TerminalTestServer : IAsyncDisposable
{
    private readonly string directory = Path.Combine("/tmp/opencode/stanza-terminal-artifacts", "http-" + Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public LiveTestClock Clock { get; } = new();
    public FakeTerminalTransport Transport { get; } = new();
    public Guid Owner { get; private set; }
    public Guid Agent { get; } = Guid.NewGuid();
    public Guid Deployment { get; } = Guid.NewGuid();
    public string Origin => App.Urls.Single();

    public static async Task<TerminalTestServer> CreateAsync()
    {
        var fixture = new TerminalTestServer();
        Directory.CreateDirectory(fixture.directory);
        var sourceRoot = new DirectoryInfo(SourceDirectory());
        while (!File.Exists(Path.Combine(sourceRoot.FullName, "Stanza.slnx"))) sourceRoot = sourceRoot.Parent!;
        var contentRoot = Path.Combine(sourceRoot.FullName, "src/Stanza.ControlPlane");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = contentRoot });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<StanzaDbContext>(options => options.UseSqlite($"Data Source={fixture.directory}/test.db"));
        builder.Services.AddScoped<AccountService>();
        builder.Services.AddScoped<AccessService>();
        builder.Services.AddScoped<InstanceSettingsService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        builder.Services.AddStanzaTerminals();
        builder.Services.AddSingleton<IAgentTerminalTransport>(fixture.Transport);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => options.TimeProvider = fixture.Clock);
        builder.Services.AddAuthorization();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(Stanza.ControlPlane.Controllers.TerminalsController).Assembly);
        fixture.App = builder.Build();
        fixture.App.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next();
        });
        fixture.App.UseStaticFiles();
        fixture.App.UseWebSockets();
        fixture.App.UseAuthentication();
        fixture.App.UseAuthorization();
        fixture.App.MapStanzaTerminals();
        fixture.App.MapControllers();
        // Test-only login issues the actual framework cookie; production gateway auth is unchanged.
        fixture.App.MapGet("/login/{actor:guid}", async (Microsoft.AspNetCore.Http.HttpContext context, Guid actor, AccountService accounts) =>
        {
            var account = (await accounts.FindAsync(actor))!;
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                    new Claim("security_stamp", account.SecurityStamp)], CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { ExpiresUtc = fixture.Clock.GetUtcNow().AddHours(8) });
        }).AllowAnonymous();
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
            await db.Database.EnsureCreatedAsync();
            fixture.Owner = (await scope.ServiceProvider.GetRequiredService<AccountService>().BootstrapAsync("owner", "a-secure-password-123")).Id;
            db.Set<AgentEntity>().Add(new() { Id = fixture.Agent, Status = "approved", Identity = "test-agent", PublicKey = "key", Hostname = "host" });
            db.Set<DeploymentDraftEntity>().Add(new() { Id = fixture.Deployment, Name = "test-deployment", TargetAgentId = fixture.Agent, Revision = 2 });
            db.Set<DeploymentRuntimeEntity>().Add(new() { DeploymentId = fixture.Deployment, AgentId = fixture.Agent, Revision = 1, ObservedState = "running" });
            await db.SaveChangesAsync();
        }
        await fixture.App.StartAsync();
        return fixture;
    }

    public async Task<string> CookieAsync(Guid? actor = null)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await client.GetAsync($"{Origin}/login/{actor ?? Owner}");
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    public async Task<ClientWebSocket> ConnectAsync(string? cookie, string? origin = null, Guid? deployment = null)
    {
        var socket = new ClientWebSocket();
        if (cookie is not null) socket.Options.SetRequestHeader("Cookie", cookie);
        if (origin != "missing") socket.Options.SetRequestHeader("Origin", origin ?? Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await socket.ConnectAsync(new Uri(Origin.Replace("http:", "ws:") + $"/live/terminal/{deployment ?? Deployment}"), timeout.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    public async Task<ClientWebSocket> OpenAsync(string? cookie = null)
    {
        var socket = await ConnectAsync(cookie ?? await CookieAsync());
        await SendAsync(socket, TerminalProtocolTests.Open);
        Assert.Equal("ready", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        return socket;
    }

    public static async Task SendAsync(WebSocket socket, object message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)), WebSocketMessageType.Text, true, timeout.Token);
    }

    public static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[TerminalProtocol.MaximumFrameBytes];
        var count = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            count += result.Count;
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
        return document.RootElement.Clone();
    }

    public async Task MutateAsync(Func<StanzaDbContext, Task> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StanzaDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Directory.Delete(directory, true);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

internal sealed class FakeTerminalTransport : IAgentTerminalTransport
{
    public FakeTerminalSession Session { get; } = new();
    public List<(Guid Agent, Guid Deployment, AgentExecution Execution)> Opens { get; } = [];
    public bool Unavailable { get; set; }
    public Task<IAgentTerminalSession> OpenAsync(Guid agentId, Guid deploymentId, AgentExecution execution, CancellationToken ct)
    {
        if (Unavailable) throw new InvalidOperationException("private-transport-error");
        Opens.Add((agentId, deploymentId, execution));
        return Task.FromResult<IAgentTerminalSession>(Session);
    }
}

internal sealed class FakeTerminalSession : IAgentTerminalSession
{
    public Channel<TerminalOutput> Output { get; } = Channel.CreateBounded<TerminalOutput>(2);
    public Channel<byte[]> Input { get; } = Channel.CreateUnbounded<byte[]>();
    public Channel<(int Columns, int Rows)> Resizes { get; } = Channel.CreateUnbounded<(int, int)>();
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Closed { get; private set; }
    public ValueTask<TerminalOutput> ReadOutputAsync(CancellationToken ct) => Output.Reader.ReadAsync(ct);
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken ct) => Input.Writer.WriteAsync(input.ToArray(), ct);
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct) => Resizes.Writer.WriteAsync((columns, rows), ct);
    public ValueTask CloseAsync(CancellationToken ct) { Closed = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
}
