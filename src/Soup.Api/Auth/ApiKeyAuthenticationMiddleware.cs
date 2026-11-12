using Microsoft.EntityFrameworkCore;
using Soup.Api.Data;

namespace Soup.Api.Auth;

public sealed class ApiKeyAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        SoupDbContext database,
        ProjectContext projectContext)
    {
        if (!RequiresApiKey(httpContext.Request))
        {
            await next(httpContext);
            return;
        }

        var presentedKey = httpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(presentedKey))
        {
            await WriteUnauthorizedAsync(httpContext);
            return;
        }

        var keyHash = ApiKeyHasher.Hash(presentedKey);
        var authenticatedKey = await database.ApiKeys
            .Where(key => key.KeyHash == keyHash && key.RevokedAt == null)
            .Select(key => new { key.ProjectId, ApiKeyId = key.Id })
            .SingleOrDefaultAsync(httpContext.RequestAborted);

        if (authenticatedKey is null)
        {
            await WriteUnauthorizedAsync(httpContext);
            return;
        }

        projectContext.Authenticate(authenticatedKey.ProjectId, authenticatedKey.ApiKeyId);
        await next(httpContext);
    }

    private static bool RequiresApiKey(HttpRequest request) =>
        (request.Path.StartsWithSegments("/api") ||
         request.Path.StartsWithSegments("/runs") ||
         request.Path.StartsWithSegments("/api-keys")) &&
        !(request.Method == HttpMethods.Post &&
          (request.Path == "/api/projects" || request.Path == "/projects"));

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "invalid_api_key",
            message = "Provide a valid X-Api-Key header."
        });
    }
}
