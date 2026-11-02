import { cleanup, render, screen } from '@testing-library/preact';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { I18n, messages, useT } from '.';
import { assertNever } from '../assertNever';

afterEach(cleanup);

function Probe() {
  return <p>{useT().loading}</p>;
}

// Every screen reads its texts from the one context; a component outside it must fail loudly,
// because falling back to a language would show the wrong one to a Polish user without a sign.
describe('useT', () => {
  it('serves the texts of the language of the enclosing I18n', () => {
    render(
      <I18n lang="pl">
        <Probe />
      </I18n>,
    );
    expect(screen.getByText(messages.pl.loading)).toBeTruthy();
  });

  it('is an error outside an I18n instead of a silent default language', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Probe />)).toThrow('useT() needs an enclosing <I18n>.');
    error.mockRestore();
  });
});

describe('assertNever', () => {
  it('fails on a value the type said could not happen, naming it', () => {
    expect(() => assertNever('bogus' as never)).toThrow('Unhandled case: "bogus"');
  });
});
