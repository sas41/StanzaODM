namespace Stanza.Agent.Runtime;

public sealed class DockerRuntimeOptions
{
    public bool EnableExecution { get; set; }
    public string ExecutablePath { get; set; } = "/usr/bin/docker";
    // Unix socket only in this foundation. Remote endpoints need an explicit TLS design.
    public string Endpoint { get; set; } = "unix:///var/run/docker.sock";
    public int OperationTimeoutSeconds { get; set; } = 300;
    public int MaxOutputCharacters { get; set; } = 65536;

    public static bool IsValid(DockerRuntimeOptions options) =>
        !string.IsNullOrWhiteSpace(options.ExecutablePath) && Path.IsPathFullyQualified(options.ExecutablePath) &&
        !options.ExecutablePath.Any(char.IsControl) &&
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "unix" &&
        options.Endpoint.StartsWith("unix:///", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(endpoint.Host) && !string.IsNullOrEmpty(endpoint.AbsolutePath) && endpoint.AbsolutePath != "/" &&
        string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) &&
        options.OperationTimeoutSeconds is >= 1 and <= 3600 && options.MaxOutputCharacters is >= 1024 and <= 1048576;
}
