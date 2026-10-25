import type { SetupIssue } from './api/setup';
import { fill, type Messages } from './i18n';
import { fieldId } from './setupDraft';
import { StateLabel } from './ui';

/**
 * One issue as a sentence in the page's language, from its code and args; when an issue carries
 * fewer args than its sentence names (the same code raised in another context), the add-on's own
 * message stands in, so no sentence ever loses its subject.
 */
export function issueText(issue: SetupIssue, t: Messages): string {
  try {
    return fill(t[`setup.issue.${issue.code}`], issue.args);
  } catch {
    return issue.message;
  }
}

/**
 * The issues of a step (UI-05): what must be fixed, then what is worth checking, each linked to the
 * field it names so the user lands on it. An issue about a part of the file the wizard does not
 * edit names that part instead.
 */
export function SetupIssues({ issues, t }: { issues: readonly SetupIssue[]; t: Messages }) {
  if (issues.length === 0) return null;
  const errors = issues.filter((i) => i.severity === 'error');
  const warnings = issues.filter((i) => i.severity === 'warning');
  return (
    <div class="setup-issues">
      {errors.length > 0 && (
        <div role="alert">
          <h3>{t['setup.issues.title']}</h3>
          <ul>
            {errors.map((issue, i) => (
              <IssueItem key={`e${i}`} issue={issue} t={t} />
            ))}
          </ul>
        </div>
      )}
      {warnings.length > 0 && (
        <div role="status">
          <h3>{t['setup.issues.warnings']}</h3>
          <ul>
            {warnings.map((issue, i) => (
              <IssueItem key={`w${i}`} issue={issue} t={t} />
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function IssueItem({ issue, t }: { issue: SetupIssue; t: Messages }) {
  const label = <StateLabel tone={issue.severity === 'error' ? 'crit' : 'warn'} label={t[`setup.severity.${issue.severity}`]} />;
  return (
    <li data-issue={issue.code} data-field={issue.field ?? undefined}>
      {label}{' '}
      {issue.field !== null ? (
        <a
          href={`#${fieldId(issue.field)}`}
          onClick={(event) => {
            // Hash routes: the link must not navigate, only focus the field.
            event.preventDefault();
            document.getElementById(fieldId(issue.field!))?.focus();
          }}
        >
          {issueText(issue, t)}
        </a>
      ) : (
        <span>{issueText(issue, t)}</span>
      )}
      {issue.code === 'document_invalid' && <span class="sub">{issue.message}</span>}
      {issue.documentPath !== null && issue.field === null && <span class="sub mono">{issue.documentPath}</span>}
    </li>
  );
}
