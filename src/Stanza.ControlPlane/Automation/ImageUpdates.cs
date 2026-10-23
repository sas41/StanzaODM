using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stanza.ControlPlane.Automation;

/// <summary>Read-only authenticated Agent inspection of an immutable applied deployment snapshot.
/// CurrentDigest and RemoteDigest are comparable image config digests, not manifest/index digests.
/// Registry manifest identities remain separate Agent response metadata. No registry calls run here.</summary>
public interface IImageDigestProbe
{
    Task<IReadOnlyList<ImageDigestObservation>> ProbeAsync(Guid actorId, Guid deploymentId, IReadOnlyList<string> services, CancellationToken ct = default);
}
public sealed record ImageDigestObservation(string Service, string Image, string Platform, string? CurrentDigest,
    string RemoteDigest, bool LocallyBuilt = false, bool ExplicitRegistrySource = false);
public sealed record ImagePolicySnapshot(Guid OwnerId, Guid DeploymentId, string Services, string Mode, bool Enabled)
{
    public static ImagePolicySnapshot From(ImageUpdatePolicy policy) => new(policy.OwnerId, policy.DeploymentId, policy.Services, policy.Mode, policy.Enabled);
}

public sealed class UnavailableImageDigestProbe : IImageDigestProbe
{
    public Task<IReadOnlyList<ImageDigestObservation>> ProbeAsync(Guid actorId, Guid deploymentId, IReadOnlyList<string> services, CancellationToken ct = default) =>
        throw new AutomationException("image-probe-unavailable");
}

public static partial class ImageUpdates
{
    public static string ProbeFailureCode(Exception error) => error switch
    {
        AutomationException { Message: "image-probe-not-deployed" or "image-probe-deployment-unavailable" or
            "image-probe-pending-manual-changes" or "image-probe-pending-placement-change" or "image-probe-pending-environment-changes" or
            "image-probe-pending-source-changes" or "image-probe-deployment-operation-pending" or "image-probe-applied-state-changed" or
            "image-probe-applied-snapshot-unavailable" or "image-probe-applied-snapshot-invalid" or "image-probe-applied-commit-unavailable" or
            "image-probe-agent-environment-unavailable" or "image-probe-agent-failed" or "image-probe-invalid-response" or
            "image-probe-invalid-services" or "image-probe-execution-disabled" } known => known.Message,
        Stanza.ControlPlane.Transport.AgentUnavailableException => "image-probe-agent-offline",
        AutomationAccessException or Stanza.ControlPlane.Sources.SourceAccessException => "image-probe-access-denied",
        OperationCanceledException => "image-probe-timeout",
        _ => "probe-failed-or-unavailable"
    };
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex ServicePattern();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex DigestPattern();
    public static IReadOnlyList<string> ServiceNames(string services)
    {
        if (services is null || services.Length > 8192) throw new AutomationException("Select a bounded service list.");
        var names = services.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 100 || names.Any(n => !ServicePattern().IsMatch(n))) throw new AutomationException("Invalid service names.");
        return names;
    }

    public static IReadOnlyList<ImageDigestObservation> Changed(IReadOnlyList<ImageDigestObservation> observations, IReadOnlyList<string> services)
    {
        if (observations.Count > 100 || observations.Any(o => !services.Contains(o.Service, StringComparer.Ordinal))
            || observations.Select(o => o.Service).Distinct(StringComparer.Ordinal).Count() != observations.Count
            || services.Any(s => observations.All(o => o.Service != s))) throw new AutomationException("image-probe-incomplete");
        var changed = new List<ImageDigestObservation>();
        foreach (var item in observations)
        {
            if (item.LocallyBuilt && !item.ExplicitRegistrySource) continue;
            if (string.IsNullOrWhiteSpace(item.Image) || item.Image.Length > 1024 || string.IsNullOrWhiteSpace(item.Platform) || item.Platform.Length > 100
                || item.RemoteDigest is null || !DigestPattern().IsMatch(item.RemoteDigest) || (item.CurrentDigest is not null && !DigestPattern().IsMatch(item.CurrentDigest)))
                throw new AutomationException("image-probe-invalid-digest-or-platform");
            // Missing local identity is not evidence of an available update: never auto-deploy an unknown baseline.
            if (item.CurrentDigest is not null && item.CurrentDigest != item.RemoteDigest) changed.Add(item);
        }
        return changed;
    }
}

public static class AutomationRegistration
{
    public static IServiceCollection AddStanzaAutomation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache(); services.AddScoped<ActionInputService>(); services.AddScoped<DeploymentActionOverview>();
        services.AddScoped<AutomationAuthorization>(); services.AddScoped<AutomationService>();
        services.AddScoped<WebhookReceiver>(); services.AddScoped<AutomationScheduler>(); services.AddScoped<AutomationExecutor>();
        services.AddScoped<AutomationOperationGuard>();
        services.TryAddScoped<IImageDigestProbe, UnavailableImageDigestProbe>();
        services.AddHostedService<AutomationWorker>(); return services;
    }
}
