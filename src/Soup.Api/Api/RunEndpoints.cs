using Microsoft.EntityFrameworkCore;
using Soup.Api.Auth;
using Soup.Api.Data;

namespace Soup.Api.Api;

public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/runs", CreateRunAsync);
        endpoints.MapGet("/api/runs", ListRunsAsync);
        endpoints.MapGet("/api/runs/{runId:guid}", GetRunAsync);
        endpoints.MapPost("/runs", CreateRunAsync);
        endpoints.MapGet("/runs", ListRunsAsync);
        endpoints.MapGet("/runs/{runId:guid}", GetRunAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateRunAsync(
        CreateRunRequest request,
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            return Results.BadRequest(new { error = "invalid_run_name" });
        }

        var run = new Run
        {
            Id = Guid.NewGuid(),
            ProjectId = project.ProjectId,
            Name = name,
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow
        };

        database.Runs.Add(run);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/runs/{run.Id}", ToResponse(run));
    }

    private static async Task<IResult> ListRunsAsync(
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var runs = await database.Runs
            .AsNoTracking()
            .Where(run => run.ProjectId == project.ProjectId)
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => new RunResponse(run.Id, run.Name, run.Status, run.CreatedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(runs);
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var run = await database.Runs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ProjectId == project.ProjectId && candidate.Id == runId,
                cancellationToken);

        return run is null ? Results.NotFound() : Results.Ok(ToResponse(run));
    }

    private static RunResponse ToResponse(Run run) =>
        new(run.Id, run.Name, run.Status, run.CreatedAt);

    public sealed record CreateRunRequest(string? Name);
    public sealed record RunResponse(Guid Id, string Name, string Status, DateTimeOffset CreatedAt);
}
