using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Switchboard.Infrastructure.Configuration;

public sealed record YamlReadResult(ConfigNode? Root, ImmutableArray<ConfigurationIssue> Errors);

/// <summary>
/// Reads one YAML document into a <see cref="ConfigNode"/> tree with positions. The document is
/// untrusted, so only plain data gets through: anchors and aliases (exponential expansion), tags
/// (type coercion), duplicate keys (which one wins is undefined), several documents in one file and
/// absurd nesting are refused with a precise issue instead of being interpreted.
/// </summary>
public static class YamlDocumentReader
{
    // A parser sanity limit, not a tuning knob: the schema nests five levels deep at most.
    private const int MaxDepth = 32;

    public static YamlReadResult Read(string text, ConfigOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new TreeBuilder(origin);
        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                if (!builder.Accept(parser.Current!))
                {
                    break;
                }
            }
        }
        catch (YamlException ex)
        {
            return new YamlReadResult(null, [Issue(ConfigurationIssueCodes.YamlSyntax, ex.Start, ex.Message)]);
        }

        return builder.Error is { } error ? new YamlReadResult(null, [error]) : new YamlReadResult(builder.Root, []);
    }

    private static ConfigurationIssue Issue(string code, Mark mark, string message) =>
        new(code, string.Empty, (int)mark.Line, (int)mark.Column, message);

    private sealed class TreeBuilder(ConfigOrigin origin)
    {
        private readonly Stack<Frame> _frames = new();
        private int _documents;

        public ConfigNode? Root { get; private set; }

        public ConfigurationIssue? Error { get; private set; }

        /// <summary>Consumes one parser event; false stops reading (an error was recorded).</summary>
        public bool Accept(ParsingEvent parsingEvent)
        {
            switch (parsingEvent)
            {
                case DocumentStart:
                    if (++_documents > 1)
                    {
                        return Fail(ConfigurationIssueCodes.YamlMultipleDocuments, parsingEvent.Start, "The file holds more than one YAML document; keep one.");
                    }

                    return true;

                case AnchorAlias alias:
                    return Fail(ConfigurationIssueCodes.YamlAliasNotAllowed, alias.Start, "Anchors and aliases (& and *) are not allowed; write the value out.");

                case NodeEvent node when !node.Anchor.IsEmpty:
                    return Fail(ConfigurationIssueCodes.YamlAliasNotAllowed, node.Start, "Anchors and aliases (& and *) are not allowed; write the value out.");

                case NodeEvent node when !node.Tag.IsEmpty:
                    return Fail(ConfigurationIssueCodes.YamlTagNotAllowed, node.Start, $"YAML tags ({node.Tag}) are not allowed; write plain values.");

                case MappingStart mapping:
                    return Open(new Frame(true, mapping.Start), mapping.Start);

                case SequenceStart sequence:
                    return Open(new Frame(false, sequence.Start), sequence.Start);

                case MappingEnd or SequenceEnd:
                    var frame = _frames.Pop();
                    var mark = frame.Start;
                    ConfigNode built = frame.IsMapping
                        ? new ConfigMapping(frame.Entries.ToImmutable(), (int)mark.Line, (int)mark.Column, origin)
                        : new ConfigSequence(frame.Items.ToImmutable(), (int)mark.Line, (int)mark.Column, origin);
                    return Attach(built);

                case Scalar scalar:
                    return Attach(new ConfigScalar(
                        scalar.Value,
                        scalar.Style != ScalarStyle.Plain,
                        (int)scalar.Start.Line,
                        (int)scalar.Start.Column,
                        origin));

                default:
                    return true;
            }
        }

        private bool Open(Frame frame, Mark start)
        {
            if (_frames.Count >= MaxDepth)
            {
                return Fail(ConfigurationIssueCodes.YamlSyntax, start, $"The document nests deeper than {MaxDepth} levels.");
            }

            _frames.Push(frame);
            return true;
        }

        private bool Attach(ConfigNode node)
        {
            if (_frames.Count == 0)
            {
                Root = node;
                return true;
            }

            var parent = _frames.Peek();
            if (!parent.IsMapping)
            {
                parent.Items.Add(node);
                return true;
            }

            if (parent.PendingKey is not { } key)
            {
                if (node is not ConfigScalar scalar)
                {
                    return Fail(ConfigurationIssueCodes.WrongType, node, "A key must be plain text.");
                }

                if (!parent.Keys.Add(scalar.Value))
                {
                    return Fail(ConfigurationIssueCodes.DuplicateKey, node, $"Key '{scalar.Value}' appears twice in the same mapping.");
                }

                parent.PendingKey = scalar;
                return true;
            }

            parent.Entries.Add(new ConfigEntry(key.Value, key.Line, key.Column, node));
            parent.PendingKey = null;
            return true;
        }

        private bool Fail(string code, Mark mark, string message)
        {
            Error ??= Issue(code, mark, message);
            return false;
        }

        private bool Fail(string code, ConfigNode at, string message)
        {
            Error ??= new ConfigurationIssue(code, string.Empty, at.Line, at.Column, message);
            return false;
        }

        private sealed class Frame(bool isMapping, Mark start)
        {
            public bool IsMapping { get; } = isMapping;

            public Mark Start { get; } = start;

            public ImmutableArray<ConfigEntry>.Builder Entries { get; } = ImmutableArray.CreateBuilder<ConfigEntry>();

            public ImmutableArray<ConfigNode>.Builder Items { get; } = ImmutableArray.CreateBuilder<ConfigNode>();

            public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

            public ConfigScalar? PendingKey { get; set; }
        }
    }
}
