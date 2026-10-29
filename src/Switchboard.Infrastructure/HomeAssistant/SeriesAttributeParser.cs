using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Reads the one whitelisted attribute of an entity as a series (15-PLANNER, "What the signal layer needs that it does not have").
/// Bounded and strict: more than <see cref="TimeSeries.MaxPoints"/> points or <see cref="TimeSeries.MaxBytes"/> bytes drops the whole
/// frame (never a truncated series), and one malformed item refuses the whole series (never a series with a hole in it). Times must
/// carry a UTC offset, unless <c>localZone</c> is given (the add-on's zone, the one of the slot grid): then a time without one is wall-clock time in that
/// zone (Forecast.Solar keys, <c>2026-10-29 07:00:00</c>); without a zone it is refused, never guessed. The result is sorted by start; numbers are raw, the signal layer
/// gives them their unit.
/// </summary>
public static class SeriesAttributeParser
{
    public static ParsedSeries Parse(JsonElement attribute, SeriesSpec spec, TimeZoneInfo? localZone = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var shape = attribute.ValueKind switch
        {
            JsonValueKind.Array => SeriesShape.List,
            JsonValueKind.Object => SeriesShape.Dict,
            _ => (SeriesShape?)null,
        };
        if (shape is null)
        {
            return ParsedSeries.Of(SeriesProblem.Missing);
        }

        if (spec.Shape != SeriesShape.Auto && spec.Shape != shape)
        {
            return ParsedSeries.Of(SeriesProblem.Malformed);
        }

        var count = shape == SeriesShape.List ? attribute.GetArrayLength() : attribute.GetPropertyCount();
        if (count == 0)
        {
            return ParsedSeries.Of(SeriesProblem.Missing);
        }

        if (count > TimeSeries.MaxPoints || Encoding.UTF8.GetByteCount(attribute.GetRawText()) > TimeSeries.MaxBytes)
        {
            return ParsedSeries.Of(SeriesProblem.TooLarge);
        }

        var points = new List<SeriesPoint>(count);
        if (shape == SeriesShape.List)
        {
            foreach (var item in attribute.EnumerateArray())
            {
                if (ListPoint(item, spec, localZone) is not { } point)
                {
                    return ParsedSeries.Of(SeriesProblem.Malformed);
                }

                points.Add(point);
            }
        }
        else
        {
            foreach (var item in attribute.EnumerateObject())
            {
                if (!IsoInstant.TryParse(item.Name, localZone, out var start) || Number(item.Value) is not { } value)
                {
                    return ParsedSeries.Of(SeriesProblem.Malformed);
                }

                points.Add(new SeriesPoint(start, null, value));
            }
        }

        points.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 0; i < points.Count; i++)
        {
            if ((i > 0 && points[i].Start == points[i - 1].Start) || points[i].End is { } end && end <= points[i].Start)
            {
                return ParsedSeries.Of(SeriesProblem.Malformed);
            }
        }

        return new ParsedSeries([.. points], SeriesProblem.None);
    }

    private static SeriesPoint? ListPoint(JsonElement item, SeriesSpec spec, TimeZoneInfo? localZone)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty(spec.StartKey, out var startElement)
            || startElement.ValueKind != JsonValueKind.String
            || !IsoInstant.TryParse(startElement.GetString(), localZone, out var start)
            || !item.TryGetProperty(spec.ValueKey, out var valueElement)
            || Number(valueElement) is not { } value)
        {
            return null;
        }

        DateTimeOffset? end = null;
        if (spec.EndKey is { } endKey && item.TryGetProperty(endKey, out var endElement) && endElement.ValueKind != JsonValueKind.Null)
        {
            if (endElement.ValueKind != JsonValueKind.String || !IsoInstant.TryParse(endElement.GetString(), localZone, out var parsed))
            {
                return null;
            }

            end = parsed;
        }

        return new SeriesPoint(start, end, value);
    }

    private static double? Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
}
