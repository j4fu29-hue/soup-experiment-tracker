using Microsoft.EntityFrameworkCore;

namespace Soup.Api.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        const int attempts = 10;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<SoupDbContext>();
                await database.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception) when (attempt < attempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
