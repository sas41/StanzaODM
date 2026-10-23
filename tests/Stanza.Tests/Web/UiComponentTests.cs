using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Stanza.ControlPlane.TagHelpers;

namespace Stanza.Tests.Web;

public sealed class UiComponentTests
{
    [Fact]
    public void TablesKeepTheirAttributesAndContentInsideAnAccessibleScrollRegion()
    {
        var context = new TagHelperContext([], new Dictionary<object, object>(), "table-test");
        var output = new TagHelperOutput("table", [new TagHelperAttribute("class", "inventory")],
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        output.Content.SetHtmlContent("<thead><tr><th>Name</th></tr></thead><tbody><tr><td>Deployment</td></tr></tbody>");
        new ResponsiveTableTagHelper().Process(context, output);
        using var writer = new StringWriter(); output.WriteTo(writer, HtmlEncoder.Default);
        var html = writer.ToString();
        Assert.StartsWith("<div class=\"table-scroll\"", html);
        Assert.Contains("role=\"region\"", html); Assert.Contains("aria-label=\"Scrollable table\"", html); Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("<table class=\"inventory\">", html); Assert.Contains("<th>Name</th>", html); Assert.Contains("<td>Deployment</td>", html);
        Assert.EndsWith("</table></div>", html);
    }

    [Fact]
    public void TableWrappingDoesNotTurnEncodedUserContentIntoHtml()
    {
        var context = new TagHelperContext([], new Dictionary<object, object>(), "table-test");
        var output = new TagHelperOutput("table", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        output.Content.SetContent("<script>untrusted()</script>");
        new ResponsiveTableTagHelper().Process(context, output);
        using var writer = new StringWriter(); output.WriteTo(writer, HtmlEncoder.Default);
        Assert.Contains("&lt;script&gt;", writer.ToString()); Assert.DoesNotContain("<script>", writer.ToString());
    }
}
