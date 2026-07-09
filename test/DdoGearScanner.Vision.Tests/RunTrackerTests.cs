using System.Collections.Generic;
using DdoGearScanner.Vision;
using OpenCvSharp;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Fixtures are plain text (what OCR of the reward/tracker panels would yield) so the parser can be
// regression-tested without launching DDO. The exact on-screen wording is a first-draft model — as real
// captures are gathered, add them here and tune RunTextParser to match.
public class RunTextParserTests
{
    [Fact]
    public void CleanTrackerNamePicksTheTitleLineOverObjectives()
    {
        string[] lines = { "Tempest's Spine", "Defeat the giant (0/1)", "37%" };
        Assert.Equal("Tempest's Spine", RunTextParser.CleanTrackerName(lines));
    }

    [Fact]
    public void CleanTrackerNameStripsTrailingDifficultyAndReturnsNullForJunk()
    {
        Assert.Equal("The Pit", RunTextParser.CleanTrackerName(new[] { "The Pit Elite" }));
        Assert.Null(RunTextParser.CleanTrackerName(new[] { "(0/3)", "88%", "--" }));
    }
}

public class QuestWikiTests
{
    [Theory]
    [InlineData("The Pit", "https://ddowiki.com/page/The_Pit")]
    [InlineData("Tempest's Spine", "https://ddowiki.com/page/Tempest%27s_Spine")]        // apostrophe URL-encoded
    [InlineData("  Delera's Tomb  ", "https://ddowiki.com/page/Delera%27s_Tomb")]
    [InlineData("THE SMUGGLER'S WAREHOUSE", "https://ddowiki.com/page/The_Smuggler%27s_Warehouse")]  // all-caps → title case
    [InlineData("Ruins of Threnal", "https://ddowiki.com/page/Ruins_of_Threnal")]        // mixed case left alone
    public void BuildsWikiUrl(string name, string expected)
        => Assert.Equal(expected, QuestWiki.Url(name));

    [Fact]
    public void SkipsObjectiveLinesWhenPickingTitle()
    {
        // Zone-load flicker: only a truncated objective fragment is readable → no title yet.
        Assert.Null(RunTextParser.CleanTrackerName(new[] { "to captain in the" }));
        Assert.Null(RunTextParser.CleanTrackerName(new[] { "Talk to Captain Alban Dranmore in the Harbor" }));
        // Title present above objectives → picks the title.
        Assert.Equal("The Smuggler's Warehouse",
            RunTextParser.CleanTrackerName(new[] { "The Smuggler's Warehouse", "Defeat the smugglers (0/6)" }));
    }

    // Build one OCR line at a position. The entry parser reads the popup by GEOMETRY, so tests place
    // lines where they'd actually sit on screen: the popup panel is a vertical stack around x≈300–500;
    // the avatar/health and chat are far to the left (x≈20–120); flavor text sits below the heading.
    private static OcrLine L(string text, int x, int y, int w = 200, int h = 24) => new(text, new Rect(x, y, w, h));

    // A canonical entry popup: name above Level above the "Select Difficulty" heading, Enter/Cancel below.
    private static List<OcrLine> Popup(string name, string levelLine, params OcrLine[] extra)
    {
        var lines = new List<OcrLine>
        {
            L(name, 330, 210),
            L(levelLine, 310, 250, 120),
            L("Duration: short", 310, 274, 140),
            L("Select Difficulty:", 300, 300, 180),
            L("Enter", 300, 380, 60),
            L("Cancel", 410, 380, 70),
        };
        lines.AddRange(extra);
        return lines;
    }

    [Fact]
    public void ParsesQuestNameAndLevelFromEntryPopup()
    {
        var lines = Popup("The Miller's Debt", "Level: 2", L("MILLER TARRIGAN'S HOUSE", 300, 176, 280));
        QuestEntry? entry = RunTextParser.ParseEntry(lines);
        Assert.NotNull(entry);
        Assert.Equal("The Miller's Debt", entry!.Name);
        Assert.Equal(2, entry.QuestLevel);
    }

    [Fact]
    public void EntryParserExcludesTheAvatarNameByPosition()
    {
        // The character name + HP/SP sit far to the LEFT (over the health orb), outside the popup panel.
        var lines = Popup("The Miller's Debt", "Level: 2",
            L("Krak„l Redeye", 40, 214, 180), L("482/482", 50, 250, 90), L("330/330", 50, 274, 90));
        QuestEntry? entry = RunTextParser.ParseEntry(lines);
        Assert.NotNull(entry);
        Assert.Equal("The Miller's Debt", entry!.Name);
    }

    [Fact]
    public void EntryParserSurvivesGarbledLevelAndExcludesFlavorBelowHeading()
    {
        // "10" mis-read "IO"; the region/flavor ("Barovia", "Mists of Ravenloft") is BELOW the heading;
        // the char name is far left. Only the name above the heading, in the panel, should be picked.
        var lines = Popup("Into the Mists", "Level: IO",
            L("OLDSVAilCH ROAD", 300, 176, 260),
            L("HEROIC", 300, 330, 80), L("Mists of Ravenloft", 300, 356, 220), L("Barovia", 320, 380, 100),
            L("Hellzbo.n Red y", 40, 300, 180), L("194/", 50, 330, 60));
        QuestEntry? entry = RunTextParser.ParseEntry(lines);
        Assert.NotNull(entry);
        Assert.Equal("Into the Mists", entry!.Name);
        Assert.Equal(10, entry.QuestLevel);   // "IO" → 10
    }

    [Fact]
    public void EntryParserExcludesChatFragmentsByPosition()
    {
        // Chat "Joining channel: The" fragments are bottom-left, far from the popup → excluded.
        var lines = Popup("Into the Mists", "Level: 10",
            L("Joining channel: The", 20, 600, 220), L("The", 20, 624, 40), L("The", 20, 648, 40));
        QuestEntry? entry = RunTextParser.ParseEntry(lines);
        Assert.NotNull(entry);
        Assert.Equal("Into the Mists", entry!.Name);
    }

    [Fact]
    public void EntryParserReturnsNullWithoutPopupLandmarks()
    {
        // No "Difficulty" heading / Enter+Cancel → not the popup → null (ignores the char XP readout).
        var lines = new List<OcrLine> { L("Level 15 (Rank 74)", 40, 40, 200), L("You killed Ironspike.", 20, 600, 220) };
        Assert.Null(RunTextParser.ParseEntry(lines));
    }

    [Fact]
    public void TrackerCompletionRequiresStatusLineNotChat()
    {
        // "Status: Completed" is one panel line → completed.
        Assert.True(RunTextParser.IsTrackerCompleted(
            new[] { "THE MILLER'S DEBT", "Status: Completed", "Find the miller" }));
        // Chat has "completed" but never on a "Status" line → NOT completed (isolated to the panel).
        Assert.False(RunTextParser.IsTrackerCompleted(
            new[] { "(Advancement): You have completed an objective", "Objective Completed! 0 XP" }));
    }
}
