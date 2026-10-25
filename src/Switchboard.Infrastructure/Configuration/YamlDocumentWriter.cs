using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Switchboard.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using CommentToken = YamlDotNet.Core.Tokens.Comment;
using ScalarEvent = YamlDotNet.Core.Events.Scalar;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>
/// Writes a configuration tree as the setup wizard saves it: block style, two-space indent, the
/// tree's own key order, quoted scalars double-quoted. Comments of a hand-written file cannot be
/// carried through a structured write; the file starts with a fixed header that says so, and
/// <see cref="HasComments"/> recognises exactly that header, so a file the wizard wrote does not ask
/// for the comments acknowledgement again.
/// </summary>
public static partial class YamlDocumentWriter
{
    // A layout bound, not a tuning knob: long entity IDs and names stay on one line.
    private const int LineWidth = 1000;

    /// <summary>The generated header, line by line; it names no time or backup, so it is recognisable exactly.</summary>
    public static ImmutableArray<string> HeaderLines { get; } =
    [
        "# Written by the Switchboard setup wizard. Comments are not kept when the wizard saves this file;",
        "# the previous version is kept next to it as switchboard.yaml.<time>.bak. Editing by hand is fine.",
    ];

    public static string Write(ConfigNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var text = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(text, new EmitterSettings(2, LineWidth, false, 1024, false, true, "\n"));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));
        Emit(emitter, root);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        return string.Join("\n", HeaderLines) + "\n\n" + text.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>True when the text holds a comment other than the generated header (a structured save would drop it).</summary>
    public static bool HasComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = text.ReplaceLineEndings("\n").TrimStart('﻿');
        var header = string.Join("\n", HeaderLines) + "\n";
        if (body.StartsWith(header, StringComparison.Ordinal))
        {
            body = body[header.Length..];
        }

        try
        {
            var scanner = new Scanner(new StringReader(body), skipComments: false);
            while (scanner.MoveNext())
            {
                if (scanner.Current is CommentToken)
                {
                    return true;
                }
            }

            return false;
        }
        catch (YamlException)
        {
            // Not readable YAML: any line that looks like a comment counts.
            return CommentLine().IsMatch(body);
        }
    }

    private static void Emit(IEmitter emitter, ConfigNode node)
    {
        switch (node)
        {
            case ConfigMapping mapping:
                emitter.Emit(new MappingStart(null, null, true, mapping.Entries.IsEmpty ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var entry in mapping.Entries)
                {
                    emitter.Emit(Scalar(entry.Key, Application.Setup.ConfigTreeText.NeedsQuotes(entry.Key)));
                    Emit(emitter, entry.Value);
                }

                emitter.Emit(new MappingEnd());
                break;
            case ConfigSequence sequence:
                emitter.Emit(new SequenceStart(null, null, true, sequence.Items.IsEmpty ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in sequence.Items)
                {
                    Emit(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case ConfigScalar scalar:
                // An unquoted empty value means null; "~" keeps that meaning in any YAML reader.
                emitter.Emit(scalar.Quoted ? Scalar(scalar.Value, true) : Scalar(scalar.Value.Length == 0 ? "~" : scalar.Value, false));
                break;
        }
    }

    private static ScalarEvent Scalar(string value, bool quoted) =>
        new(null, null, value, quoted ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain, true, true);

    [GeneratedRegex(@"(^|\s)#", RegexOptions.Multiline)]
    private static partial Regex CommentLine();
}
