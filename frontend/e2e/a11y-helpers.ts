import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

/**
 * The rules of WCAG 2.0, 2.1 and 2.2 at levels A and AA (VIN-130). The engine is axe-core: it checks the names, roles and states that assistive
 * technology reads, the contrast of the text, the order and structure of the page, the labels of the fields and the size of the targets.
 */
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

interface Found {
  rule: string;
  impact: string | null | undefined;
  help: string;
  nodes: string[];
}

/**
 * Runs axe over the page as it is now and returns what it found, readable. The frame that shows a message is left out: its content is the
 * sender's, not ours (cleaned on the server and sandboxed), and axe cannot enter a frame that has no access to the page; the frame itself
 * must have a title, which `expectMessageFrameTitled` checks.
 */
export async function violations(page: Page): Promise<Found[]> {
  const results = await new AxeBuilder({ page })
    .withTags(WCAG_TAGS)
    .exclude('iframe[sandbox]')
    .analyze();
  return results.violations.map((v) => ({
    rule: v.id,
    impact: v.impact,
    help: v.help,
    nodes: v.nodes
      .slice(0, 4)
      .map((n) => `${n.target.join(' >> ')}: ${n.failureSummary?.split('\n')[1]?.trim() ?? ''}`),
  }));
}

/** Fails with the list of the violations (rule, what is wrong, the first elements) when the page has any. */
export async function expectAccessible(page: Page, screen: string): Promise<void> {
  const found = await violations(page);
  const text = found.map((f) => `${f.rule} [${f.impact}] ${f.help}\n    ${f.nodes.join('\n    ')}`);
  expect(text, `${screen}: violations of WCAG 2.2 A and AA`).toEqual([]);
}

/** The frame with the text of a message is named after the subject, so that a screen reader says what it is. */
export async function expectMessageFrameTitled(page: Page, subject: string): Promise<void> {
  await expect(page.locator(`iframe[sandbox][title="${subject}"]`)).toHaveCount(1);
}
