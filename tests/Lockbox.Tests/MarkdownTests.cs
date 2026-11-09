using Lockbox.Core.Tools;
using static Lockbox.Core.Tools.MarkdownLite;

namespace Lockbox.Tests;

public class MarkdownTests
{
    [Fact]
    public void Reads_headings_paragraphs_and_lists()
    {
        var blocks = Parse("""
            ## What's new

            Lockbox now imports
            browser passwords.

            - Chrome and Edge
            - Firefox
              - including logins with no site
            1. Open Import
            2. Choose the CSV
            ---
            > Tip: delete the CSV afterwards.
            """);

        Assert.Equal(
            [BlockKind.Heading, BlockKind.Paragraph, BlockKind.Bullet, BlockKind.Bullet, BlockKind.Bullet,
             BlockKind.Numbered, BlockKind.Numbered, BlockKind.Rule, BlockKind.Quote],
            blocks.Select(b => b.Kind));
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal("What's new", blocks[0].PlainText);
        Assert.Equal("Lockbox now imports browser passwords.", blocks[1].PlainText);
        Assert.Equal(0, blocks[3].Level);
        Assert.Equal(1, blocks[4].Level);
        Assert.Equal("2.", blocks[6].Marker);
        Assert.Equal("Tip: delete the CSV afterwards.", blocks[8].PlainText);
    }

    [Fact]
    public void Plain_bullets_written_for_older_versions_read_the_same()
    {
        // Release notes are shown as plain text by 0.5.x and as Markdown from 0.6.0, so simple bullets must work in both.
        var blocks = Parse("- One\n- Two\n- Three");
        Assert.All(blocks, b => Assert.Equal(BlockKind.Bullet, b.Kind));
        Assert.Equal(["One", "Two", "Three"], blocks.Select(b => b.PlainText));
    }

    [Fact]
    public void Wrapped_list_item_lines_join_the_item()
    {
        var blocks = Parse("- A long item that\n  wraps onto the next line\n- Next");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("A long item that wraps onto the next line", blocks[0].PlainText);
    }

    [Fact]
    public void Inline_styles()
    {
        var spans = ParseInline("Press **Ctrl+Alt+A** to *auto-type*, or run `vpk pack`.");
        Assert.Contains(spans, s => s is { Text: "Ctrl+Alt+A", Bold: true });
        Assert.Contains(spans, s => s is { Text: "auto-type", Italic: true });
        Assert.Contains(spans, s => s is { Text: "vpk pack", Code: true });
        Assert.Equal("Press Ctrl+Alt+A to auto-type, or run vpk pack.", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Links_keep_only_their_text()
    {
        var spans = ParseInline("See [the guide](https://example.com/guide) for more.");
        Assert.Equal("See the guide for more.", string.Concat(spans.Select(s => s.Text)));
    }

    [Theory]
    [InlineData("snake_case_name stays as is", "snake_case_name stays as is")]
    [InlineData("5 * 3 = 15", "5 * 3 = 15")]
    [InlineData("an unclosed **bold", "an unclosed **bold")]
    [InlineData(@"literal \*stars\*", "literal *stars*")]
    public void Stray_markers_stay_literal(string input, string expected)
    {
        var spans = ParseInline(input);
        Assert.Equal(expected, string.Concat(spans.Select(s => s.Text)));
        Assert.DoesNotContain(spans, s => s.Bold || s.Italic);
    }

    [Fact]
    public void Code_blocks_keep_their_lines()
    {
        var blocks = Parse("Run:\n```\ngit am --3way x.patch\ngit push\n```\nDone.");
        Assert.Equal([BlockKind.Paragraph, BlockKind.Code, BlockKind.Paragraph], blocks.Select(b => b.Kind));
        Assert.Equal("git am --3way x.patch\ngit push", blocks[1].PlainText);
    }

    [Fact]
    public void Empty_input_gives_no_blocks()
    {
        Assert.Empty(Parse(null));
        Assert.Empty(Parse("  \n\n "));
    }
}
