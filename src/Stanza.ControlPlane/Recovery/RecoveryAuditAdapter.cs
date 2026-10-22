using Stanza.ControlPlane.Audit;

namespace Stanza.ControlPlane.Recovery;

public sealed class RecoveryAuditAdapter(AuditService audit) : IRecoveryAuditSink
{
    public Task RecordBackupAsync(Guid actorId, RecoveryResult result, CancellationToken token) => audit.WriteAsync(actorId, "recovery.backup", "instance", "encrypted_archive_created", token);
    public Task RecordRestoreAsync(RecoveryResult result, CancellationToken token) => audit.WriteAsync(null, "recovery.restore", "instance", "restart_required", token);
}
