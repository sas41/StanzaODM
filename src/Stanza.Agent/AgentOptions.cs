using Microsoft.Extensions.Options;

namespace Stanza.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public string PersistentDataRoot { get; set; } = "/var/lib/stanza-agent";
    public string ControlPlaneUrl { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public string Name { get; set; } = "";
    // Friendly signed label only; never replace the Docker hostname used for self-mount discovery.
    public string RegistrationName => string.IsNullOrWhiteSpace(Name) ? System.Environment.MachineName : Name.Trim();
    public bool AllowLoopbackHttp { get; set; }
    public bool AllowInsecureHttp { get; set; }

    public bool AllowsControlPlaneOrigin(Uri origin, bool development = false) =>
        origin.IsAbsoluteUri && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.AbsolutePath == "/" &&
        (origin.Scheme == Uri.UriSchemeHttps || origin.Scheme == Uri.UriSchemeHttp &&
            (AllowInsecureHttp || development && AllowLoopbackHttp && origin.IsLoopback));
}

public sealed class AgentOptionsValidator(IHostEnvironment environment) : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var failures = new List<string>();
        if (options.Name is null || options.Name.Length > 253 || options.Name.Any(char.IsControl))
            failures.Add("Agent:Name must be at most 253 characters without control characters.");
        if (!IsPersistentDirectory(options.PersistentDataRoot))
        {
            failures.Add("Agent:PersistentDataRoot must be an absolute, dedicated persistent directory, not a filesystem root.");
        }

        if (!Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var uri) || !options.AllowsControlPlaneOrigin(uri, environment.IsDevelopment()))
        {
            failures.Add("Agent:ControlPlaneUrl must be an HTTPS origin without credentials, path, query or fragment. HTTP requires Agent:AllowInsecureHttp (STANZA_ALLOW_INSECURE_HTTP in Compose); a Development-only loopback exception remains available.");
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
