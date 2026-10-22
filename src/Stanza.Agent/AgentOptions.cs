using Microsoft.Extensions.Options;

namespace Stanza.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public string PersistentDataRoot { get; set; } = "/var/lib/stanza-agent";
    public string ControlPlaneUrl { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public bool AllowLoopbackHttp { get; set; }
}

public sealed class AgentOptionsValidator(IHostEnvironment environment) : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var failures = new List<string>();
        if (!IsPersistentDirectory(options.PersistentDataRoot))
        {
            failures.Add("Agent:PersistentDataRoot must be an absolute, dedicated persistent directory, not a filesystem root.");
        }

        if (!Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback &&
                options.AllowLoopbackHttp && environment.IsDevelopment())))
        {
            failures.Add("Agent:ControlPlaneUrl must be an HTTPS origin; explicit loopback HTTP is permitted only in Development.");
        }

        if (string.IsNullOrWhiteSpace(options.EnrollmentToken) || options.EnrollmentToken.Length is < 32 or > 4096 ||
            options.EnrollmentToken.Any(char.IsControl))
        {
            failures.Add("Agent:EnrollmentToken must contain 32 to 4096 characters and no control characters.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsPersistentDirectory(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                Path.GetFullPath(path) != Path.GetPathRoot(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
