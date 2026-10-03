using System.IO.Compression;
using System.Text;
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

    Config.MapRule? Rule(string path) => _cfg.DeploymentMap
        .Where(m => path.StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(m => m.Prefix.Length).FirstOrDefault();

    string? MapTarget(string path) => Rule(path)?.Target;

    // Production destination = rule.ProdPath + path relative to rule.Prefix
    string Dest(Artifact a)
    {
        var r = Rule(a.Path);
        if (r == null) return "";
        var rel = a.Path[r.Prefix.Length..];
        if (string.IsNullOrWhiteSpace(r.ProdPath)) return $"<set ProdPath for '{r.Target}' in appsettings.json>/{rel}";
        var sep = r.ProdPath.Contains('\\') ? '\\' : '/';
        return r.ProdPath.TrimEnd('/', '\\') + sep + rel.Replace('/', sep);
    }

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

        var head = _git.Head();
        var manifest = new
        {
            generatedAtUtc = DateTime.UtcNow,
            repoHead = head,
            cards = cards.Select(c => new { c.Key, c.Summary, testResults = tests[c.Key].Select(Path.GetFileName) }),
            artifacts = artifacts.Select(a => new { a.Path, a.Sha, a.Date, a.Deleted, a.Target, prodDestination = Dest(a), a.CardKeys, a.SupersededShas }),
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
        Add("DeploymentDocument.md", Encoding.UTF8.GetBytes(BuildDeploymentDoc(cards, artifacts, tests, excluded, head, Path.GetFileName(zipPath))));
        foreach (var a in artifacts.Where(a => !a.Deleted))
            Add($"artifacts/{a.Target}/{a.Path}", _git.ShowFile(a.Sha, a.Path));
        foreach (var (key, files) in tests)
            foreach (var f in files) Add($"tests/{key}/{Path.GetFileName(f)}", File.ReadAllBytes(f));
        return zipPath;
    }

    string BuildDeploymentDoc(List<Card> cards, List<Artifact> artifacts, Dictionary<string, List<string>> tests,
        List<(string Key, string Reason)> excluded, string head, string zipName)
    {
        var sb = new StringBuilder();
        var live = artifacts.Where(a => !a.Deleted).ToList();
        var gone = artifacts.Where(a => a.Deleted).ToList();
        sb.AppendLine("# Deployment Document\n");
        sb.AppendLine($"- **Generated (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Package:** {zipName}");
        sb.AppendLine($"- **Source:** {(_cfg.Git.RemoteUrl == "" ? _cfg.Git.RepoPath : _cfg.Git.RemoteUrl)} (branch `{_cfg.Git.Branch}`, commit `{head}`)");
        sb.AppendLine($"- **Files to copy:** {live.Count}  |  **Files to remove:** {gone.Count}\n");

        sb.AppendLine("## 1. Cards in this release\n");
        sb.AppendLine("| Card | Summary | Test evidence |\n|---|---|---|");
        foreach (var c in cards)
            sb.AppendLine($"| {c.Key} | {c.Summary.Replace("|", "/")} | {string.Join(", ", tests[c.Key].Select(Path.GetFileName))} |");

        sb.AppendLine("\n## 2. Before you start\n");
        sb.AppendLine("1. Get change approval and a deployment window.");
        sb.AppendLine($"2. Extract `{zipName}` to a working folder on the deployment machine. Below, `<EXTRACT>` means that folder.");
        sb.AppendLine("3. Back up every destination file or folder listed in section 3 (copy to a dated backup folder).");
        sb.AppendLine("4. Stop the affected application/service if your process requires it.\n");

        sb.AppendLine("## 3. Files to copy to production\n");
        sb.AppendLine("Copy each source (inside the extracted package) to the destination, overwriting any existing file.\n");
        sb.AppendLine("| # | Source (in package) | Destination (production) | Commit | Cards |\n|---|---|---|---|---|");
        int n = 1;
        foreach (var a in live)
            sb.AppendLine($"| {n++} | `<EXTRACT>/artifacts/{a.Target}/{a.Path}` | `{Dest(a)}` | {a.Sha[..8]} | {string.Join(", ", a.CardKeys)} |");

        sb.AppendLine("\n## 4. Files to remove from production\n");
        if (gone.Count == 0) sb.AppendLine("None.");
        else foreach (var a in gone) sb.AppendLine($"- `{Dest(a)}` (deleted in commit {a.Sha[..8]}, cards: {string.Join(", ", a.CardKeys)})");

        sb.AppendLine("\n## 5. After copying\n");
        sb.AppendLine("1. Restart / recycle the application or service if required.");
        sb.AppendLine("2. Verify each card below, using its test evidence in `<EXTRACT>/tests/<card>/` as the reference:\n");
        foreach (var c in cards) sb.AppendLine($"   - [ ] {c.Key}: {c.Summary}");
        sb.AppendLine("\n## 6. Rollback\n");
        sb.AppendLine("Stop the service, restore the backed-up files from step 2.3, delete any newly added files, and restart.");

        if (excluded.Count > 0)
        {
            sb.AppendLine("\n## 7. Cards NOT included (resolve before releasing them)\n");
            foreach (var (k, r) in excluded) sb.AppendLine($"- {k}: {r}");
        }
        sb.AppendLine("\n## Sign-off\n");
        sb.AppendLine("| Role | Name | Date | Signature |\n|---|---|---|---|\n| Deployed by | | | |\n| Verified by | | | |");
        return sb.ToString();
    }
}
