using Microsoft.EntityFrameworkCore;
using Soup.Api.Auth;
using Soup.Api.Data;

namespace Soup.Api.Api;

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/api-keys/rotate", RotateAsync);
        endpoints.MapPost("/api-keys/rotate", RotateAsync);
        return endpoints;
    }

    private static async Task<IResult> RotateAsync(
        RotateApiKeyRequest request,
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "rotated" : request.Name.Trim();
        if (name.Length > 100)
        {
            return Results.BadRequest(new { error = "invalid_api_key_name" });
        }

        var now = DateTimeOffset.UtcNow;
        var plaintextKey = ApiKeyHasher.Generate();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var revoked = await database.ApiKeys
            .Where(key => key.ProjectId == project.ProjectId &&
                          key.Id == project.ApiKeyId &&
                          key.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(key => key.RevokedAt, now),
                cancellationToken);
        if (revoked != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new { error = "api_key_already_rotated" });
        }

        var newKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            ProjectId = project.ProjectId,
            Name = name,
            KeyHash = ApiKeyHasher.Hash(plaintextKey),
            CreatedAt = now
        };
        database.ApiKeys.Add(newKey);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Created("/api-keys", new
        {
            apiKey = plaintextKey,
            apiKeyId = newKey.Id,
            revokedApiKeyId = project.ApiKeyId,
            warning = "The previous key is revoked. Save this replacement now; it cannot be shown again."
        });
    }

    public sealed record RotateApiKeyRequest(string? Name);
}
