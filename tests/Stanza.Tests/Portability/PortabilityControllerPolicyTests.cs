using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Controllers;
using Stanza.ControlPlane.Portability;
using Stanza.ControlPlane.Sources;

namespace Stanza.Tests.Portability;

public sealed class PortabilityControllerPolicyTests
{
    [Fact]
    public void Every_portability_mutation_requires_csrf_and_the_controller_is_authenticated_and_not_cacheable()
    {
        var type = typeof(PortabilityController);
        Assert.Single(type.GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.True(Assert.Single(type.GetCustomAttributes(typeof(ResponseCacheAttribute), true).Cast<ResponseCacheAttribute>()).NoStore);
        foreach (var method in type.GetMethods().Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), true).Length > 0))
            Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Concrete_source_and_automation_denials_become_forbidden_not_server_errors(bool source)
    {
        var context = Context(source ? new SourceAccessException() : new AutomationAccessException());
        new PortabilityExceptionFilter(new TempDataFactory()).OnException(context);
        Assert.True(context.ExceptionHandled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public void Safe_validation_errors_return_to_ui_but_unexpected_failures_are_not_misreported_as_success()
    {
        var factory = new TempDataFactory();
        var context = Context(new SourceException("Choose an isolated checkout directory."));
        var filter = new PortabilityExceptionFilter(factory);
        filter.OnException(context);
        Assert.True(context.ExceptionHandled);
        Assert.Equal("Choose an isolated checkout directory.", factory.Values!["Error"]);
        Assert.IsType<RedirectToActionResult>(context.Result);
        var unexpected = Context(new InvalidOperationException("private diagnostic details"));
        filter.OnException(unexpected);
        Assert.False(unexpected.ExceptionHandled);
        Assert.Null(unexpected.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"agent\":\"one\",\"agent\":\"two\"}")]
    [InlineData("{\"agent\":null}")]
    [InlineData("invalid")]
    public void Mapping_parser_rejects_ambiguous_malformed_or_null_references(string input) =>
        Assert.Throws<PortabilityException>(() => PortableMappingParser.Parse<string>(input));

    [Fact]
    public void Mapping_parser_preserves_explicit_account_names_and_approved_agent_ids()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, PortableMappingParser.Parse<Guid>("{\"agent-0\":\"" + id + "\"}")["agent-0"]);
        Assert.Equal("destination-user", PortableMappingParser.Parse<string>("{\"account:source-user\":\"destination-user\"}")["account:source-user"]);
        Assert.Empty(PortableMappingParser.Parse<string>(null));
        Assert.Throws<PortabilityException>(() => PortableMappingParser.Parse<string>(new string(' ', 131073) + "{}"));
    }

    private static ExceptionContext Context(Exception exception) =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = exception };
    private sealed class TempDataFactory : ITempDataDictionaryFactory
    {
        public ITempDataDictionary? Values { get; private set; }
        public ITempDataDictionary GetTempData(HttpContext context) => Values ??= new TempDataDictionary(context, new Provider());
    }
    private sealed class Provider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
