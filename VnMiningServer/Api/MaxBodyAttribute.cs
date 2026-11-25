using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VnMiningServer.Api;

/// <summary>
/// Turns a request away that says it is larger than the server takes, before any of it is read, with an
/// answer that says so. A request that does not say how large it is ends where the limit is reached.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MaxBodyAttribute : Attribute, IResourceFilter
{
    private readonly long _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaxBodyAttribute"/> class.
    /// </summary>
    /// <param name="bytes">The largest request taken.</param>
    public MaxBodyAttribute(long bytes)
    {
        _bytes = bytes;
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength > _bytes)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload Too Large",
                Detail = $"The server takes at most {_bytes / (1024 * 1024)} MB in one request.",
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
            };
        }
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
