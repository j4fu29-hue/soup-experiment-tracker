using Microsoft.EntityFrameworkCore;
using Soup.Api.Api;
using Soup.Api.Auth;
using Soup.Api.Data;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try
    {
        using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var healthResponse = await healthClient.GetAsync("http://127.0.0.1:8080/health");
        Environment.ExitCode = healthResponse.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddDbContext<SoupDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<ProjectContext>();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

app.MapGet("/health", async (SoupDbContext database, CancellationToken cancellationToken) =>
    await database.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapProjectEndpoints();
app.MapApiKeyEndpoints();
app.MapRunEndpoints();
app.MapMetricEndpoints();

app.Run();

public partial class Program;
