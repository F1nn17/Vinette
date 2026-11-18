import type { AttachmentItem } from './attachments';
import { EMPTY_COMPOSE, type ComposeValues } from './compose-values';
import { sendBlocker } from './send';

const to = (address: string): ComposeValues => ({ ...EMPTY_COMPOSE, to: [{ name: '', address }] });
const file = (status: AttachmentItem['status']): AttachmentItem => ({
  key: 'k',
  fileName: 'a.pdf',
  size: 1,
  contentType: 'application/pdf',
  status,
  progress: status === 'done' ? 1 : 0,
});

describe('sendBlocker', () => {
  it('needs a recipient in To, Cc or Bcc', () => {
    expect(sendBlocker(EMPTY_COMPOSE, [])).toBe('noRecipients');
    expect(
      sendBlocker({ ...EMPTY_COMPOSE, bcc: [{ name: '', address: 'a@x.test' }] }, []),
    ).toBeNull();
  });

  it('wants every recipient to be an address', () => {
    expect(sendBlocker(to('anna'), [])).toBe('invalidAddress');
    expect(sendBlocker(to('a@x.test'), [])).toBeNull();
  });

  it('waits for files on their way, and does not leave out a file that failed without saying', () => {
    expect(sendBlocker(to('a@x.test'), [file('uploading')])).toBe('uploading');
    expect(sendBlocker(to('a@x.test'), [file('done'), file('failed')])).toBe('failedFiles');
    expect(sendBlocker(to('a@x.test'), [file('done')])).toBeNull();
  });
});
