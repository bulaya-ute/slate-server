using Slate.Server.Notes;

namespace Slate.Server.Tests;

/// <summary>
/// Pure unit tests for <see cref="MarkdownIndexer"/> - no DB/TestApp needed, since the indexer is a
/// stateless function of its input text.
/// </summary>
public class MarkdownIndexerTests
{
    [Fact]
    public void Extract_TitleFromFirstH1Heading()
    {
        var result = MarkdownIndexer.Extract("# My Title\n\nSome body text.\n\n## Subheading\n", "fallback");
        Assert.Equal("My Title", result.Title);
    }

    [Fact]
    public void Extract_NoHeading_FallsBackToProvidedTitle()
    {
        var result = MarkdownIndexer.Extract("Just a paragraph, no heading.", "my-note");
        Assert.Equal("my-note", result.Title);
    }

    [Fact]
    public void Extract_IgnoresH2AndDeeperHeadingsForTitle()
    {
        var result = MarkdownIndexer.Extract("## Not the title\n\n# Actual Title\n", "fallback");
        Assert.Equal("Actual Title", result.Title);
    }

    [Fact]
    public void Extract_InlineTags_AreFound()
    {
        var result = MarkdownIndexer.Extract("Some text with #project and #area/work tags.", "fallback");
        Assert.Contains("project", result.Tags);
        Assert.Contains("area/work", result.Tags);
    }

    [Fact]
    public void Extract_TagsInsideFencedCodeBlock_AreIgnored()
    {
        const string content = """
            Real tag here: #keep

            ```
            # This is a comment, not a heading
            some_var = "#notatag"
            ```

            More text #alsokeep.
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Contains("keep", result.Tags);
        Assert.Contains("alsokeep", result.Tags);
        Assert.DoesNotContain("notatag", result.Tags);
    }

    [Fact]
    public void Extract_TagsInsideTildeFencedCodeBlock_AreIgnored()
    {
        const string content = """
            ~~~
            #ignored
            ~~~
            #real
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.DoesNotContain("ignored", result.Tags);
        Assert.Contains("real", result.Tags);
    }

    [Fact]
    public void Extract_DoesNotTreatHeadingHashAsTag()
    {
        var result = MarkdownIndexer.Extract("# Heading One\n\nBody #tag1 text.", "fallback");
        Assert.DoesNotContain("Heading", result.Tags);
        Assert.Contains("tag1", result.Tags);
    }

    [Fact]
    public void Extract_FrontmatterTags_BracketList()
    {
        const string content = """
            ---
            title: Something
            tags: [alpha, beta, gamma]
            ---
            Body text.
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Contains("alpha", result.Tags);
        Assert.Contains("beta", result.Tags);
        Assert.Contains("gamma", result.Tags);
    }

    [Fact]
    public void Extract_FrontmatterTags_YamlBlockList()
    {
        const string content = """
            ---
            tags:
              - alpha
              - beta
            other: value
            ---
            Body text.
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Contains("alpha", result.Tags);
        Assert.Contains("beta", result.Tags);
        Assert.Equal(2, result.Tags.Count);
    }

    [Fact]
    public void Extract_FrontmatterTags_ScalarValue()
    {
        const string content = """
            ---
            tags: solo-tag
            ---
            Body text.
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Single(result.Tags);
        Assert.Contains("solo-tag", result.Tags);
    }

    [Fact]
    public void Extract_FrontmatterAndInlineTags_AreMergedAndDeduplicated()
    {
        const string content = """
            ---
            tags: [shared, fromfrontmatter]
            ---
            Body with #shared and #inline tags.
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Contains("shared", result.Tags);
        Assert.Contains("fromfrontmatter", result.Tags);
        Assert.Contains("inline", result.Tags);
        Assert.Equal(3, result.Tags.Count);
    }

    [Fact]
    public void Extract_SimpleWikilink()
    {
        var result = MarkdownIndexer.Extract("See [[Other Note]] for more.", "fallback");
        var link = Assert.Single(result.Links);
        Assert.Equal("Other Note", link.Target);
        Assert.Null(link.Alias);
        Assert.False(link.IsEmbed);
    }

    [Fact]
    public void Extract_WikilinkWithAlias()
    {
        var result = MarkdownIndexer.Extract("See [[folder/note|a nicer name]] for more.", "fallback");
        var link = Assert.Single(result.Links);
        Assert.Equal("folder/note", link.Target);
        Assert.Equal("a nicer name", link.Alias);
        Assert.False(link.IsEmbed);
    }

    [Fact]
    public void Extract_EmbedLink()
    {
        var result = MarkdownIndexer.Extract("Here's an image: ![[diagram.png]]", "fallback");
        var link = Assert.Single(result.Links);
        Assert.Equal("diagram.png", link.Target);
        Assert.Null(link.Alias);
        Assert.True(link.IsEmbed);
    }

    [Fact]
    public void Extract_AllThreeLinkForms_TogetherInOneNote()
    {
        const string content = """
            [[Simple Link]]
            [[target|aliased]]
            ![[embedded.png]]
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        Assert.Equal(3, result.Links.Count);
        Assert.Contains(result.Links, l => l.Target == "Simple Link" && l.Alias == null && !l.IsEmbed);
        Assert.Contains(result.Links, l => l.Target == "target" && l.Alias == "aliased" && !l.IsEmbed);
        Assert.Contains(result.Links, l => l.Target == "embedded.png" && l.Alias == null && l.IsEmbed);
    }

    [Fact]
    public void Extract_LinksInsideFencedCodeBlock_AreIgnored()
    {
        const string content = """
            Real: [[Real Note]]

            ```
            [[Not A Link]]
            ```
            """;

        var result = MarkdownIndexer.Extract(content, "fallback");

        var link = Assert.Single(result.Links);
        Assert.Equal("Real Note", link.Target);
    }

    [Fact]
    public void StripToPlainText_RemovesFrontmatterAndMarkdownSyntax()
    {
        const string content = """
            ---
            tags: [x]
            ---
            # Heading Word

            Some **bold** and _italic_ text with a [[Wiki Link]] and a [label](https://example.com).
            """;

        var plain = MarkdownIndexer.StripToPlainText(content);

        Assert.DoesNotContain("---", plain);
        Assert.DoesNotContain("tags:", plain);
        Assert.Contains("Heading Word", plain);
        Assert.Contains("bold", plain);
        Assert.Contains("italic", plain);
        Assert.Contains("Wiki Link", plain);
        Assert.Contains("label", plain);
        Assert.DoesNotContain("https://example.com", plain);
    }
}
