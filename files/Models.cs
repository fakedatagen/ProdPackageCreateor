using System.Text.Json;

public record Card(string Key, string Summary, string Status);
public record Change(string Sha, DateTimeOffset Date, string Subject, string Path, char Kind, string CardKey);
public record Artifact(string Path, string Sha, DateTimeOffset Date, bool Deleted, string Target, List<string> CardKeys, List<string> SupersededShas);

public class Config
{
    public JiraCfg Jira { get; set; } = new();
    public GitCfg Git { get; set; } = new();
    public string TestResultsDir { get; set; } = "./TestResults";
    public string OutputDir { get; set; } = "./out";
    public List<MapRule> DeploymentMap { get; set; } = new();

    public class JiraCfg { public string Mode { get; set; } = "file"; public string CardsFile { get; set; } = "cards.csv"; public string BaseUrl { get; set; } = ""; public string ReadyStatus { get; set; } = "Ready for Deployment"; public string ProjectKey { get; set; } = ""; }
    public class GitCfg { public string RemoteUrl { get; set; } = ""; public string Branch { get; set; } = "qa"; public string CacheDir { get; set; } = "./.gitcache"; public string RepoPath { get; set; } = "."; }
    public class MapRule { public string Prefix { get; set; } = ""; public string Target { get; set; } = ""; public string ProdPath { get; set; } = ""; }

    public static Config Load(string path = "appsettings.json") =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Invalid config");
}
