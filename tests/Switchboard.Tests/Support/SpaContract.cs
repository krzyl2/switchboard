using System.Text.Json;
using System.Text.RegularExpressions;

namespace Switchboard.Tests.Support;

/// <summary>
/// The SPA parses every answer strictly (web/src/api/*.ts): a key it does not know about is ignored,
/// but a key it expects and does not find turns the page "unavailable". These helpers compare the
/// key shapes of real endpoint answers with the committed sample payloads the SPA tests parse, and
/// read the SPA's closed lists. With SWITCHBOARD_WRITE_SPA_SAMPLES=1 a test rewrites its sample from
/// the real serializer first (how the samples were generated).
/// </summary>
internal static partial class SpaContract
{
    public static bool WriteSamples => Environment.GetEnvironmentVariable("SWITCHBOARD_WRITE_SPA_SAMPLES") == "1";

    /// <param name="freeMaps">Paths of objects whose keys are data, not a shape (issue args, per-phase deltas): only their presence is compared.</param>
    public static List<string> Describe(IEnumerable<JsonElement> documents, params string[] freeMaps)
    {
        var shapes = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            Collect(document, "$", shapes, freeMaps);
        }

        return [.. shapes.Select(s => $"{s.Key}: {string.Join(",", s.Value)}")];
    }

    /// <summary>
    /// Writes <paramref name="document"/> as the sample when asked to, then returns the committed
    /// sample. <paramref name="at"/> replaces the answer's <c>at</c>: the test host's signal clock is
    /// the wall clock, while the data is the replay's, and the SPA tests compute ages from <c>at</c>.
    /// </summary>
    public static JsonElement Sample(string path, JsonElement document, DateTimeOffset? at = null)
    {
        if (WriteSamples)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(document.GetRawText())!;
            if (at is { } instant)
            {
                node["at"] = instant;
            }

            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }

        using var sample = JsonDocument.Parse(File.ReadAllText(path));
        return sample.RootElement.Clone();
    }

    public static string[] List(string file, string name)
    {
        var source = File.ReadAllText(file);
        var match = Regex.Match(source, $@"export const {name} = \[(?<items>[^\]]*)\] as const;");
        Assert.True(match.Success, $"{name} not found in {file}.");
        return [.. QuotedItem().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value)];
    }

    private static void Collect(JsonElement element, string path, SortedDictionary<string, string[]> shapes, string[] freeMaps)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when freeMaps.Contains(path):
                shapes[path] = ["<map>"];
                break;
            case JsonValueKind.Object:
                var keys = element.EnumerateObject().Select(p => p.Name).ToArray();
                if (shapes.TryGetValue(path, out var seen))
                {
                    Assert.True(seen.SequenceEqual(keys), $"{path} has two different shapes: {string.Join(",", seen)} / {string.Join(",", keys)}");
                }

                shapes[path] = keys;
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, $"{path}.{property.Name}", shapes, freeMaps);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, path + "[]", shapes, freeMaps);
                }

                break;
        }
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedItem();
}
