namespace Soup.Api.Auth;

public sealed class ProjectContext
{
    public Guid ProjectId { get; private set; }
    public Guid ApiKeyId { get; private set; }
    public bool IsAuthenticated => ProjectId != Guid.Empty;

    public void Authenticate(Guid projectId, Guid apiKeyId)
    {
        if (projectId == Guid.Empty || apiKeyId == Guid.Empty)
        {
            throw new ArgumentException("Project and API key ids are required.");
        }

        ProjectId = projectId;
        ApiKeyId = apiKeyId;
    }
}
