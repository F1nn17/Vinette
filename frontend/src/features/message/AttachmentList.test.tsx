import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AttachmentList } from './AttachmentList';
import { attachmentHref, extensionOf } from './attachment-helpers';

const file = (
  partId: string,
  fileName: string,
  size = 1024,
  extra: Record<string, unknown> = {},
) => ({
  partId,
  fileName,
  contentType: 'application/octet-stream',
  size,
  isMessage: false,
  ...extra,
});

describe('AttachmentList', () => {
  it('has a heading with the number of files and a card for each', () => {
    render(
      <AttachmentList
        messageId="SU5CT1g.1.7"
        attachments={[
          file('1.2', 'landing-v2.pdf', 4.8 * 1024 * 1024),
          file('1.3', 'hero-1440.png', 1.2 * 1024 * 1024),
        ]}
      />,
    );

    expect(screen.getByRole('heading', { level: 2, name: 'Вложения · 2' })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('landing-v2.pdf')).toBeInTheDocument();
    expect(screen.getByText('hero-1440.png')).toBeInTheDocument();
    expect(screen.getByText(/4,8\s/u)).toBeInTheDocument();
    expect(screen.getByText(/1,2\s/u)).toBeInTheDocument();
  });

  it('is a region named by its heading', () => {
    render(<AttachmentList messageId="a.1.1" attachments={[file('1.2', 'a.pdf')]} />);

    expect(screen.getByRole('region', { name: 'Вложения · 1' })).toBeInTheDocument();
  });

  it('makes each card one link that downloads the file under its own name', () => {
    render(<AttachmentList messageId="SU5CT1g.1.7" attachments={[file('1.2.1', 'report.pdf')]} />);

    const link = screen.getByRole('link', { name: /Скачать report\.pdf/u });
    expect(link).toHaveAttribute('href', '/api/v1/messages/SU5CT1g.1.7/attachments/1.2.1');
    expect(link).toHaveAttribute('download', 'report.pdf');
  });

  it('names the link by file and size for assistive technology', () => {
    render(<AttachmentList messageId="a.1.1" attachments={[file('1.2', 'report.pdf', 2048)]} />);

    expect(screen.getByRole('link', { name: /^Скачать report\.pdf, 2\s/u })).toBeInTheDocument();
  });

  it('is reached with the keyboard: one Tab stop per card, in order', async () => {
    const user = userEvent.setup();
    render(
      <AttachmentList
        messageId="a.1.1"
        attachments={[file('1.2', 'one.pdf'), file('1.3', 'two.pdf')]}
      />,
    );

    await user.tab();
    expect(within(screen.getAllByRole('listitem')[0]!).getByRole('link')).toHaveFocus();
    await user.tab();
    expect(within(screen.getAllByRole('listitem')[1]!).getByRole('link')).toHaveFocus();
  });

  it('shows the file type on a tile, in capitals', () => {
    render(
      <AttachmentList
        messageId="a.1.1"
        attachments={[file('1.2', 'a.pdf'), file('1.3', 'b.PNG'), file('1.4', 'c.docx')]}
      />,
    );

    expect(screen.getByText('PDF')).toBeInTheDocument();
    expect(screen.getByText('PNG')).toBeInTheDocument();
    expect(screen.getByText('DOCX')).toBeInTheDocument();
  });

  it('shows a plain file icon when the name has no usable extension', () => {
    const { container } = render(
      <AttachmentList
        messageId="a.1.1"
        attachments={[file('1.2', 'README'), file('1.3', 'archive.verylongextension')]}
      />,
    );

    expect(container.querySelectorAll('svg.lucide-file')).toHaveLength(2);
  });

  it('shows nothing when there are no attachments', () => {
    const { container } = render(<AttachmentList messageId="a.1.1" attachments={[]} />);

    expect(container).toBeEmptyDOMElement();
  });

  it('writes names as text, never as markup', () => {
    const { container } = render(
      <AttachmentList
        messageId="a.1.1"
        attachments={[file('1.2', '<img src=x onerror=alert(1)>.pdf')]}
      />,
    );

    expect(screen.getByText('<img src=x onerror=alert(1)>.pdf')).toBeInTheDocument();
    expect(container.querySelector('li img')).toBeNull();
  });

  it('puts the ids into the address in encoded form', () => {
    expect(attachmentHref('a b.1.1', '1.2')).toBe('/api/v1/messages/a%20b.1.1/attachments/1.2');
  });
});

describe('extensionOf', () => {
  it.each([
    ['report.pdf', 'PDF'],
    ['photo.JPEG', 'JPEG'],
    ['отчёт.docx', 'DOCX'],
    ['archive.tar.gz', 'GZ'],
    ['файл.тхт', 'ТХТ'],
    ['.hidden', null],
    ['noextension', null],
    ['trailing.', null],
    ['too.long5', null],
    ['has space.p df', null],
    ['', null],
  ])('%s -> %s', (name, expected) => {
    expect(extensionOf(name)).toBe(expected);
  });
});
