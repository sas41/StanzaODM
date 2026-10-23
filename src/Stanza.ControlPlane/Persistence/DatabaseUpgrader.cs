using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Stanza.ControlPlane.Persistence;

/// <summary>Versioned additive upgrades also support databases originally created with EnsureCreated.</summary>
public static class DatabaseUpgrader
{
    public static async Task UpgradeAsync(StanzaDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(827346901)", ct);
        const string key = "schema-version";
        var marker = await db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
        var version = marker is null ? 0 : int.Parse(marker.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (version < 1)
        {
            var identityType = db.Database.IsNpgsql() ? "uuid" : "TEXT";
            var createOutput = $"""
                CREATE TABLE IF NOT EXISTS "OperationOutput" (
                    "OperationId" {identityType} NOT NULL,
                    "Sequence" bigint NOT NULL,
                    "Stream" text NOT NULL,
                    "ProtectedText" text NOT NULL,
                    CONSTRAINT "PK_OperationOutput" PRIMARY KEY ("OperationId", "Sequence"),
                    CONSTRAINT "FK_OperationOutput_Operations_OperationId" FOREIGN KEY ("OperationId") REFERENCES "Operations" ("Id") ON DELETE CASCADE
                )
                """;
            await db.Database.ExecuteSqlRawAsync(createOutput, ct);
            // Missing new settings inherit the old cadence, not new-install defaults. Existing values remain untouched.
            var policy = await db.Settings.SingleOrDefaultAsync(x => x.Key == "operational-policy", ct);
            if (policy is null && await db.Accounts.AnyAsync(ct))
            {
                var legacy = new Configuration.OperationalPolicy { OperationTimeoutSeconds = 300, MaximumTextBytes = 1048576,
                    MaximumUploadBytes = 4194304, MaximumWorkspaceBytes = 16777216, MaximumSupportingFiles = 100, TrustedProxyAddresses = "" };
                policy = new SettingEntity { Key = "operational-policy", Value = System.Text.Json.JsonSerializer.Serialize(new Configuration.PolicyEditor(legacy, 0)) };
                db.Settings.Add(policy);
            }
            if (policy is not null && JsonNode.Parse(policy.Value) is JsonObject document && document["Policy"] is JsonObject values)
            {
                var interval = values["StatusIntervalSeconds"]?.GetValue<int>() ?? 15;
                values.TryAdd("ConfigurationIntervalSeconds", JsonValue.Create(interval));
                values.TryAdd("StaleStatusSeconds", JsonValue.Create(interval));
                policy.Value = document.ToJsonString();
            }
            if (marker is null) db.Settings.Add(new() { Key = key, Value = "1" }); else marker.Value = "1";
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
