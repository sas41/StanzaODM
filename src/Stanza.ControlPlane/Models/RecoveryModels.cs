using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Stanza.ControlPlane.Models;

public sealed class RecoveryModel
{
    [BindNever] public bool FreshInstance { get; set; }
    [Required, StringLength(1024, MinimumLength = 16), DataType(DataType.Password)]
    public string Passphrase { get; set; } = "";
    public bool Acknowledge { get; set; }
    public IFormFile? Archive { get; set; }
    [BindNever] public bool Restored { get; set; }
    public override string ToString() => nameof(RecoveryModel);
}
