namespace CRM.Application.SystemHealth;

public sealed class BuildDeploymentJenkinsOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Credentials { get; set; } = string.Empty;
    public int MaxLogCharacters { get; set; } = 60000;
    public int MaxArtifactBuilds { get; set; } = 30;
    public BuildDeploymentJenkinsJobOptions[] Jobs { get; set; } = Array.Empty<BuildDeploymentJenkinsJobOptions>();
}

public sealed class BuildDeploymentJenkinsJobOptions
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ProdJobName { get; set; } = string.Empty;
    public string DevJobName { get; set; } = string.Empty;
}
