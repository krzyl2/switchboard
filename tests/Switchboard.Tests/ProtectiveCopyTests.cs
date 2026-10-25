using System.Text.RegularExpressions;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// CLAUDE.md: Switchboard is not a protective device and no copy anywhere may suggest it is. The
/// add-on log is copy too (the user reads it in the Supervisor panel at every start and reload), so
/// no string literal in src/ may say that Switchboard or its guard protects, guarantees or prevents
/// a trip. The UI's texts are held to the same rule by web/src/i18n/i18n.test.ts.
/// </summary>
public sealed partial class ProtectiveCopyTests
{
    [Fact]
    public void No_string_literal_in_src_claims_protection()
    {
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .ToList();
        var offenders = files
            .SelectMany(f => File.ReadLines(f).Select((line, index) => (File: f, Line: index + 1, Text: line)))
            .SelectMany(l => Literal().Matches(l.Text).Where(m => Claim().IsMatch(m.Value)).Select(m => $"{Path.GetFileName(l.File)}:{l.Line} {m.Value}"))
            .ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\b(protects|guarantees|will protect|prevents? (a |the )?trip)", RegexOptions.IgnoreCase)]
    private static partial Regex Claim();
}
