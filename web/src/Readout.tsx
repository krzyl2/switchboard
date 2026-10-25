import type { Readout } from './api/setup';
import { formatAge, formatInterval } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { StateLabel } from './ui';

/**
 * The live readout beside a picked entity (UI-04): its value as Home Assistant reports it, the age
 * of its last report on the add-on's clock (it ticks with every poll, so a frozen value shows), and
 * its refresh once detected (SIG-07). A wrong pick (an entity that does not exist, a sleeping one,
 * a value in another unit) is visible at once.
 */
export function ReadoutLine({ readout, t, lang }: { readout: Readout | undefined; t: Messages; lang: Lang }) {
  if (readout === undefined) return <span class="readout sub-inline">{t['setup.readout.none']}</span>;
  switch (readout.status) {
    case 'pending':
      return <span class="readout sub-inline">{t['setup.readout.pending']}</span>;
    case 'not_found':
      return (
        <span class="readout">
          <StateLabel tone="crit" label={t['setup.readout.not_found']} />
        </span>
      );
    case 'unavailable':
      return (
        <span class="readout">
          <StateLabel tone="warn" label={fill(t['setup.readout.unavailable'], { state: readout.state ?? '' })} />
        </span>
      );
    case 'found':
      return (
        <span class="readout" data-entity={readout.entityId}>
          <span class="mono value">
            {readout.state}
            {readout.unit !== null ? ` ${readout.unit}` : ''}
          </span>
          {readout.ageS !== null && <span class="sub age">{fill(t['setup.readout.age'], { age: formatAge(readout.ageS, lang) })}</span>}
          <span class="sub refresh">
            {readout.refresh.intervalS !== null
              ? fill(t['setup.readout.refresh'], { interval: formatInterval(readout.refresh.intervalS, lang) })
              : fill(t['setup.readout.detecting'], { samples: String(readout.refresh.samples), needed: String(readout.refresh.needed) })}
          </span>
        </span>
      );
  }
}
