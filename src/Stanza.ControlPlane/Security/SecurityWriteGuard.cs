using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Persistence;

namespace Stanza.ControlPlane.Security;

internal static class SecurityWriteGuard
{
    internal static async Task LockAsync(StanzaDbContext db, CancellationToken cancellationToken)
    {
        // The row lock is held by the caller's transaction across all checks and writes.
        var count = await db.Set<SecurityGuard>().Where(x => x.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Revision, x => x.Revision + 1), cancellationToken);
        if (count != 1) throw new SecurityOperationException("Complete initial setup before managing security.");
    }
}
