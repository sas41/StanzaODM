using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Stanza.ControlPlane.TagHelpers;

/// <summary>Keep wide operational tables within a keyboard-accessible scroll region.</summary>
[HtmlTargetElement("table")]
public sealed class ResponsiveTableTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.PreElement.AppendHtml("<div class=\"table-scroll\" role=\"region\" aria-label=\"Scrollable table\" tabindex=\"0\">");
        output.PostElement.AppendHtml("</div>");
    }
}
