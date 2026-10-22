using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Core.Security;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Security;
using Stanza.ControlPlane.Persistence;

namespace Stanza.Tests.Web;

public sealed class AccessUiTests
{
    private const string Password = "a-secure-password-123";
    private const string ReplacementPassword = "replacement-secure-password-456";

    [Fact]
    public async Task SystemAdminCanLogInAndCreateOrdinaryAndAdminAccountsThroughHttp()
    {
        await using var session = await Session.CreateAsync();
        Assert.Equal(AccountRole.SystemAdmin, session.Owner.Role);
        Assert.Equal(AccountRole.Admin, session.Admin.Role);
        Assert.Equal(AccountRole.User, session.User.Role);
        Assert.Equal(3, (await AccountsAsync(session.Factory)).Count);
        using var ordinary = Client(session.Factory);
        await LoginAsync(ordinary, session.User.Username);
        Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync("/")).StatusCode);
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/users")).StatusCode);
    }

    [Fact]
    public async Task HttpsLoginIssuesProtectedSessionCookieWithoutRawAccountSecrets()
    {
        await using var session = await Session.CreateAsync();
        using var client = Client(session.Factory);
        using var response = await PostAsync(client, "/account/login", "/account/login", ("Username", "owner"), ("Password", Password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Stanza.Session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.DoesNotContain(session.Owner.SecurityStamp, cookie);
        Assert.DoesNotContain(Password, cookie);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("SystemAdmin")]
    [InlineData("2")]
    public async Task CraftedAdminCreationOfPrivilegedAccountsDoesNotMutateState(string role)
    {
        await using var session = await Session.CreateAsync();
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        var before = await AccountsAsync(session.Factory);
        using var response = await PostAsync(admin, "/users/create", "/users/create",
            ("Username", "crafted"), ("Password", Password), ("Role", role));
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await AccountsAsync(session.Factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CraftedAdminRequestsCannotManageAdminOrSystemAdminTargets(bool systemAdminTarget)
    {
        await using var session = await Session.CreateAsync();
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        var target = systemAdminTarget ? session.Owner : session.Admin;
        var routes = new (string Suffix, (string Key, string Value)[] Fields)[]
        {
            ("role", [("role", "User")]),
            ("role", [("role", "SystemAdmin")]),
            ("enabled", [("enabled", "false")]),
            ("password", [("password", ReplacementPassword)]),
            ("delete", [])
        };
        foreach (var (suffix, fields) in routes)
        {
            using var response = await PostAsync(admin, $"/users/{target.Id}/{suffix}", "/users", fields);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(target, await FindAsync(session.Factory, target.Id));
            Assert.NotNull(await AuthenticateAsync(session.Factory, target.Username, Password));
            Assert.Null(await AuthenticateAsync(session.Factory, target.Username, ReplacementPassword));
        }
    }

    [Fact]
    public async Task AdminCanManageOrdinaryUsersButCannotPromoteThem()
    {
        await using var session = await Session.CreateAsync();
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        await CreateUserAsync(session.Factory, admin, "admin-created", AccountRole.User);
        var user = Assert.Single(await AccountsAsync(session.Factory), x => x.Username == "admin-created");
        using (await PostAsync(admin, $"/users/{user.Id}/role", "/users", ("role", "Admin"))) { }
        Assert.Equal(user, await FindAsync(session.Factory, user.Id));
        using (await PostAsync(admin, $"/users/{user.Id}/password", "/users", ("password", ReplacementPassword))) { }
        Assert.Null(await AuthenticateAsync(session.Factory, user.Username, Password));
        Assert.NotNull(await AuthenticateAsync(session.Factory, user.Username, ReplacementPassword));
        using (await PostAsync(admin, $"/users/{user.Id}/enabled", "/users", ("enabled", "false"))) { }
        Assert.False((await FindAsync(session.Factory, user.Id)).Enabled);
        Assert.Null(await AuthenticateAsync(session.Factory, user.Username, ReplacementPassword));
        using (await PostAsync(admin, $"/users/{user.Id}/enabled", "/users", ("enabled", "true"))) { }
        Assert.True((await FindAsync(session.Factory, user.Id)).Enabled);
        using (await PostAsync(admin, $"/users/{user.Id}/delete", "/users")) { }
        Assert.DoesNotContain(await AccountsAsync(session.Factory), x => x.Id == user.Id);
    }

    [Fact]
    public async Task AdminCanManageOrdinaryUserGroupMembershipAndGrantCrudThroughHttp()
    {
        await using var session = await Session.CreateAsync();
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        using (await PostAsync(admin, "/groups/create", "/groups", ("name", "Admin-managed"))) { }
        var group = Assert.Single(await GroupsAsync(session.Factory));
        using (await PostAsync(admin, $"/groups/{group.Id}/members/add", "/groups", ("accountId", session.User.Id.ToString()))) { }
        using (await PostAsync(admin, $"/groups/{group.Id}/grants", "/groups", ("permission", "deployment.read|deployment"))) { }
        using (await PostAsync(admin, $"/users/{session.User.Id}/grants", $"/users/{session.User.Id}/access", ("permission", "deployment.start|deployment"))) { }
        var current = await AccessAsync(session.Factory, session.User.Id);
        Assert.Equal(2, current.EffectivePermissions.Count);
        Assert.All(current.EffectivePermissions, grant => Assert.True(grant.Allowed));
        var directGrant = Assert.Single(current.DirectGrants);
        var groupGrant = Assert.Single(Assert.Single(current.Groups).Grants);
        using (await PostAsync(admin, $"/groups/{group.Id}/rename", "/groups", ("name", "Renamed by Admin"))) { }
        Assert.Equal("Renamed by Admin", Assert.Single(await GroupsAsync(session.Factory)).Name);
        using (await PostAsync(admin, $"/groups/grants/{groupGrant.Id}/delete", "/groups")) { }
        using (await PostAsync(admin, $"/users/{session.User.Id}/grants/delete", $"/users/{session.User.Id}/access", ("grantId", directGrant.Id.ToString()))) { }
        Assert.Empty((await AccessAsync(session.Factory, session.User.Id)).EffectivePermissions);
        using (await PostAsync(admin, $"/groups/{group.Id}/members/remove", "/groups", ("accountId", session.User.Id.ToString()))) { }
        Assert.Empty(Assert.Single(await GroupsAsync(session.Factory)).Members);
        using (await PostAsync(admin, $"/groups/{group.Id}/delete", "/groups")) { }
        Assert.Empty(await GroupsAsync(session.Factory));
        Assert.Equal(session.Admin, await FindAsync(session.Factory, session.Admin.Id));
    }

    [Fact]
    public async Task AccountManagementMutationsAlsoRequireCsrf()
    {
        await using var session = await Session.CreateAsync();
        var before = await AccountsAsync(session.Factory);
        var routes = new (string Path, (string Key, string Value)[] Fields)[]
        {
            ("/users/create", [("Username", "forbidden"), ("Password", Password), ("Role", "SystemAdmin")]),
            ($"/users/{session.User.Id}/enabled", [("enabled", "false")]),
            ($"/users/{session.User.Id}/role", [("role", "SystemAdmin")]),
            ($"/users/{session.User.Id}/password", [("password", ReplacementPassword)]),
            ($"/users/{session.User.Id}/delete", [])
        };
        foreach (var (path, fields) in routes)
        {
            using var response = await session.OwnerClient.PostAsync(path, Form(fields));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await AccountsAsync(session.Factory));
        }
        Assert.NotNull(await AuthenticateAsync(session.Factory, session.User.Username, Password));
        Assert.Null(await AuthenticateAsync(session.Factory, session.User.Username, ReplacementPassword));
    }

    [Theory]
    [InlineData("role", null)]
    [InlineData("role", "")]
    [InlineData("role", "not-a-role")]
    [InlineData("role", "999")]
    [InlineData("enabled", null)]
    [InlineData("enabled", "")]
    [InlineData("enabled", "not-a-bool")]
    public async Task MissingOrMalformedAccountMutationValuesDoNotDefaultToDestructiveChanges(string route, string? value)
    {
        await using var session = await Session.CreateAsync();
        var fields = value is null ? Array.Empty<(string Key, string Value)>() : [(route, value)];
        using var response = await PostAsync(session.OwnerClient, $"/users/{session.Admin.Id}/{route}", "/users", fields);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(session.Admin, await FindAsync(session.Factory, session.Admin.Id));
    }

    [Fact]
    public async Task OrdinaryUserCannotReadOrMutateGroupsSettingsOrAccountPermissionsEvenWithValidCsrf()
    {
        await using var session = await Session.CreateAsync();
        var group = await CreateGroupAsync(session, "Protected");
        var grant = await AddDirectGrantAsync(session, session.User.Id, "deployment.read|deployment");
        var before = await FindAsync(session.Factory, session.User.Id);
        var settings = await SettingsAsync(session);
        using var ordinary = Client(session.Factory);
        await LoginAsync(ordinary, session.User.Username);
        foreach (var path in new[] { "/groups", "/settings", "/users", $"/users/{session.User.Id}/access" })
        {
            using var response = await ordinary.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/account/denied", response.Headers.Location?.ToString());
        }
        var routes = new (string Path, (string Key, string Value)[] Fields)[]
        {
            ("/groups/create", [("name", "Forbidden")]),
            ($"/groups/{group.Id}/rename", [("name", "Forbidden")]),
            ($"/groups/{group.Id}/delete", []),
            ($"/groups/{group.Id}/members/add", [("accountId", session.User.Id.ToString())]),
            ($"/groups/{group.Id}/members/remove", [("accountId", session.User.Id.ToString())]),
            ($"/groups/{group.Id}/grants", [("permission", "deployment.stop|deployment")]),
            ($"/groups/grants/{grant.Id}/delete", []),
            ($"/users/{session.User.Id}/grants", [("permission", "deployment.stop|deployment")]),
            ($"/users/{session.User.Id}/grants/delete", [("grantId", grant.Id.ToString())]),
            ("/settings", [("PublicUrl", "https://attacker.invalid"), ("GlobalEnvironment", "CHANGED=yes"), ("Version", settings.Version.ToString())])
        };
        foreach (var (path, fields) in routes)
        {
            using var response = await PostAsync(ordinary, path, "/account/password", fields);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/account/denied", response.Headers.Location?.ToString());
        }
        Assert.Equal(before, await FindAsync(session.Factory, session.User.Id));
        var remainingGroup = Assert.Single(await GroupsAsync(session.Factory));
        Assert.Equal("Protected", remainingGroup.Name);
        Assert.Empty(remainingGroup.Members);
        Assert.Empty(remainingGroup.Grants);
        Assert.Equal(grant, Assert.Single((await AccessAsync(session.Factory, session.User.Id)).DirectGrants));
        Assert.Equal(settings, await SettingsAsync(session));
    }

    [Fact]
    public async Task EveryGroupGrantAndSettingsMutationRequiresCsrfAndRejectsAnotherSessionsToken()
    {
        await using var session = await Session.CreateAsync();
        var group = await CreateGroupAsync(session, "Readers");
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/members/add", "/groups", ("accountId", session.User.Id.ToString()))) { }
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/grants", "/groups", ("permission", "deployment.read|deployment"))) { }
        var groupGrant = Assert.Single(Assert.Single(await GroupsAsync(session.Factory)).Grants);
        var directGrant = await AddDirectGrantAsync(session, session.User.Id, "deployment.start|deployment");
        var before = await FindAsync(session.Factory, session.User.Id);
        var settings = await SettingsAsync(session);
        var routes = new (string Path, (string Key, string Value)[] Fields)[]
        {
            ("/groups/create", [("name", "Forbidden")]),
            ($"/groups/{group.Id}/rename", [("name", "Forbidden")]),
            ($"/groups/{group.Id}/delete", []),
            ($"/groups/{group.Id}/members/add", [("accountId", session.Admin.Id.ToString())]),
            ($"/groups/{group.Id}/members/remove", [("accountId", session.User.Id.ToString())]),
            ($"/groups/{group.Id}/grants", [("permission", "deployment.stop|deployment")]),
            ($"/groups/grants/{groupGrant.Id}/delete", []),
            ($"/users/{session.User.Id}/grants", [("permission", "deployment.stop|deployment")]),
            ($"/users/{session.User.Id}/grants/delete", [("grantId", directGrant.Id.ToString())]),
            ("/settings", [("PublicUrl", "https://attacker.invalid"), ("GlobalEnvironment", "CHANGED=yes"), ("Version", settings.Version.ToString())])
        };
        using var otherSession = Client(session.Factory);
        await LoginAsync(otherSession, session.Admin.Username);
        var foreignToken = await TokenAsync(otherSession, "/groups");
        foreach (var (path, fields) in routes)
        {
            using var missing = await session.OwnerClient.PostAsync(path, Form(fields));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var mismatched = await session.OwnerClient.PostAsync(path, Form(fields.Append(("__RequestVerificationToken", foreignToken)).ToArray()));
            Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        }
        Assert.Equal(before, await FindAsync(session.Factory, session.User.Id));
        var unchanged = Assert.Single(await GroupsAsync(session.Factory));
        Assert.Equal("Readers", unchanged.Name);
        Assert.Equal(session.User.Id, Assert.Single(unchanged.Members));
        Assert.Equal(groupGrant, Assert.Single(unchanged.Grants));
        Assert.Equal(directGrant, Assert.Single((await AccessAsync(session.Factory, session.User.Id)).DirectGrants));
        Assert.Equal(settings, await SettingsAsync(session));
    }

    [Theory]
    [InlineData("*|deployment", "")]
    [InlineData("deployment.read|*", "")]
    [InlineData("deployment.read|agent", "")]
    [InlineData("account.manage|account", "")]
    [InlineData("deployment.read", "")]
    [InlineData("deployment.read|deployment|extra", "")]
    [InlineData("", "")]
    [InlineData("deployment.read|deployment", "not-a-guid")]
    [InlineData("deployment.read|deployment", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidGrantActionAndResourceInputsCannotCreateDirectOrGroupGrants(string permission, string resource)
    {
        await using var session = await Session.CreateAsync();
        var group = await CreateGroupAsync(session, "Readers");
        var fields = new[] { ("permission", permission), ("resourceId", resource) };
        foreach (var (post, tokenPage) in new[]
        {
            ($"/users/{session.User.Id}/grants", $"/users/{session.User.Id}/access"),
            ($"/groups/{group.Id}/grants", "/groups")
        })
        {
            using var response = await PostAsync(session.OwnerClient, post, tokenPage, fields);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Empty((await AccessAsync(session.Factory, session.User.Id)).DirectGrants);
            Assert.Empty(Assert.Single(await GroupsAsync(session.Factory)).Grants);
            Assert.Equal(session.User, await FindAsync(session.Factory, session.User.Id));
        }
    }

    [Fact]
    public async Task EffectivePermissionsExplainCombinedSourcesAndRevocationInvalidatesExistingUserSessions()
    {
        await using var session = await Session.CreateAsync();
        var resource = Guid.NewGuid();
        var group = await CreateGroupAsync(session, "Release readers");
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/members/add", "/groups", ("accountId", session.User.Id.ToString()))) { }
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/grants", "/groups",
            ("permission", "deployment.read|deployment"), ("resourceId", resource.ToString()))) { }
        var direct = await AddDirectGrantAsync(session, session.User.Id, "deployment.read|deployment", resource);
        var html = WebUtility.HtmlDecode(await session.OwnerClient.GetStringAsync($"/users/{session.User.Id}/access"));
        Assert.Contains("Release readers", html);
        Assert.Contains("Direct grant, Group: Release readers", html);
        Assert.Contains(resource.ToString(), html);
        var effective = Assert.Single((await AccessAsync(session.Factory, session.User.Id)).EffectivePermissions);
        Assert.True(effective.Allowed);
        Assert.Equal(resource, effective.ResourceId);
        using var ordinary = Client(session.Factory);
        await LoginAsync(ordinary, session.User.Username);
        Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync("/")).StatusCode);
        var before = await FindAsync(session.Factory, session.User.Id);
        using (await PostAsync(session.OwnerClient, $"/users/{session.User.Id}/grants/delete", $"/users/{session.User.Id}/access", ("grantId", direct.Id.ToString()))) { }
        Assert.NotEqual(before.SecurityStamp, (await FindAsync(session.Factory, session.User.Id)).SecurityStamp);
        using var revoked = await ordinary.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Contains("/account/login", revoked.Headers.Location?.ToString());
        Assert.Contains(revoked.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("Stanza.Session=;", StringComparison.Ordinal));
        await LoginAsync(ordinary, session.User.Username);
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/members/remove", "/groups", ("accountId", session.User.Id.ToString()))) { }
        Assert.Empty((await AccessAsync(session.Factory, session.User.Id)).EffectivePermissions);
        using var groupRevoked = await ordinary.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, groupRevoked.StatusCode);
        Assert.Contains("/account/login", groupRevoked.Headers.Location?.ToString());
        html = await session.OwnerClient.GetStringAsync($"/users/{session.User.Id}/access");
        Assert.DoesNotContain("Group: Release readers", html);
        Assert.DoesNotContain("Direct grant,", html);
    }

    [Fact]
    public async Task AddingAndRemovingGroupGrantsInvalidatesExistingMemberCookies()
    {
        await using var session = await Session.CreateAsync();
        var group = await CreateGroupAsync(session, "Readers");
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/members/add", "/groups", ("accountId", session.User.Id.ToString()))) { }
        using var ordinary = Client(session.Factory);
        await LoginAsync(ordinary, session.User.Username);
        var before = await FindAsync(session.Factory, session.User.Id);
        using (await PostAsync(session.OwnerClient, $"/groups/{group.Id}/grants", "/groups", ("permission", "deployment.read|deployment"))) { }
        Assert.NotEqual(before.SecurityStamp, (await FindAsync(session.Factory, session.User.Id)).SecurityStamp);
        using (var stale = await ordinary.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
            Assert.Contains("/account/login", stale.Headers.Location?.ToString());
        }
        await LoginAsync(ordinary, session.User.Username);
        var grant = Assert.Single(Assert.Single(await GroupsAsync(session.Factory)).Grants);
        before = await FindAsync(session.Factory, session.User.Id);
        using (await PostAsync(session.OwnerClient, $"/groups/grants/{grant.Id}/delete", "/groups")) { }
        Assert.NotEqual(before.SecurityStamp, (await FindAsync(session.Factory, session.User.Id)).SecurityStamp);
        Assert.Empty((await AccessAsync(session.Factory, session.User.Id)).EffectivePermissions);
        using var revoked = await ordinary.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Contains("/account/login", revoked.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CraftedGroupAndDirectGrantRequestsCannotIndirectlyManagePrivilegedAccounts()
    {
        await using var session = await Session.CreateAsync();
        var group = await CreateGroupAsync(session, "Readers");
        using var admin = Client(session.Factory);
        await LoginAsync(admin, session.Admin.Username);
        foreach (var target in new[] { session.Owner, session.Admin })
        {
            using (await PostAsync(admin, $"/groups/{group.Id}/members/add", "/groups", ("accountId", target.Id.ToString()))) { }
            using (await PostAsync(admin, $"/users/{target.Id}/grants", $"/users/{session.User.Id}/access", ("permission", "deployment.read|deployment"))) { }
            Assert.Equal(target, await FindAsync(session.Factory, target.Id));
            Assert.Empty((await AccessAsync(session.Factory, target.Id)).DirectGrants);
        }
        Assert.Empty(Assert.Single(await GroupsAsync(session.Factory)).Members);
        var privilegedGrant = await AddDirectGrantAsync(session, session.Admin.Id, "deployment.read|deployment");
        var privilegedBefore = await FindAsync(session.Factory, session.Admin.Id);
        // The System Admin's legitimate grant invalidated this Admin's previous cookie.
        await LoginAsync(admin, session.Admin.Username);
        using (await PostAsync(admin, $"/groups/grants/{privilegedGrant.Id}/delete", "/groups")) { }
        Assert.Equal(privilegedGrant, Assert.Single((await AccessAsync(session.Factory, session.Admin.Id)).DirectGrants));
        Assert.Equal(privilegedBefore, await FindAsync(session.Factory, session.Admin.Id));
    }

    [Fact]
    public async Task DirectGrantDeletionCannotBeRetargetedThroughAnotherAccountsUrl()
    {
        await using var session = await Session.CreateAsync();
        var grant = await AddDirectGrantAsync(session, session.User.Id, "deployment.read|deployment");
        var before = await FindAsync(session.Factory, session.User.Id);
        using var response = await PostAsync(session.OwnerClient, $"/users/{session.Admin.Id}/grants/delete", "/users", ("grantId", grant.Id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(grant, Assert.Single((await AccessAsync(session.Factory, session.User.Id)).DirectGrants));
        Assert.Equal(before, await FindAsync(session.Factory, session.User.Id));
    }

    [Fact]
    public async Task HtmlNeverExposesPasswordHashesSecurityStampsOrSubmittedPasswordsAndSettingsRemainPrivate()
    {
        await using var session = await Session.CreateAsync();
        const string secret = "GLOBAL_SECRET=sensitive-environment-value-123";
        var settings = await SettingsAsync(session);
        using (await PostAsync(session.OwnerClient, "/settings", "/settings", ("PublicUrl", "https://stanza.example.test"),
            ("GlobalEnvironment", secret), ("Version", settings.Version.ToString()))) { }
        Assert.Equal(secret, (await SettingsAsync(session)).GlobalEnvironment);
        var storedSecrets = await ReadAsync(session.Factory, provider => provider.GetRequiredService<StanzaDbContext>().Accounts
            .AsNoTracking().Select(x => new[] { x.PasswordHash, x.SecurityStamp }).ToListAsync());
        foreach (var path in new[] { "/users", "/users/create", "/groups", "/settings", "/account/password", $"/users/{session.User.Id}/access" })
        {
            using var response = await session.OwnerClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            var html = await response.Content.ReadAsStringAsync();
            foreach (var value in storedSecrets.SelectMany(x => x)) Assert.DoesNotContain(value, html);
            Assert.DoesNotContain(Password, html);
            Assert.DoesNotContain("PasswordHash", html);
            Assert.DoesNotContain("SecurityStamp", html);
            if (path != "/settings") Assert.DoesNotContain(secret, html);
        }
        using var failedCreation = await PostAsync(session.OwnerClient, "/users/create", "/users/create",
            ("Username", session.User.Username), ("Password", ReplacementPassword), ("Role", "User"));
        Assert.DoesNotContain(ReplacementPassword, await failedCreation.Content.ReadAsStringAsync());
        using var ordinary = Client(session.Factory);
        await LoginAsync(ordinary, session.User.Username);
        using var denied = await ordinary.GetAsync("/settings");
        Assert.DoesNotContain(secret, await denied.Content.ReadAsStringAsync());
        using var anonymous = Client(session.Factory);
        using var failedLogin = await PostAsync(anonymous, "/account/login", "/account/login",
            ("Username", "unknown"), ("Password", ReplacementPassword));
        Assert.DoesNotContain(ReplacementPassword, await failedLogin.Content.ReadAsStringAsync());
    }

    private static HttpClient Client(StanzaFactory factory) => factory.CreateClient(new()
    {
        AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost")
    });

    private static async Task LoginAsync(HttpClient client, string username)
    {
        using var response = await PostAsync(client, "/account/login", "/account/login", ("Username", username), ("Password", Password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("Stanza.Session=", StringComparison.Ordinal));
    }

    private static async Task CreateUserAsync(StanzaFactory factory, HttpClient client, string username, AccountRole role)
    {
        using var response = await PostAsync(client, "/users/create", "/users/create",
            ("Username", username), ("Password", Password), ("Role", role.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var account = Assert.Single(await AccountsAsync(factory), x => x.Username == username);
        Assert.Equal(role, account.Role);
        Assert.True(account.Enabled);
    }

    private static async Task<GroupSummary> CreateGroupAsync(Session session, string name)
    {
        using var response = await PostAsync(session.OwnerClient, "/groups/create", "/groups", ("name", name));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Assert.Single(await GroupsAsync(session.Factory), x => x.Name == name);
    }

    private static async Task<GrantSummary> AddDirectGrantAsync(Session session, Guid id, string permission, Guid? resource = null)
    {
        using var response = await PostAsync(session.OwnerClient, $"/users/{id}/grants", $"/users/{session.User.Id}/access",
            ("permission", permission), ("resourceId", resource?.ToString() ?? ""));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Assert.Single((await AccessAsync(session.Factory, id)).DirectGrants);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string tokenPage,
        params (string Key, string Value)[] fields)
    {
        var token = await TokenAsync(client, tokenPage);
        using var content = Form(fields.Append(("__RequestVerificationToken", token)).ToArray());
        return await client.PostAsync(path, content);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"An antiforgery token should be present at {path}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<T> ReadAsync<T>(StanzaFactory factory, Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider);
    }

    private static Task<IReadOnlyList<AccountSummary>> AccountsAsync(StanzaFactory factory) =>
        ReadAsync(factory, provider => provider.GetRequiredService<AccountService>().ListAsync());
    private static async Task<AccountSummary> FindAsync(StanzaFactory factory, Guid id) =>
        Assert.IsType<AccountSummary>(await ReadAsync(factory, provider => provider.GetRequiredService<AccountService>().FindAsync(id)));
    private static Task<AccountSummary?> AuthenticateAsync(StanzaFactory factory, string username, string password) =>
        ReadAsync(factory, provider => provider.GetRequiredService<AccountService>().AuthenticateAsync(username, password));
    private static Task<AccessSummary> AccessAsync(StanzaFactory factory, Guid id) =>
        ReadAsync(factory, provider => provider.GetRequiredService<AccessService>().GetAccessAsync(id));
    private static Task<IReadOnlyList<GroupSummary>> GroupsAsync(StanzaFactory factory) =>
        ReadAsync(factory, provider => provider.GetRequiredService<AccessService>().ListGroupsAsync());
    private static Task<InstanceSettings> SettingsAsync(Session session) =>
        ReadAsync(session.Factory, provider => provider.GetRequiredService<InstanceSettingsService>().ReadAsync(session.Owner.Id));

    private sealed class Session(StanzaFactory factory, HttpClient ownerClient, AccountSummary owner, AccountSummary admin, AccountSummary user) : IAsyncDisposable
    {
        public StanzaFactory Factory { get; } = factory;
        public HttpClient OwnerClient { get; } = ownerClient;
        public AccountSummary Owner { get; } = owner;
        public AccountSummary Admin { get; } = admin;
        public AccountSummary User { get; } = user;

        public static async Task<Session> CreateAsync()
        {
            var factory = new StanzaFactory();
            var client = Client(factory);
            try
            {
                var ownerId = await factory.BootstrapAsync();
                await LoginAsync(client, "owner");
                await CreateUserAsync(factory, client, "admin", AccountRole.Admin);
                await CreateUserAsync(factory, client, "ordinary", AccountRole.User);
                var accounts = await AccountsAsync(factory);
                return new(factory, client, Assert.Single(accounts, x => x.Id == ownerId),
                    Assert.Single(accounts, x => x.Username == "admin"), Assert.Single(accounts, x => x.Username == "ordinary"));
            }
            catch
            {
                client.Dispose();
                await factory.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            OwnerClient.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
