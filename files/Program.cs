var cfg = Config.Load();
ICardSource jira = cfg.Jira.Mode.Equals("api", StringComparison.OrdinalIgnoreCase)
    ? new JiraClient(cfg.Jira)
    : new FileCardSource(cfg.Jira.CardsFile, cfg.Jira.ReadyStatus);
var git = new GitService(cfg.Git);
Console.WriteLine($"Syncing branch '{cfg.Git.Branch}' from remote...");
git.Sync();
var pack = new Packager(cfg, git);

// 1-2. Connect + get Ready for Deployment cards
Console.WriteLine($"Loading '{cfg.Jira.ReadyStatus}' cards...");
var ready = await jira.GetReadyAsync();
for (int i = 0; i < ready.Count; i++) Console.WriteLine($"  {i + 1,3}. {ready[i].Key,-12} {ready[i].Summary}");

// 3. Menu
Console.WriteLine("\n1) Select All   2) Manual   3) All + Manual");
Console.Write("Choice: ");
var choice = Console.ReadLine()?.Trim();
var keys = new List<string>();
if (choice is "1" or "3") keys.AddRange(ready.Select(c => c.Key));
if (choice == "2")
{
    Console.Write("Numbers from list and/or card keys, comma-separated: ");
    foreach (var t in (Console.ReadLine() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        keys.Add(int.TryParse(t, out var n) && n >= 1 && n <= ready.Count ? ready[n - 1].Key : t.ToUpperInvariant());
}
if (choice == "3")
{
    Console.Write("Extra card keys to add (comma-separated, blank for none): ");
    keys.AddRange((Console.ReadLine() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => k.ToUpperInvariant()));
}
keys = keys.Distinct().ToList();
if (keys.Count == 0) { Console.WriteLine("No cards selected."); return 1; }

// 4-5. Validate cards, search commits, check test evidence (per card, before artifact selection)
var cards = new List<Card>(); var changes = new List<Change>();
var tests = new Dictionary<string, List<string>>(); var excluded = new List<(string, string)>();
foreach (var key in keys)
{
    var card = ready.FirstOrDefault(c => c.Key == key) ?? await jira.GetAsync_Card(key);
    string? problem = null;
    if (card == null) problem = "not found in Jira";
    else if (!card.Status.Equals(cfg.Jira.ReadyStatus, StringComparison.OrdinalIgnoreCase)) problem = $"status is '{card.Status}'";
    List<Change> cardChanges = new();
    List<string> testFiles = new();
    if (problem == null)
    {
        cardChanges = git.GetChanges(key);
        if (cardChanges.Count == 0) problem = "no Git commits reference this card";
    }
    if (problem == null)
    {
        (testFiles, problem) = pack.FindTests(key);
    }
    if (problem != null) { excluded.Add((key, problem)); Console.WriteLine($"  EXCLUDED {key}: {problem}"); continue; }
    cards.Add(card!); changes.AddRange(cardChanges); tests[key] = testFiles;
    Console.WriteLine($"  OK       {key}: {cardChanges.Select(c => c.Sha).Distinct().Count()} commit(s), {testFiles.Count} test file(s)");
}
if (cards.Count == 0) { Console.WriteLine("No valid cards left. Aborting."); return 1; }

// 6-8. Dedupe paths, pick latest version
var artifacts = pack.Resolve(changes);

// 9. Validate deployment mapping (abort if any file is unmapped)
var unmapped = artifacts.Where(a => a.Target == "" && !a.Deleted).ToList();
if (unmapped.Count > 0)
{
    Console.WriteLine("\nUnmapped artifacts (add a DeploymentMap rule):");
    unmapped.ForEach(a => Console.WriteLine($"  {a.Path}  (cards: {string.Join(", ", a.CardKeys)})"));
    return 2;
}

Console.WriteLine($"\n{artifacts.Count} artifact(s) after de-duplication ({artifacts.Count(a => a.Deleted)} deletion(s)).");
Console.Write("Generate package? (y/N): ");
if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 0;

// 10-11. Manifest + ZIP
var zip = pack.Build(cards, artifacts, tests, excluded);
Console.WriteLine($"DONE: {zip}");
return 0;
