using Switchboard.Application.Configuration;
using Switchboard.Infrastructure.Configuration;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The configuration document is untrusted: only plain data may get through the reader. Aliases
/// expand exponentially, tags coerce types, duplicate keys make the winner undefined.
/// </summary>
public sealed class YamlDocumentReaderTests
{
    [Fact]
    public void Plain_document_keeps_positions_and_quoting()
    {
        var result = YamlDocumentReader.Read("a: 1\nb:\n  c: \"null\"\n  d: ~\nlist: [x, y]\n", ConfigOrigin.Document);

        var root = Assert.IsType<ConfigMapping>(result.Root);
        var b = Assert.IsType<ConfigMapping>(root.Find("b")!.Value);
        var c = Assert.IsType<ConfigScalar>(b.Find("c")!.Value);
        Assert.Equal(("null", true, false), (c.Value, c.Quoted, c.IsNull));
        Assert.True(Assert.IsType<ConfigScalar>(b.Find("d")!.Value).IsNull);
        Assert.Equal((3, 6), (c.Line, c.Column));
        Assert.Equal(2, Assert.IsType<ConfigSequence>(root.Find("list")!.Value).Items.Length);
        Assert.Equal((2, 1), (root.Find("b")!.Line, root.Find("b")!.Column));
    }

    [Theory]
    [InlineData("base: &b { x: 1 }\nother: *b\n", "yaml_alias_not_allowed", 1)]
    [InlineData("a: !!str 5\n", "yaml_tag_not_allowed", 1)]
    [InlineData("a: 1\nb: 2\na: 3\n", "duplicate_key", 3)]
    [InlineData("a: 1\n---\nb: 2\n", "yaml_multiple_documents", 2)]
    [InlineData("a: [1, 2\n", "yaml_syntax", null)]
    public void Aliases_and_tags_refused_in_untrusted_document(string yaml, string code, int? line)
    {
        var result = YamlDocumentReader.Read(yaml, ConfigOrigin.Document);

        Assert.Null(result.Root);
        var issue = Assert.Single(result.Errors);
        Assert.Equal(code, issue.Code);
        if (line is not null)
        {
            Assert.Equal(line, issue.Line);
        }
    }

    [Fact]
    public void Absurd_nesting_is_refused_before_anything_recurses_over_it()
    {
        var yaml = new string('[', 200) + new string(']', 200);

        Assert.Equal("yaml_syntax", Assert.Single(YamlDocumentReader.Read(yaml, ConfigOrigin.Document).Errors).Code);
    }

    [Fact]
    public void Empty_document_has_no_root_and_no_error()
    {
        var result = YamlDocumentReader.Read("# only a comment\n", ConfigOrigin.Document);

        Assert.Null(result.Root);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Shipped_defaults_embedded_and_valid()
    {
        var defaults = EmbeddedControlDefaults.Load();

        var result = SwitchboardDocumentParser.Parse(
            YamlDocumentReader.Read(Support.TestConfigurations.ThreePhase(), ConfigOrigin.Document).Root,
            defaults,
            "test",
            DateTimeOffset.UnixEpoch);
        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Contains("reserve_derivation:", EmbeddedControlDefaults.Yaml, StringComparison.Ordinal);
    }
}
