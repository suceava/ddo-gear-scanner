using DdoGearScanner.Model;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

// Slug MUST match the web/backend slug() exactly (shared/src/slug.ts) — these cases mirror its behavior.
public class SlugTests
{
    [Theory]
    [InlineData("Lesk Redeye", "lesk-redeye")]
    [InlineData("Lesk", "lesk")]
    [InlineData("Throgar the Bold", "throgar-the-bold")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("a!!b", "a-b")]
    [InlineData("O'Brien", "o-brien")]
    [InlineData("MixedCASE123", "mixedcase123")]
    [InlineData("---trim---", "trim")]
    [InlineData("", "")]
    public void MatchesWebSlug(string input, string expected)
    {
        Assert.Equal(expected, Slug.Of(input));
    }
}
