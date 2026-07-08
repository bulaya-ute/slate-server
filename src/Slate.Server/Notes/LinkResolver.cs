namespace Slate.Server.Notes;

/// <summary>
/// Obsidian-style wikilink target matching: a link's raw target text (e.g. "folder/Note",
/// "Note.md", "note", or "Note#Heading") resolves against a candidate note path case-insensitively,
/// with or without the ".md" extension, and falls back to a basename-only match when the link omits
/// the folder (mirrors Obsidian's "shortest path when possible" resolution). Used both when a note's
/// own outgoing links are (re)indexed and when a newly created/renamed note's path is checked against
/// every vault-wide unresolved link.
/// </summary>
public static class LinkResolver
{
    public static bool Matches(string targetText, string notePath)
    {
        var target = StripFragment(targetText).Trim();
        if (target.Length == 0)
        {
            return false;
        }

        var normalizedTarget = Normalize(StripMdExtension(target));
        var normalizedPath = Normalize(StripMdExtension(notePath));

        if (normalizedTarget == normalizedPath)
        {
            return true;
        }

        var targetBasename = Normalize(Basename(StripMdExtension(target)));
        var pathBasename = Normalize(Basename(StripMdExtension(notePath)));

        return targetBasename == pathBasename;
    }

    /// <summary>Strips an Obsidian heading ("#Heading") or block ("^blockId") reference suffix, if any.</summary>
    private static string StripFragment(string text)
    {
        var index = text.IndexOfAny(new[] { '#', '^' });
        return index >= 0 ? text[..index] : text;
    }

    private static string StripMdExtension(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    private static string Basename(string path)
    {
        var index = path.LastIndexOf('/');
        return index >= 0 ? path[(index + 1)..] : path;
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();
}
