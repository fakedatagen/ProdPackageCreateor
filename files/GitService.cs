using System.Diagnostics;
using System.Text.RegularExpressions;

public sealed class GitService
{
    readonly string _repo, _ref, _remoteUrl;

    // RemoteUrl set  -> bare cache clone of the remote branch (nothing read from your working copy)
    // RemoteUrl empty -> existing local clone at RepoPath, reading origin/<branch>
    public GitService(Config.GitCfg cfg)
    {
        _remoteUrl = cfg.RemoteUrl;
        if (_remoteUrl != "") { _repo = Path.GetFullPath(cfg.CacheDir); _ref = cfg.Branch; }
        else { _repo = cfg.RepoPath; _ref = "origin/" + cfg.Branch; }
    }

    // Fetch the latest branch state from the remote. Uses your existing git credentials.
    public void Sync()
    {
        if (_remoteUrl == "") { Run("fetch", "origin", _ref.Replace("origin/", "")); return; }
        Directory.CreateDirectory(_repo);
        if (!File.Exists(Path.Combine(_repo, "HEAD"))) Run("clone", "--bare", "--single-branch", "--branch", _ref, _remoteUrl, ".");
        else Run("fetch", "origin", $"+refs/heads/{_ref}:refs/heads/{_ref}");
    }

    ProcessStartInfo Psi(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    string Run(params string[] args)
    {
        using var p = Process.Start(Psi(args))!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {e}");
        return o;
    }

    public string Head() => Run("rev-parse", _ref).Trim();

    // Commits whose message references the card key (e.g. PROJ-123 but not PROJ-1234)
    public List<Change> GetChanges(string cardKey)
    {
        var pattern = $"(^|[^A-Za-z0-9]){Regex.Escape(cardKey)}([^0-9]|$)";
        var output = Run("log", _ref, "-E", $"--grep={pattern}", "-M", "--name-status", "--format=@@%H|%aI|%s");
        var list = new List<Change>();
        string sha = "", subj = ""; DateTimeOffset date = default;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("@@"))
            {
                var parts = line[2..].Split('|', 3);
                sha = parts[0]; date = DateTimeOffset.Parse(parts[1]); subj = parts[2];
                continue;
            }
            var f = line.Split('\t');
            var kind = f[0][0];
            if (kind == 'R' || kind == 'C') list.Add(new Change(sha, date, subj, f[2], 'A', cardKey)); // renamed/copied -> new path
            else list.Add(new Change(sha, date, subj, f[1], kind, cardKey));
        }
        return list;
    }

    public byte[] ShowFile(string sha, string path)
    {
        using var p = Process.Start(Psi(new[] { "show", $"{sha}:{path}" }))!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git show {sha}:{path} failed");
        return ms.ToArray();
    }
}
