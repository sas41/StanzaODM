using Stanza.ControlPlane.Security;

namespace Stanza.ControlPlane.Models;

public sealed record GroupsModel(IReadOnlyList<GroupSummary> Groups, IReadOnlyList<AccountSummary> Accounts);
public sealed record AccountAccessModel(AccountSummary Account, AccessSummary Access);
