using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class Packager
{
    readonly Config _cfg; readonly GitService _git;
    public Packager(Config cfg, GitService git) { _cfg = cfg; _git = git; }

    // Remove duplicate paths: keep the latest commit (by date) per path across all selected cards.
    public List<Artifact> Resolve(List<Change> changes)
    {
        return changes.GroupBy(c => c.Path).Select(g =>
        {
            var ordered = g.OrderByDescending(c => c.Date).ToList();
            var latest = ordered[0];
            return new Artifact(g.Key, latest.Sha, latest.Date, latest.Kind == 'D', MapTarget(g.Key) ?? "",
                g.Select(c => c.CardKey).Distinct().OrderBy(k => k).ToList(),
                ordered.Skip(1).Select(c => c.Sha).Distinct().ToList());
        }).OrderBy(a => a.Path).ToList();
    }

    string? MapTarget(string path) => _cfg.DeploymentMap
        .Where(m => path.StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(m => m.Prefix.Length).FirstOrDefault()?.Target;

    // Returns (files, problem) - problem is null when the card has passing evidence
    public (List<string> Files, string? Problem) FindTests(string key)
    {
        if (!Directory.Exists(_cfg.TestResultsDir)) return (new(), $"test results dir not found: {_cfg.TestResultsDir}");
        var files = Directory.GetFiles(_cfg.TestResultsDir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith(key, StringComparison.OrdinalIgnoreCase)
                     || Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar).Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0) return (files, "no test results found");
        foreach (var trx in files.Where(f => f.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)))
        {
            var m = Regex.Match(File.ReadAllText(trx), "failed=\"(\\d+)\"");
            if (m.Success && int.Parse(m.Groups[1].Value) > 0) return (files, $"failing tests in {Path.GetFileName(trx)}");
        }
        return (files, null);
    }

    public string Build(List<Card> cards, List<Artifact> artifacts, Dictionary<string, List<string>> tests, List<(string Key, string Reason)> excluded)
    {
        Directory.CreateDirectory(_cfg.OutputDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(_cfg.OutputDir, $"release-{stamp}.zip");

        var manifest = new
        {
            generatedAtUtc = DateTime.UtcNow,
            repoHead = _git.Head(),
            cards = cards.Select(c => new { c.Key, c.Summary, testResults = tests[c.Key].Select(Path.GetFileName) }),
            artifacts = artifacts.Select(a => new { a.Path, a.Sha, a.Date, a.Deleted, a.Target, a.CardKeys, a.SupersededShas }),
            excludedCards = excluded.Select(e => new { e.Key, e.Reason })
        };

        using var fs = new FileStream(zipPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        void Add(string name, byte[] data)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(data);
        }

        Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var a in artifacts.Where(a => !a.Deleted))
            Add($"artifacts/{a.Target}/{a.Path}", _git.ShowFile(a.Sha, a.Path));
        foreach (var (key, files) in tests)
            foreach (var f in files) Add($"tests/{key}/{Path.GetFileName(f)}", File.ReadAllBytes(f));
        return zipPath;
    }
}
