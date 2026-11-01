using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// Which signals exist: the current <see cref="SignalMap"/> and the set of entities the source must
/// deliver. A valid document replaces the map and keeps the runtimes (and so the samples) of signals
/// that read the same entity the same way; an invalid or missing one replaces it with
/// <see cref="SignalMap.Empty"/> and forgets every runtime.
/// </summary>
internal sealed class SignalRegistry
{
    private ImmutableArray<TrackedEntity> _tracked = [];
    private bool _trackedChanged;

    public SignalMap Map { get; private set; } = SignalMap.Empty;

    /// <summary>
    /// Installs the signal set of <paramref name="result"/>. Returns the signals whose reading changed
    /// (kind, unit or sign) with the last raw sample of the entity: the caller reads them again under
    /// the new definition, once the new map is in place. Nothing is returned for an empty map.
    /// </summary>
    public IReadOnlyList<(SignalRuntime Runtime, RawSample Raw)> Replace(ConfigurationResult result)
    {
        if (result.Status != ConfigurationStatus.Valid || result.Configuration is null)
        {
            Map = SignalMap.Empty;
            SetTracked([]);
            return [];
        }

        var runtimes = ImmutableArray.CreateBuilder<SignalRuntime>(result.Configuration.Signals.Length);
        var reread = new List<(SignalRuntime Runtime, RawSample Raw)>();
        foreach (var definition in result.Configuration.Signals)
        {
            // One entity feeds one signal, except the timestamp signals that read different attributes of it (sun.sun's next_rising and next_setting).
            var held = Map.Find(definition.Entity);
            if (held.FirstOrDefault(k => k.ReadsLike(definition)) is { } same)
            {
                same.Definition = definition;
                runtimes.Add(same);
            }
            else
            {
                var runtime = new SignalRuntime(definition);
                runtimes.Add(runtime);
                var kept = held.FirstOrDefault(k => k.Definition.Attribute == definition.Attribute) ?? held.FirstOrDefault();
                if (kept?.Raw is { } raw)
                {
                    // A series or an attribute text read under another spec says nothing under this one.
                    reread.Add((runtime, raw with
                    {
                        Series = kept.Definition.Series == definition.Series ? raw.Series : null,
                        AttributeText = kept.Definition.Attribute == definition.Attribute ? raw.AttributeText : null,
                        Calendar = kept.Definition.Kind == SignalKind.CalendarEvent && definition.Kind == SignalKind.CalendarEvent ? raw.Calendar : null,
                    }));
                }
            }
        }

        Map = SignalMap.Of(result.Configuration, runtimes.ToImmutable());
        SetTracked([.. Map.Signals.Select(s => new TrackedEntity(s.Definition.Entity, s.Definition.Unit) { Series = s.Definition.Series, Attribute = s.Definition.Attribute, Calendar = s.Definition.Kind == SignalKind.CalendarEvent }).OrderBy(t => t.Entity)]);
        return reread;
    }

    /// <summary>Returns the new tracked set once after it changed.</summary>
    public bool TryTakeTrackedChange(out ImmutableArray<TrackedEntity> tracked)
    {
        tracked = _tracked;
        var changed = _trackedChanged;
        _trackedChanged = false;
        return changed;
    }

    private void SetTracked(ImmutableArray<TrackedEntity> tracked)
    {
        if (!tracked.SequenceEqual(_tracked))
        {
            _tracked = tracked;
            _trackedChanged = true;
        }
    }
}
