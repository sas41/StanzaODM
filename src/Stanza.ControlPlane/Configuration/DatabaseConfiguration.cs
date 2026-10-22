using Npgsql;

namespace Stanza.ControlPlane.Configuration;

public static class DatabaseConfiguration
{
    public static string GetConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("Stanza");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parsed = new NpgsqlConnectionStringBuilder(configured);
            if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database))
                throw new InvalidOperationException("The Stanza database connection requires Host and Database.");
            return parsed.ConnectionString;
        }
        var file = configuration["Database:PasswordFile"];
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            throw new InvalidOperationException("Configure ConnectionStrings__Stanza before launch, or use the supplied Compose stack to generate database credentials.");
        var password = File.ReadAllText(file).TrimEnd('\r', '\n');
        if (password.Length < 16)
            throw new InvalidOperationException("The database password file must contain at least 16 characters.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = configuration["Database:Host"] ?? "database",
            Database = configuration["Database:Name"] ?? "stanza",
            Username = configuration["Database:Username"] ?? "stanza",
            Password = password,
            IncludeErrorDetail = false
        }.ConnectionString;
    }
}
