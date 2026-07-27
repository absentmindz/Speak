using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class LocalTextFormatterTests
{
    private static readonly DictationMode SmartMode = new() { Id = "smart" };

    [Fact]
    public void SmartFormattingPreservesIntentionalTechnicalCasing()
    {
        var formatter = new LocalTextFormatter();

        string formatted = formatter.Format(
            @"use PowerShell with the API at C:\Temp\MyTool and https://Example.test/CaseSensitivePath",
            SmartMode,
            Array.Empty<VocabularyEntry>());

        Assert.Equal(
            @"Use PowerShell with the API at C:\Temp\MyTool and https://Example.test/CaseSensitivePath.",
            formatted);
    }

    [Fact]
    public void SmartFormattingNormalizesFirstPersonPronounsWithoutLowercasingOtherWords()
    {
        var formatter = new LocalTextFormatter();

        string formatted = formatter.Format(
            "i know GitHub and i've tested JSON",
            SmartMode,
            Array.Empty<VocabularyEntry>());

        Assert.Equal("I know GitHub and I've tested JSON.", formatted);
    }

    [Theory]
    [InlineData("$1", "dollar one", "send dollar one", "Send $1.")]
    [InlineData(
        @"C:\$cache\1",
        "cache path",
        "open cache path",
        @"Open C:\$cache\1.")]
    [InlineData("${HOME}", "home variable", "use home variable", "Use ${HOME}.")]
    public void VocabularyWritesReplacementLiterally(
        string written,
        string spoken,
        string input,
        string expected)
    {
        var formatter = new LocalTextFormatter();
        var vocabulary = new[]
        {
            new VocabularyEntry
            {
                Spoken = spoken,
                Written = written
            }
        };

        string formatted = formatter.Format(input, SmartMode, vocabulary);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void VocabularyMatchesSpokenSeparatorAliasesAndPreservesLiteralReplacement()
    {
        string formatted = LocalTextFormatter.ApplyVocabulary(
            "contact dev at example dot test",
            new[]
            {
                new VocabularyEntry
                {
                    Spoken = "dev at example dot test",
                    Written = "dev+$1@example.test"
                }
            });

        Assert.Equal("contact dev+$1@example.test", formatted);
    }
}
