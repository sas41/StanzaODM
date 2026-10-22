using System.ComponentModel.DataAnnotations;
using Stanza.ControlPlane.Models;

namespace Stanza.Tests.Web;

public sealed class AccountFormTests
{
    [Fact]
    public void SetupRequiresMatchingStrongPasswords()
    {
        var model = new SetupModel { Username = "owner", Password = "short", ConfirmPassword = "other" };
        var errors = Validate(model);
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(SetupModel.Password)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(SetupModel.ConfirmPassword)));
    }

    [Fact]
    public void LoginBoundsPasswordLength()
    {
        var model = new LoginModel { Username = "owner", Password = new string('x', 1025) };
        Assert.Contains(Validate(model), error => error.MemberNames.Contains(nameof(LoginModel.Password)));
    }

    [Fact]
    public void SettingsAllowBlankPublicOriginAndEnvironment()
    {
        Assert.Empty(Validate(new SettingsModel()));
    }

    [Fact]
    public void ChangePasswordRequiresMatchingNewPassword()
    {
        var model = new ChangePasswordModel { CurrentPassword = "old-password", NewPassword = "new-password-123", ConfirmPassword = "different-password" };
        Assert.Contains(Validate(model), error => error.MemberNames.Contains(nameof(ChangePasswordModel.ConfirmPassword)));
    }

    private static IReadOnlyList<ValidationResult> Validate(object model)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true);
        return errors;
    }
}
