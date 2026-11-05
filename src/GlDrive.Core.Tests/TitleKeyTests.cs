using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class TitleKeyTests
{
    [Theory]
    [InlineData("The Office (US)", "theofficeus")]
    [InlineData("Marvel's Agents of S.H.I.E.L.D.", "marvelsagentsofshield")]
    [InlineData("Love Island: USA 2026", "loveislandusa2026")]
    [InlineData("  ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Ascii_titles_keep_their_lower_case_letters_and_digits(string? title, string expected)
        => Assert.Equal(expected, TitleKey.Normalize(title));

    [Theory]
    [InlineData("ＦＵＬＬ　ＷＩＤＴＨ", "fullwidth")]
    [InlineData("Ｓｉｎｎｅｒｓ ２０２５", "sinners2025")]
    [InlineData("The ﬁnal ﬂight", "thefinalflight")]
    [InlineData("Ⅻ Monkeys", "xiimonkeys")]
    public void Compatibility_forms_fold_to_plain_letters(string title, string expected)
        => Assert.Equal(expected, TitleKey.Normalize(title));

    [Fact]
    public void Composed_and_decomposed_accents_give_one_key()
    {
        const string composed = "Amélie";
        const string decomposed = "Amélie";
        Assert.Equal("amélie", TitleKey.Normalize(composed));
        Assert.Equal(TitleKey.Normalize(composed), TitleKey.Normalize(decomposed));
    }

    [Fact]
    public void Leading_articles_are_part_of_the_key()
        => Assert.NotEqual(TitleKey.Normalize("The Bear"), TitleKey.Normalize("Bear"));
}
