import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ReadingPane } from './ReadingPane';

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' }, ...init });

const message = (extra: Record<string, unknown> = {}) => ({
  id: 'SU5CT1g.1.7',
  subject: 'Layouts, second iteration',
  from: { name: 'Anna Smirnova', address: 'anna@studio.example' },
  to: [{ name: '', address: 'user@example.com' }],
  cc: [],
  date: new Date(2026, 9, 2, 14, 32).toISOString(),
  seen: true,
  flagged: false,
  answered: false,
  draft: false,
  labels: [],
  bodyFormat: 'html',
  body: '<p>Hello</p>',
  blockedExternalResources: 0,
  truncated: false,
  sender: { verdict: 'unknown', domain: 'studio.example' },
  attachments: [],
  ...extra,
});

function mockApi(answer: () => Response | unknown) {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      urls.push(String(input));
      const result = answer();
      return Promise.resolve(result instanceof Response ? result : json(result));
    }),
  );
  return urls;
}

function renderPane(messageId: string | null = 'SU5CT1g.1.7', address = 'user@example.com') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ReadingPane messageId={messageId ?? undefined} address={address} />
    </QueryClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('ReadingPane', () => {
  it('asks for the message by its id and shows the subject, sender, recipient and date', async () => {
    const urls = mockApi(() => message());
    renderPane();

    expect(
      await screen.findByRole('heading', { name: 'Layouts, second iteration' }),
    ).toBeInTheDocument();
    expect(urls).toEqual(['/api/v1/messages/SU5CT1g.1.7']);
    expect(screen.getByText('Anna Smirnova')).toBeInTheDocument();
    expect(screen.getByText('anna@studio.example')).toBeInTheDocument();
    expect(screen.getByText('Кому: вы <user@example.com>')).toBeInTheDocument();
    expect(screen.getByText('Пт, 2 окт., 14:32')).toBeInTheDocument();
  });

  it('is an article named by its subject', async () => {
    mockApi(() => message());
    renderPane();

    expect(
      await screen.findByRole('article', { name: 'Layouts, second iteration' }),
    ).toBeInTheDocument();
  });

  it('puts the body in the sandboxed frame, named by the subject', async () => {
    mockApi(() => message({ body: '<p>Hello</p>' }));
    renderPane();

    const frame = (await screen.findByTitle('Layouts, second iteration')) as HTMLIFrameElement;
    expect(frame.tagName).toBe('IFRAME');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-scripts');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame.getAttribute('srcdoc')).toContain('<p>Hello</p>');
  });

  it('gives a plain text body the reading width', async () => {
    mockApi(() => message({ bodyFormat: 'text', body: '<div>plain</div>' }));
    renderPane();

    const frame = (await screen.findByTitle('Layouts, second iteration')) as HTMLIFrameElement;
    expect(frame.getAttribute('srcdoc')).toContain('max-width:68ch');
  });

  it('shows the labels of the message', async () => {
    mockApi(() => message({ labels: ['Work', 'Bills'] }));
    renderPane();

    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    expect(screen.getByText('Work')).toBeInTheDocument();
    expect(screen.getByText('Bills')).toBeInTheDocument();
  });

  it('names the recipients when the mail is not only for the reader', async () => {
    mockApi(() =>
      message({
        to: [
          { name: 'Boris', address: 'b@example.org' },
          { name: '', address: 'c@example.org' },
          { name: 'Dina', address: 'd@example.org' },
          { name: 'Egor', address: 'e@example.org' },
          { name: 'Fedor', address: 'f@example.org' },
        ],
      }),
    );
    renderPane();

    expect(await screen.findByText('Кому: Boris, c@example.org, Dina и ещё 2')).toBeInTheDocument();
  });

  it('says so when a message has no sender, no subject, no recipients and no date', async () => {
    mockApi(() => message({ from: null, subject: '', to: [], date: null }));
    renderPane();

    expect(await screen.findByRole('heading', { name: '(без темы)' })).toBeInTheDocument();
    expect(screen.getByText('Без отправителя')).toBeInTheDocument();
    expect(screen.getByText('Получатели не указаны')).toBeInTheDocument();
    expect(document.querySelector('time')).toBeNull();
  });

  describe('the sender check', () => {
    it('confirms a verified sender', async () => {
      mockApi(() => message({ sender: { verdict: 'verified', domain: 'studio.example' } }));
      renderPane();

      expect(await screen.findByText('Отправитель подтверждён')).toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('warns about a suspicious one in plain words', async () => {
      mockApi(() => message({ sender: { verdict: 'suspicious', domain: 'bank.example' } }));
      renderPane();

      const warning = await screen.findByRole('alert');
      expect(warning).toHaveTextContent('Похоже на мошенничество');
      expect(warning).toHaveTextContent('Не переходите по ссылкам и не вводите пароль');
      expect(screen.queryByText('Отправитель подтверждён')).not.toBeInTheDocument();
    });

    it('shows nothing when there is nothing reliable to say', async () => {
      mockApi(() => message({ sender: { verdict: 'unknown', domain: null } }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(screen.queryByText('Отправитель подтверждён')).not.toBeInTheDocument();
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('treats a verdict it does not know as unknown', async () => {
      mockApi(() => message({ sender: { verdict: 'something-new', domain: null } }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
  });

  describe('hidden pictures', () => {
    it('says that external images are hidden, and why', async () => {
      mockApi(() => message({ blockedExternalResources: 3 }));
      renderPane();

      expect(await screen.findByText(/Внешние изображения скрыты/u)).toBeInTheDocument();
    });

    it('says nothing when nothing was hidden', async () => {
      mockApi(() => message({ blockedExternalResources: 0 }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(screen.queryByText(/Внешние изображения скрыты/u)).not.toBeInTheDocument();
    });

    it('reads the count when the server sends it as text', async () => {
      mockApi(() => message({ blockedExternalResources: '2' }));
      renderPane();

      expect(await screen.findByText(/Внешние изображения скрыты/u)).toBeInTheDocument();
    });
  });

  describe('the action bar', () => {
    it('has the actions of the design, named for assistive technology', async () => {
      mockApi(() => message());
      renderPane();

      const bar = await screen.findByRole('toolbar', { name: 'Действия с письмом' });
      for (const name of [
        'Ответить',
        'Переслать',
        'В архив',
        'В спам',
        'Удалить',
        'Флажок',
        'Ещё',
      ]) {
        expect(within(bar).getByRole('button', { name })).toBeInTheDocument();
      }
    });

    it('cannot be used yet: each action is switched on by the task that makes it work', async () => {
      mockApi(() => message());
      renderPane();

      const bar = await screen.findByRole('toolbar', { name: 'Действия с письмом' });
      for (const button of within(bar).getAllByRole('button')) {
        expect(button).toBeDisabled();
      }
    });

    it('shows the flag as pressed for a flagged message', async () => {
      mockApi(() => message({ flagged: true }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' }); // the message, not the placeholder
      const bar = screen.getByRole('toolbar', { name: 'Действия с письмом' });
      expect(within(bar).getByRole('button', { name: 'Флажок' })).toHaveAttribute(
        'aria-pressed',
        'true',
      );
    });
  });

  it('says in plain words when the mail has no text', async () => {
    mockApi(() => message({ bodyFormat: 'empty', body: '' }));
    renderPane();

    expect(await screen.findByText('В письме нет текста.')).toBeInTheDocument();
    expect(screen.queryByTitle('Layouts, second iteration')).not.toBeInTheDocument();
  });

  it('shows a placeholder while the message loads', async () => {
    mockApi(() => new Promise(() => undefined) as unknown as Response);
    renderPane();

    expect(await screen.findByRole('status', { name: 'Загрузка…' })).toBeInTheDocument();
  });

  it.each([
    ['not_found', 404, 'Этого письма или папки уже нет'],
    ['payload_too_large', 413, 'Письмо слишком большое'],
    ['uid_validity_changed', 409, 'Папка изменилась'],
    ['mail_unavailable', 503, 'Почта временно недоступна'],
  ])('explains %s in plain words and offers to try again', async (code, status, text) => {
    let fail = true;
    mockApi(() => (fail ? json({ code, title: 'x' }, { status }) : message()));
    const user = userEvent.setup();
    renderPane();

    expect(await screen.findByRole('alert')).toHaveTextContent(text);
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Повторить' }));
    expect(
      await screen.findByRole('heading', { name: 'Layouts, second iteration' }),
    ).toBeInTheDocument();
  });

  it('shows a hint instead of a message when none is open, and asks for nothing', () => {
    const urls = mockApi(() => message());
    renderPane(null);

    expect(screen.getByText('Выберите письмо, чтобы прочитать')).toBeInTheDocument();
    expect(urls).toEqual([]);
  });

  it('keeps hostile text of a message out of the page: subject and sender are only text', async () => {
    mockApi(() =>
      message({
        subject: '<img src=x onerror=alert(1)>',
        from: { name: '<script>alert(2)</script>', address: 'a@example.org' },
      }),
    );
    renderPane();

    expect(
      await screen.findByRole('heading', { name: '<img src=x onerror=alert(1)>' }),
    ).toBeInTheDocument();
    expect(document.querySelector('script')).toBeNull();
    expect(document.querySelector('article img')).toBeNull();
  });
});
