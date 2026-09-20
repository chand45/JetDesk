using System.Text.RegularExpressions;

namespace JetDesk;

/// <summary>Constructs strings in ordinary code; Jev can select them but never invent text.</summary>
public static class TextCandidates
{
    public const int MaximumCandidates = 180;
    private static readonly Regex Words = new(@"\S+", RegexOptions.Compiled);
    private static readonly HashSet<string> GenericLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "home", "back", "forward", "next", "previous", "play", "pause", "stop",
        "close", "minimize", "maximize", "restore", "settings", "menu", "file", "edit", "view",
        "help", "submit", "cancel", "ok", "yes", "no", "button", "window", "desktop"
    };

    public static List<TextCandidate> Build(string goal, Snapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(goal)) return [];
        var result = new List<TextCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value, string source)
        {
            var text = value.Trim();
            if (result.Count >= MaximumCandidates || text.Length is 0 or > 1000 ||
                text.Any(c => c == '\0' || c == '\r' || c == '\n') || !seen.Add(text)) return;
            result.Add(new($"t{result.Count:D3}", text, source));
        }

        // Exact quoted strings outrank heuristic phrases, including spaces and punctuation.
        foreach (Match match in Regex.Matches(goal, "[\"“]([^\"”]+)[\"”]|(?<![\\p{L}\\p{N}])'([^']+)'"))
            Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, "quoted user text");

        // Strip navigation wording with explicit rules, not language generation.
        foreach (Match match in Regex.Matches(goal,
                     @"(?:\b(?:search(?:\s+for)?|find|play|type|enter|look\s+up)\s+)(.+?)(?=\s+(?:on|in|using)\s+(?:spotify|youtube|google|bing|edge|chrome|the\s+browser)\b|\s+(?:and\s+then|then|and\s+(?:play|click|open|select|press|find|search))\b|$)",
                     RegexOptions.IgnoreCase))
            Add(match.Groups[1].Value.Trim('"', '“', '”', '\''), "request phrase after an action verb");

        // A path/address explicitly supplied by the user remains an exact string.
        foreach (Match match in Regex.Matches(goal, @"https?://[^\s<>""“”]+|[A-Za-z]:\\[^\r\n""“”]+"))
            Add(match.Value, "literal path or URL in request");

        // Reserve screen-derived candidates before spans can consume the budget.
        var goalWords = TokenSet(goal);
        var screenNames = snapshot.Controls.Where(c => !c.IsPassword && c.Name.Length is >= 3 and <= 160)
            .Where(c => !GenericLabels.Contains(c.Name.Trim()))
            .OrderByDescending(c => TokenSet(c.Name).Intersect(goalWords).Count())
            .ThenByDescending(c => c.Role.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
                                   c.Role.Contains("Item", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name.Trim()).Distinct(StringComparer.Ordinal).Take(24).ToList();
        foreach (var name in screenNames) Add(name, "observed screen label");

        // Templates have fixed syntax. Values must come from the request or this observation.
        var templateEntities = result.Where(c => c.Source != "literal path or URL in request")
            .Select(c => c.Text).Take(32).ToArray();
        if (Regex.IsMatch(goal, @"\bmanual\b", RegexOptions.IgnoreCase))
            foreach (var entity in templateEntities) Add(entity + " manual", "fixed manual-search template");
        else if (Regex.IsMatch(goal, @"\b(?:documentation|docs)\b", RegexOptions.IgnoreCase))
            foreach (var entity in templateEntities) Add(entity + " documentation", "fixed documentation-search template");

        // Scan every start position for each length. Two-to-four word names get priority,
        // avoiding the common bug where long goals exhaust the budget on single words.
        var words = Words.Matches(goal).Select(m => m.Value).Take(100).ToArray();
        int[] lengths = [2, 3, 4, 1, 5, 6, 7, 8, 9, 10, 11, 12];
        foreach (var length in lengths)
            for (var start = 0; start + length <= words.Length; start++)
                Add(string.Join(" ", words.Skip(start).Take(length)).Trim('"', '“', '”', '\''),
                    "contiguous phrase from request");
        Add(goal, "complete request (use only for explicit literal transcription)");
        return result;
    }

    internal static HashSet<string> TokenSet(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).Where(s => s.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
