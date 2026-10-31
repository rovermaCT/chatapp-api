using ChatApp.Api.Extensions;
using ChatApp.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid CurrentUserId => User.GetUserId();

    protected ActionResult<T> FromResult<T>(ServiceResult<T> result) =>
        result.Error is { } error ? ProblemFor(error) : Ok(result.Value);

    protected ObjectResult ProblemFor(ServiceError error)
    {
        HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(GetType())
            .LogDebug("Returning problem {StatusCode} {Title}: {Detail}", error.StatusCode, error.Title, error.Detail);
        return Problem(statusCode: error.StatusCode, title: error.Title, detail: error.Detail);
    }
}
