import { useState } from 'preact/hooks';
import type { DiscoveredEntity } from './api/setup';
import { fill, type Messages } from './i18n';

/**
 * What a field accepts (UI-03): entities of these domains whose device class is one of
 * `deviceClasses` or whose unit is one of `units` (a YAML Modbus sensor often has a unit and no
 * device class). Without classes or units, the domain alone decides (actuators).
 */
export interface PickerFilter {
  domains: readonly string[];
  deviceClasses?: readonly string[];
  units?: readonly string[];
}

export function matchesFilter(entity: DiscoveredEntity, filter: PickerFilter): boolean {
  if (!filter.domains.includes(entity.domain) || entity.disabled) return false;
  const classes = filter.deviceClasses ?? [];
  const units = filter.units ?? [];
  if (classes.length === 0 && units.length === 0) return true;
  return (entity.deviceClass !== null && classes.includes(entity.deviceClass)) || (entity.unit !== null && units.includes(entity.unit));
}

interface Props {
  id: string;
  label: string;
  value: string | null;
  onChange: (value: string | null) => void;
  /** The discovery's entities; null when Home Assistant cannot be reached (manual entry only). */
  entities: readonly DiscoveredEntity[] | null;
  filter: PickerFilter;
  t: Messages;
}

/**
 * The shared entity picker of every step. It offers the entities that fit the field, lists derived
 * sensors (template, statistics, powercalc …) disabled with their platform as the reason (07: never
 * a control input), marks entities whose source cannot be verified, never preselects anything (only
 * the user declares an entity, CLAUDE.md), and always allows typing the entity ID by hand. Entity
 * IDs are shown in monospace.
 */
export function EntityPicker({ id, label, value, onChange, entities, filter, t }: Props) {
  const [manual, setManual] = useState(false);
  const candidates = entities === null ? [] : entities.filter((e) => matchesFilter(e, filter));
  const primary = candidates.filter((e) => e.derived === null);
  const derived = candidates.filter((e) => e.derived !== null);
  const known = value !== null && candidates.some((e) => e.entityId === value);
  const typing = entities === null || manual;
  return (
    <div class="picker">
      <label for={id}>{label}</label>
      {typing ? (
        <input
          id={id}
          class="mono"
          type="text"
          spellcheck={false}
          autocomplete="off"
          placeholder="sensor.…"
          value={value ?? ''}
          onInput={(event) => {
            const text = (event.currentTarget as HTMLInputElement).value.trim();
            onChange(text === '' ? null : text);
          }}
        />
      ) : (
        <select
          id={id}
          class="mono"
          value={value ?? ''}
          onChange={(event) => {
            const selected = (event.currentTarget as HTMLSelectElement).value;
            onChange(selected === '' ? null : selected);
          }}
        >
          <option value="">{t['setup.picker.choose']}</option>
          {primary.map((e) => (
            <option key={e.entityId} value={e.entityId}>
              {e.entityId}
              {e.name !== null ? ` — ${e.name}` : ''}
              {e.inRegistry ? '' : ` (${t['setup.picker.notInRegistry']})`}
            </option>
          ))}
          {derived.map((e) => (
            <option key={e.entityId} value={e.entityId} disabled>
              {fill(t['setup.picker.derived'], { entity: e.entityId, platform: e.derived!.platform })}
            </option>
          ))}
          {value !== null && !known && <option value={value}>{value}</option>}
        </select>
      )}
      {entities === null && <span class="sub">{t['setup.picker.unavailable']}</span>}
      {entities !== null && candidates.length === 0 && <span class="sub">{t['setup.picker.empty']}</span>}
      {entities !== null && (
        <button type="button" class="linkbutton" onClick={() => setManual(!manual)}>
          {manual ? t['setup.picker.list'] : t['setup.picker.manual']}
        </button>
      )}
    </div>
  );
}
