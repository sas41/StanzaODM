namespace Stanza.ControlPlane.Configuration;

// Bootstrap-only opt-in: a browser request or configuration import cannot weaken transport security.
public sealed record BrowserHttpPolicy(bool AllowInsecureHttp = false);
