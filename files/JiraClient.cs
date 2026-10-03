using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class JiraClient : ICardSource
{
    readonly HttpClient _http;
    readonly Config.JiraCfg _cfg;

    public JiraClient(Config.JiraCfg cfg)
    {
        _cfg = cfg;
        var email = Environment.GetEnvironmentVariable("JIRA_EMAIL") ?? throw new InvalidOperationException("Set JIRA_EMAIL");
        var token = Environment.GetEnvironmentVariable("JIRA_TOKEN") ?? throw new InvalidOperationException("Set JIRA_TOKEN");
        _http = new HttpClient { BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    static Card ToCard(JsonElement i) => new(
        i.GetProperty("key").GetString()!,
        i.GetProperty("fields").GetProperty("summary").GetString() ?? "",
        i.GetProperty("fields").GetProperty("status").GetProperty("name").GetString() ?? "");

    public async Task<List<Card>> GetReadyAsync()
    {
        var jql = $"status = \"{_cfg.ReadyStatus}\"" + (_cfg.ProjectKey == "" ? "" : $" AND project = {_cfg.ProjectKey}") + " ORDER BY key";
        var cards = new List<Card>();
        string? next = null;
        do
        {
            var url = $"rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields=summary,status&maxResults=100" +
                      (next == null ? "" : $"&nextPageToken={Uri.EscapeDataString(next)}");
            using var doc = JsonDocument.Parse(await GetAsync(url));
            foreach (var i in doc.RootElement.GetProperty("issues").EnumerateArray()) cards.Add(ToCard(i));
            next = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        } while (next != null);
        return cards;
    }

    public async Task<Card?> GetAsync_Card(string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync($"rest/api/3/issue/{key}?fields=summary,status"));
            return ToCard(doc.RootElement);
        }
        catch (HttpRequestException) { return null; }
    }

    async Task<string> GetAsync(string url)
    {
        var r = await _http.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync();
    }
}
