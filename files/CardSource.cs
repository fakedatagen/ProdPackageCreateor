public interface ICardSource
{
    Task<List<Card>> GetReadyAsync();
    Task<Card?> GetAsync_Card(string key);
}

// No-API mode: reads a Jira CSV export (columns "Issue key"/"Key", "Summary", "Status") or a plain list of keys, one per line.
public sealed class FileCardSource : ICardSource
{
    readonly List<Card> _cards = new();
    readonly string _ready;

    public FileCardSource(string path, string readyStatus)
    {
        _ready = readyStatus;
        if (!File.Exists(path)) throw new FileNotFoundException($"Cards file not found: {path}. Export your Jira filter to CSV or list card keys one per line.");
        var rows = ParseCsv(File.ReadAllText(path));
        if (rows.Count == 0) return;
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int k = header.FindIndex(h => h is "issue key" or "key");
        if (k < 0) // plain key list
        {
            foreach (var r in rows) if (r.Count > 0 && r[0].Trim() != "") _cards.Add(new Card(r[0].Trim().ToUpperInvariant(), "", readyStatus));
            return;
        }
        int s = header.IndexOf("summary"), st = header.IndexOf("status");
        foreach (var r in rows.Skip(1))
            if (r.Count > k && r[k].Trim() != "")
                _cards.Add(new Card(r[k].Trim(), s >= 0 && r.Count > s ? r[s] : "", st >= 0 && r.Count > st ? r[st] : readyStatus));
    }

    public Task<List<Card>> GetReadyAsync() =>
        Task.FromResult(_cards.Where(c => c.Status.Equals(_ready, StringComparison.OrdinalIgnoreCase)).ToList());

    public Task<Card?> GetAsync_Card(string key) =>
        Task.FromResult<Card?>(_cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                               ?? new Card(key, "(entered manually, not verified)", _ready));

    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cur.ToString()); cur.Clear(); }
            else if (c == '\n' || c == '\r') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cur.ToString()); cur.Clear(); if (row.Any(x => x != "")) rows.Add(row); row = new(); }
            else cur.Append(c);
        }
        row.Add(cur.ToString()); if (row.Any(x => x != "")) rows.Add(row);
        return rows;
    }
}
