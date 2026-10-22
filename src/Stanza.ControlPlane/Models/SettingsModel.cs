using System.ComponentModel.DataAnnotations;

namespace Stanza.ControlPlane.Models;

public sealed class SettingsModel
{
    [StringLength(2048)] public string PublicUrl { get; set; } = "";
    public string GlobalEnvironment { get; set; } = "";
    public bool CanWriteSettings { get; set; }
    public bool CanRevealEnvironment { get; set; }
    public bool CanWriteEnvironment { get; set; }
    public bool ReplaceEnvironment { get; set; }
    [Range(0, long.MaxValue)] public long Version { get; set; }
}
