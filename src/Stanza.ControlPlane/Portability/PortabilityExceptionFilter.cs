using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Sources;

namespace Stanza.ControlPlane.Portability;

public sealed class PortabilityExceptionFilter(ITempDataDictionaryFactory tempData) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is SourceAccessException or AutomationAccessException or PortabilityAccessException
            || context.Exception is AgentOperationException { StatusCode: 403 })
            context.Result = new ForbidResult();
        else if (context.Exception is SourceException or AutomationException or SettingsOperationException or AgentOperationException)
        {
            tempData.GetTempData(context.HttpContext)["Error"] = context.Exception.Message;
            context.Result = new RedirectToActionResult("Index", "Portability", null);
        }
        else return;
        context.ExceptionHandled = true;
    }
}
