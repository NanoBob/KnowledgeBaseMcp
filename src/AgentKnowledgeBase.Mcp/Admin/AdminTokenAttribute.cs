using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Admin;

/// <summary>
/// Guards every action of an admin controller. The 401 body is written directly instead of returned as a
/// result so <c>[ApiController]</c>'s client-error mapping does not replace it with ProblemDetails.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AdminTokenAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (AdminTokenValidator.IsValid(context.HttpContext.Request, options.Token))
        {
            return;
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "admin_token_required" });
        context.Result = new EmptyResult();
    }
}
