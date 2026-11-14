import { QueryClient } from '@tanstack/react-query';
import { consumeRemoval, ECHO_EXPIRY_MS, expectRemoval } from './echo';

const client = () => new QueryClient();

describe('echo of our own removals', () => {
  it('lets nothing through when the report is exactly what was expected', () => {
    const c = client();
    expectRemoval(c, 'INBOX', 2);

    expect(consumeRemoval(c, 'INBOX', 2)).toBe(0);
  });

  it('lets through what was not expected: the rest of a bigger report', () => {
    const c = client();
    expectRemoval(c, 'INBOX', 2);

    expect(consumeRemoval(c, 'INBOX', 3)).toBe(1);
    expect(consumeRemoval(c, 'INBOX', 1)).toBe(1); // the expectation is used up
  });

  it('accepts the report in pieces (the server may split a batch)', () => {
    const c = client();
    expectRemoval(c, 'INBOX', 3);

    expect(consumeRemoval(c, 'INBOX', 1)).toBe(0);
    expect(consumeRemoval(c, 'INBOX', 1)).toBe(0);
    expect(consumeRemoval(c, 'INBOX', 1)).toBe(0);
    expect(consumeRemoval(c, 'INBOX', 1)).toBe(1);
  });

  it('adds up the expectations of several actions in a row', () => {
    const c = client();
    expectRemoval(c, 'INBOX', 1);
    expectRemoval(c, 'INBOX', 2);

    expect(consumeRemoval(c, 'INBOX', 3)).toBe(0);
  });

  it('keeps the folders apart', () => {
    const c = client();
    expectRemoval(c, 'INBOX', 1);

    expect(consumeRemoval(c, 'Work', 1)).toBe(1);
    expect(consumeRemoval(c, 'INBOX', 1)).toBe(0);
  });

  it('lets everything through when nothing was expected', () => {
    expect(consumeRemoval(client(), 'INBOX', 2)).toBe(2);
  });

  it('forgets an expectation that is taken back (the action failed)', () => {
    const c = client();
    const takeBack = expectRemoval(c, 'INBOX', 1);

    takeBack();

    expect(consumeRemoval(c, 'INBOX', 1)).toBe(1);
  });

  it('forgets an expectation whose report never came, so that a real removal is not hidden for long', () => {
    const c = client();
    const now = 1_000_000;
    expectRemoval(c, 'INBOX', 1, now);

    expect(consumeRemoval(c, 'INBOX', 1, now + ECHO_EXPIRY_MS + 1)).toBe(1);
  });

  it('keeps the expectations of one client apart from those of another', () => {
    const first = client();
    const second = client();
    expectRemoval(first, 'INBOX', 1);

    expect(consumeRemoval(second, 'INBOX', 1)).toBe(1);
    expect(consumeRemoval(first, 'INBOX', 1)).toBe(0);
  });
});
