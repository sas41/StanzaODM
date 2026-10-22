using System.ComponentModel.DataAnnotations;
using Stanza.Core.Security;

namespace Stanza.ControlPlane.Models;

public sealed class LoginModel
{
    [Required, StringLength(64)] public string Username { get; set; } = "";
    [Required, StringLength(1024), DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public sealed class SetupModel
{
    [Required, StringLength(64)] public string Username { get; set; } = "";
    [Required, StringLength(1024, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}

public sealed class CreateUserModel
{
    [Required, StringLength(64)] public string Username { get; set; } = "";
    [Required, StringLength(1024, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    public AccountRole Role { get; set; } = AccountRole.User;
}

public sealed class ChangePasswordModel
{
    [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(1024, MinimumLength = 12), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [Required, Compare(nameof(NewPassword)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
