using System.Text;
using System.Text.RegularExpressions;

namespace Slate.Server.Notes;

/// <summary>One wikilink/embed found in a note's content, e.g. "[[folder/note|alias]]" or "![[img.png]]".</summary>
public record ExtractedLink(string Target, string? Alias, bool IsEmbed);

/// <summary>Everything the indexer pulls out of a note's raw markdown for a single re-index pass.</summary>
public record MarkdownIndexResult(string Title, IReadOnlyList<string> Tags, IReadOnlyList<ExtractedLink> Links);

/// <summary>
/// Regex-based markdown indexer (design spec "Indexer"): re-extracts title, tags, and wikilinks from
/// a note's raw content on every write. Deliberately regex/line-scanning based rather than a full
/// markdown parser - the extraction rules are simple and the corpus (user notes) is untrusted-but-not-
/// adversarial, so a lightweight approach keeps this dependency-free and fast.
///
/// Frontmatter and fenced code blocks are stripped before title/tag/link scanning: a "#" inside a
/// ```code``` block is source, not a heading or tag, and frontmatter's own "tags:" key is parsed
/// separately (see <see cref="ExtractFrontmatterTags"/>) rather than scanned for inline "#tag" syntax.
/// </summary>
public static class MarkdownIndexer
{
    private static readonly Regex HeadingPattern = new(@"^#[ \t]+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Requires the character right before "#" to not be a word character or another "#" (so "##" H2
    // headings and mid-word "a#b" never match), and the first character after "#" to be a letter
    // (so a numbered heading marker or a URL fragment like "#1" isn't mistaken for a tag).
    private static readonly Regex InlineTagPattern =
        new(@"(?<![\w#])#([\p{L}][\p{L}\p{N}_\-/]*)", RegexOptions.Compiled);

    // Group 1: leading "!" marks an embed. Group 2: target. Group 3: optional alias after "|".
    private static readonly Regex LinkPattern =
        new(@"(!)?\[\[([^\]\|\r\n]+?)(?:\|([^\]\r\n]+))?\]\]", RegexOptions.Compiled);

    private static readonly Regex FrontmatterTagsKey = new(@"^tags:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FrontmatterListItem = new(@"^\s*-\s*(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts title, tags, and links from a note's raw content. <paramref name="fallbackTitle"/>
    /// (typically the note's filename without extension) is used when the content has no top-level
    /// "# " heading.
    /// </summary>
    public static MarkdownIndexResult Extract(string content, string fallbackTitle)
    {
        var (body, frontmatterTags) = ExtractFrontmatter(content);
        var stripped = StripFencedCodeBlocks(body);

        var title = ExtractTitle(stripped) ?? fallbackTitle;
        var tags = MergeTags(ExtractInlineTags(stripped), frontmatterTags);
        var links = ExtractLinks(stripped);

        return new MarkdownIndexResult(title, tags, links);
    }

    /// <summary>
    /// Plain-text rendering of a note's content for full-text indexing (fed to Postgres'
    /// <c>to_tsvector</c>): frontmatter and fenced code blocks removed, wikilink/markdown-link
    /// syntax collapsed to their visible text, and the remaining formatting punctuation stripped so
    /// only words remain.
    /// </summary>
    public static string StripToPlainText(string content)
    {
        var (body, _) = ExtractFrontmatter(content);
        var text = StripFencedCodeBlocks(body);

        // Embeds and wikilinks collapse to their alias (if any) or their raw target text.
        text = Regex.Replace(text, @"!?\[\[([^\]\|\r\n]+?)(?:\|([^\]\r\n]+))?\]\]",
            m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);

        // Markdown links [label](url) collapse to just the label.
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^\)]*\)", "$1");

        // Remaining markdown syntax punctuation - headings, emphasis, inline code, blockquotes,
        // list bullets - is noise for search purposes, so it's replaced with a space (not removed
        // outright) to avoid accidentally gluing adjacent words together.
        text = Regex.Replace(text, @"[`*_>#]", " ");
        text = Regex.Replace(text, @"^[ \t]*-[ \t]+", " ", RegexOptions.Multiline);

        return text;
    }

    private static List<string> MergeTags(IEnumerable<string> inline, IEnumerable<string> frontmatter)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in inline.Concat(frontmatter))
        {
            var normalized = raw.Trim().TrimStart('#').ToLowerInvariant();
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string? ExtractTitle(string text)
    {
        var match = HeadingPattern.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static List<string> ExtractInlineTags(string text)
    {
        var results = new List<string>();
        foreach (Match m in InlineTagPattern.Matches(text))
        {
            results.Add(m.Groups[1].Value);
        }

        return results;
    }

    private static List<ExtractedLink> ExtractLinks(string text)
    {
        var results = new List<ExtractedLink>();
        foreach (Match m in LinkPattern.Matches(text))
        {
            var target = m.Groups[2].Value.Trim();
            if (target.Length == 0)
            {
                continue;
            }

            var alias = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            results.Add(new ExtractedLink(target, alias, IsEmbed: m.Groups[1].Success));
        }

        return results;
    }

    /// <summary>
    /// Splits YAML frontmatter (a "---" line at the very start of the file through the next "---"
    /// line) off the body, returning the body and any tags found under a "tags:" key - either a
    /// bracketed/comma list (<c>tags: [a, b]</c> or <c>tags: a, b</c>) or a YAML block list
    /// (<c>tags:</c> followed by indented "- item" lines). Content without a well-formed frontmatter
    /// block (no opening "---" on line 1, or no closing "---") is returned unchanged.
    /// </summary>
    private static (string Body, List<string> Tags) ExtractFrontmatter(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (content, new List<string>());
        }

        var endLine = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endLine = i;
                break;
            }
        }

        if (endLine == -1)
        {
            return (content, new List<string>());
        }

        var frontmatterLines = lines[1..endLine];
        var tags = ExtractFrontmatterTags(frontmatterLines);
        var body = string.Join('\n', lines[(endLine + 1)..]);
        return (body, tags);
    }

    private static List<string> ExtractFrontmatterTags(string[] frontmatterLines)
    {
        var result = new List<string>();

        for (var i = 0; i < frontmatterLines.Length; i++)
        {
            var match = FrontmatterTagsKey.Match(frontmatterLines[i]);
            if (!match.Success)
            {
                continue;
            }

            var remainder = match.Groups[1].Value.Trim();

            if (remainder.Length == 0)
            {
                // YAML block list on the following indented "- item" lines.
                var j = i + 1;
                while (j < frontmatterLines.Length)
                {
                    var itemMatch = FrontmatterListItem.Match(frontmatterLines[j]);
                    if (!itemMatch.Success)
                    {
                        break;
                    }

                    AddCleanTag(result, itemMatch.Groups[1].Value);
                    j++;
                }
            }
            else
            {
                var inner = remainder.StartsWith('[') ? remainder.Trim('[', ']') : remainder;
                foreach (var item in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    AddCleanTag(result, item);
                }
            }

            break; // Only one "tags:" key is meaningful in a frontmatter block.
        }

        return result;
    }

    private static void AddCleanTag(List<string> result, string raw)
    {
        var cleaned = raw.Trim().Trim('"', '\'').Trim();
        if (cleaned.Length > 0)
        {
            result.Add(cleaned);
        }
    }

    /// <summary>
    /// Removes fenced code blocks (``` or ~~~, 3+ characters, matching CommonMark's rule that the
    /// closing fence must use the same character and be at least as long as the opener) so any "#"
    /// inside sample code is never mistaken for a heading or tag.
    /// </summary>
    private static string StripFencedCodeBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var fenceMatch = Regex.Match(trimmed, "^(`{3,}|~{3,})");

            if (!inFence)
            {
                if (fenceMatch.Success)
                {
                    inFence = true;
                    fenceChar = fenceMatch.Groups[1].Value[0];
                    fenceLen = fenceMatch.Groups[1].Value.Length;
                    continue;
                }

                sb.Append(line).Append('\n');
                continue;
            }

            if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fenceChar && fenceMatch.Groups[1].Value.Length >= fenceLen)
            {
                inFence = false;
            }
            // Content inside the fence (including its closing marker line) is dropped either way.
        }

        return sb.ToString();
    }
}
