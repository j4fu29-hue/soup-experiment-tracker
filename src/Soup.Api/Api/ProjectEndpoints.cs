using Soup.Api.Auth;
using Soup.Api.Data;

namespace Soup.Api.Api;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/projects", CreateProjectAsync);
        endpoints.MapPost("/projects", CreateProjectAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateProjectAsync(
        CreateProjectRequest request,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var projectName = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(projectName) || projectName.Length > 200)
        {
            return Results.BadRequest(new { error = "invalid_project_name" });
        }

        var apiKeyName = string.IsNullOrWhiteSpace(request.ApiKeyName) ? "default" : request.ApiKeyName.Trim();
        if (apiKeyName.Length > 100)
        {
            return Results.BadRequest(new { error = "invalid_api_key_name" });
        }

        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var plaintextKey = ApiKeyHasher.Generate();

        database.Projects.Add(new Project
        {
            Id = projectId,
            Name = projectName,
            CreatedAt = now
        });
        database.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = apiKeyName,
            KeyHash = ApiKeyHasher.Hash(plaintextKey),
            CreatedAt = now
        });

        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/projects/{projectId}", new
        {
            projectId,
            apiKey = plaintextKey,
            warning = "Save this key now. It is stored only as a SHA-256 hash and cannot be shown again."
        });
    }

    public sealed record CreateProjectRequest(string? Name, string? ApiKeyName);
}
