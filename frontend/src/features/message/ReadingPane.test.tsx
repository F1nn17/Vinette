import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ReadingPane, type ReadingPaneProps } from './ReadingPane';

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
  canShowImages: false,
  imagesShown: 0,
  hasExternalImages: false,
  imagesTrusted: false,
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

function renderPane(
  messageId: string | null = 'SU5CT1g.1.7',
  address = 'user@example.com',
  onBack?: () => void,
) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ReadingPane messageId={messageId ?? undefined} address={address} onBack={onBack} />
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
    expect(urls.filter((url) => url.includes('/messages/'))).toEqual([
      '/api/v1/messages/SU5CT1g.1.7',
    ]);
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

  describe('trusting a sender with pictures', () => {
    const verified = { verdict: 'verified', domain: 'studio.example' };
    const withPictures = (extra: Record<string, unknown> = {}) =>
      message({
        hasExternalImages: true,
        blockedExternalResources: 2,
        canShowImages: true,
        sender: verified,
        ...extra,
      });

    /** Answers the message, and the settings change with what it was told to do; remembers what was sent to the settings. */
    function stubServer(first: () => unknown, after: () => unknown = first) {
      const patches: unknown[] = [];
      let changed = false;
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input, init) => {
          if (String(input).endsWith('/api/v1/settings') && init?.method === 'PATCH') {
            patches.push(JSON.parse(String(init.body)));
            changed = true;
            return Promise.resolve(
              json({
                density: 'comfortable',
                language: null,
                labelColors: {},
                trustedImageSenders: [],
              }),
            );
          }
          return Promise.resolve(json(changed ? after() : first()));
        }),
      );
      return patches;
    }

    it('offers to always show the pictures of a verified sender', async () => {
      mockApi(() => withPictures());
      renderPane();

      expect(await screen.findByText(/Показывать его картинки сразу/u)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Всегда показывать' })).toBeEnabled();
    });

    it('trusts the sender with the address of the message, and loads the message again', async () => {
      const patches = stubServer(
        () => withPictures(),
        () =>
          withPictures({
            imagesTrusted: true,
            canShowImages: false,
            imagesShown: 2,
            blockedExternalResources: 0,
          }),
      );
      const user = userEvent.setup();
      renderPane();

      await user.click(await screen.findByRole('button', { name: 'Всегда показывать' }));

      expect(
        await screen.findByText('Картинки от anna@studio.example показываются сразу.'),
      ).toBeInTheDocument();
      expect(patches).toEqual([{ trustImagesFrom: 'anna@studio.example' }]);
      expect(screen.getByRole('button', { name: 'Не показывать сразу' })).toBeInTheDocument();
    });

    it('can take the trust back', async () => {
      const patches = stubServer(
        () =>
          withPictures({
            imagesTrusted: true,
            canShowImages: false,
            imagesShown: 2,
            blockedExternalResources: 0,
          }),
        () => withPictures(),
      );
      const user = userEvent.setup();
      renderPane();

      await user.click(await screen.findByRole('button', { name: 'Не показывать сразу' }));

      expect(await screen.findByRole('button', { name: 'Всегда показывать' })).toBeInTheDocument();
      expect(patches).toEqual([{ untrustImagesFrom: 'anna@studio.example' }]);
    });

    it('does not offer it for a sender who is not verified, as it would have no effect', async () => {
      mockApi(() => withPictures({ sender: { verdict: 'unknown', domain: 'studio.example' } }));
      renderPane();

      await screen.findByText(/Внешние изображения скрыты/u);
      expect(screen.queryByRole('button', { name: 'Всегда показывать' })).not.toBeInTheDocument();
    });

    it('does not offer it for a suspicious sender', async () => {
      mockApi(() => withPictures({ sender: { verdict: 'suspicious', domain: 'studio.example' } }));
      renderPane();

      await screen.findByText(/Внешние изображения скрыты/u);
      expect(screen.queryByRole('button', { name: 'Всегда показывать' })).not.toBeInTheDocument();
    });

    it('still lets the reader withdraw the trust when the sender is no longer verified', async () => {
      mockApi(() =>
        withPictures({
          imagesTrusted: true,
          sender: { verdict: 'unknown', domain: 'studio.example' },
        }),
      );
      renderPane();

      expect(
        await screen.findByRole('button', { name: 'Не показывать сразу' }),
      ).toBeInTheDocument();
    });

    it('says nothing when the message has no pictures from the web', async () => {
      mockApi(() => message({ sender: verified }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(screen.queryByRole('button', { name: 'Всегда показывать' })).not.toBeInTheDocument();
    });

    it('says in plain words when the choice could not be saved', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((_input, init) =>
          Promise.resolve(
            init?.method === 'PATCH'
              ? json({ title: 'x', code: 'bad_request' }, { status: 400 })
              : json(withPictures()),
          ),
        ),
      );
      const user = userEvent.setup();
      renderPane();

      await user.click(await screen.findByRole('button', { name: 'Всегда показывать' }));

      expect(
        await screen.findByText('Не удалось сохранить выбор. Попробуйте ещё раз.'),
      ).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Всегда показывать' })).toBeEnabled();
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

    it('has no button when the server cannot show them (the feature is off)', async () => {
      mockApi(() => message({ blockedExternalResources: 3, canShowImages: false }));
      renderPane();

      await screen.findByText(/Внешние изображения скрыты/u);
      expect(screen.queryByRole('button', { name: 'Показать' })).not.toBeInTheDocument();
    });

    it('asks for the pictures when the button is pressed, and shows them without a blank screen in between', async () => {
      const urls: string[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) => {
          const url = String(input);
          urls.push(url);
          return Promise.resolve(
            json(
              url.includes('images=true')
                ? message({
                    blockedExternalResources: 0,
                    canShowImages: false,
                    imagesShown: 2,
                    body: '<img src="data:image/png;base64,iVBORw0KGgo=">',
                  })
                : message({ blockedExternalResources: 2, canShowImages: true }),
            ),
          );
        }),
      );
      const user = userEvent.setup();
      renderPane();
      await screen.findByText(/Внешние изображения скрыты/u);

      await user.click(screen.getByRole('button', { name: 'Показать' }));

      await waitFor(() =>
        expect(
          (screen.getByTitle('Layouts, second iteration') as HTMLIFrameElement).getAttribute(
            'srcdoc',
          ),
        ).toContain('data:image/png'),
      );
      expect(
        urls.filter((u) => u.includes('/messages/SU5CT1g.1.7') && !u.includes('/flags')),
      ).toEqual(['/api/v1/messages/SU5CT1g.1.7', '/api/v1/messages/SU5CT1g.1.7?images=true']);
      expect(screen.queryByText(/Внешние изображения скрыты/u)).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Показать' })).not.toBeInTheDocument();
    });

    it('says so when some of the pictures could not be loaded', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) =>
          Promise.resolve(
            json(
              String(input).includes('images=true')
                ? message({ blockedExternalResources: 1, canShowImages: false, imagesShown: 1 })
                : message({ blockedExternalResources: 2, canShowImages: true }),
            ),
          ),
        ),
      );
      const user = userEvent.setup();
      renderPane();
      await user.click(await screen.findByRole('button', { name: 'Показать' }));

      expect(
        await screen.findByText('Некоторые изображения не удалось загрузить.'),
      ).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Показать' })).not.toBeInTheDocument();
    });

    it('asks for the pictures of the open message only', async () => {
      const urls: string[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) => {
          urls.push(String(input));
          return Promise.resolve(
            json(message({ id: 'x', blockedExternalResources: 2, canShowImages: true })),
          );
        }),
      );
      const user = userEvent.setup();
      const view = render(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ReadingPane messageId="SU5CT1g.1.7" address="user@example.com" />
        </QueryClientProvider>,
      );
      await user.click(await screen.findByRole('button', { name: 'Показать' }));
      await waitFor(() => expect(urls.some((u) => u.includes('images=true'))).toBe(true));

      view.rerender(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ReadingPane messageId="SU5CT1g.1.8" address="user@example.com" />
        </QueryClientProvider>,
      );

      await waitFor(() => expect(urls).toContain('/api/v1/messages/SU5CT1g.1.8'));
      expect(urls).not.toContain('/api/v1/messages/SU5CT1g.1.8?images=true');
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

    it('keeps replying and forwarding off until the editor exists', async () => {
      mockApi(() => message());
      renderPane();

      const bar = await screen.findByRole('toolbar', { name: 'Действия с письмом' });
      expect(within(bar).getByRole('button', { name: 'Ответить' })).toBeDisabled();
      expect(within(bar).getByRole('button', { name: 'Переслать' })).toBeDisabled();
    });

    it('cannot archive, mark as spam or delete where the screen gives no way to', async () => {
      mockApi(() => message());
      renderPane();

      const bar = await screen.findByRole('toolbar', { name: 'Действия с письмом' });
      for (const name of ['В архив', 'В спам', 'Удалить']) {
        expect(within(bar).getByRole('button', { name })).toBeDisabled();
      }
    });
  });

  describe('actions on the open message', () => {
    const mount = (props: Partial<ReadingPaneProps> = {}) =>
      render(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ReadingPane messageId="SU5CT1g.1.7" address="user@example.com" {...props} />
        </QueryClientProvider>,
      );
    const bar = () => screen.getByRole('toolbar', { name: 'Действия с письмом' });

    it.each([
      ['В архив', 'archive'],
      ['В спам', 'spam'],
      ['Удалить', 'trash'],
    ])('the button "%s" asks for "%s"', async (name, action) => {
      mockApi(() => message());
      const onAction = vi.fn();
      const user = userEvent.setup();
      mount({ onAction });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });

      await user.click(within(bar()).getByRole('button', { name }));

      expect(onAction).toHaveBeenCalledExactlyOnceWith(action);
    });

    it('does not offer the action that would leave the message where it is', async () => {
      mockApi(() => message());
      mount({ onAction: vi.fn(), folderRole: 'archive' });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(within(bar()).queryByRole('button', { name: 'В архив' })).not.toBeInTheDocument();
      expect(within(bar()).getByRole('button', { name: 'В спам' })).toBeInTheDocument();
    });

    it('does not offer to delete from the trash, nor spam from the spam folder', async () => {
      mockApi(() => message());
      const { unmount } = mount({ onAction: vi.fn(), folderRole: 'trash' });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(within(bar()).queryByRole('button', { name: 'Удалить' })).not.toBeInTheDocument();
      unmount();

      mount({ onAction: vi.fn(), folderRole: 'junk' });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(within(bar()).queryByRole('button', { name: 'В спам' })).not.toBeInTheDocument();
    });

    it('"More" opens a panel with "Mark as unread", and chosen it asks for it and closes', async () => {
      mockApi(() => message());
      const onAction = vi.fn();
      const user = userEvent.setup();
      mount({ onAction });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });

      const more = within(bar()).getByRole('button', { name: 'Ещё' });
      expect(more).toHaveAttribute('aria-expanded', 'false');
      await user.click(more);
      expect(more).toHaveAttribute('aria-expanded', 'true');
      await user.click(
        within(screen.getByRole('group', { name: 'Ещё' })).getByRole('button', {
          name: 'Отметить непрочитанным',
        }),
      );

      expect(onAction).toHaveBeenCalledExactlyOnceWith('unread');
      expect(screen.queryByRole('group', { name: 'Ещё' })).not.toBeInTheDocument();
    });

    it('"More" closes on Escape and on a click outside', async () => {
      mockApi(() => message());
      const user = userEvent.setup();
      mount({ onAction: vi.fn() });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });

      await user.click(within(bar()).getByRole('button', { name: 'Ещё' }));
      await user.keyboard('{Escape}');
      expect(screen.queryByRole('group', { name: 'Ещё' })).not.toBeInTheDocument();

      await user.click(within(bar()).getByRole('button', { name: 'Ещё' }));
      await user.click(screen.getByRole('heading', { name: 'Layouts, second iteration' }));
      expect(screen.queryByRole('group', { name: 'Ещё' })).not.toBeInTheDocument();
    });

    it('"More" carries the actions of the phone too: archive, spam, delete and the flag', async () => {
      mockApi(() => message());
      const onAction = vi.fn();
      const user = userEvent.setup();
      mount({ onAction });
      await screen.findByRole('heading', { name: 'Layouts, second iteration' });

      await user.click(within(bar()).getByRole('button', { name: 'Ещё' }));
      const panel = screen.getByRole('group', { name: 'Ещё' });
      for (const name of ['В архив', 'В спам', 'Удалить', 'Флажок', 'Отметить непрочитанным']) {
        expect(within(panel).getByRole('button', { name })).toBeInTheDocument();
      }
      await user.click(within(panel).getByRole('button', { name: 'В спам' }));
      expect(onAction).toHaveBeenCalledExactlyOnceWith('spam');
    });
  });

  describe('the flag', () => {
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

  it('lists the attachments after the body, each as a download link', async () => {
    mockApi(() =>
      message({
        attachments: [
          {
            partId: '1.2',
            fileName: 'report.pdf',
            contentType: 'application/pdf',
            size: 2048,
            isMessage: false,
          },
        ],
      }),
    );
    renderPane();

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Вложения · 1' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Скачать report\.pdf/u })).toHaveAttribute(
      'href',
      '/api/v1/messages/SU5CT1g.1.7/attachments/1.2',
    );
  });

  it('shows no attachments section for a message without files', async () => {
    mockApi(() => message({ attachments: [] }));
    renderPane();

    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    expect(screen.queryByText(/Вложения/u)).not.toBeInTheDocument();
  });

  describe('on a phone', () => {
    it('has a back button first in the bar, which goes back to the list', async () => {
      mockApi(() => message());
      const onBack = vi.fn();
      const user = userEvent.setup();
      renderPane('SU5CT1g.1.7', 'user@example.com', onBack);

      await screen.findByRole('heading', { name: 'Layouts, second iteration' }); // the message, not the placeholder
      const bar = screen.getByRole('toolbar', { name: 'Действия с письмом' });
      const [first] = within(bar).getAllByRole('button');
      expect(first).toHaveAccessibleName('Назад к списку');
      await user.click(first!);

      expect(onBack).toHaveBeenCalledOnce();
    });

    it('has the back button while the message loads and when it cannot be loaded', async () => {
      mockApi(() => json({ code: 'not_found', title: 'x' }, { status: 404 }));
      renderPane('SU5CT1g.1.7', 'user@example.com', vi.fn());

      expect(await screen.findByRole('alert')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Назад к списку' })).toBeInTheDocument();
    });

    it('has touch targets of at least 44 px for the buttons of the bar', async () => {
      mockApi(() => message());
      renderPane('SU5CT1g.1.7', 'user@example.com', vi.fn());

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      const bar = screen.getByRole('toolbar', { name: 'Действия с письмом' });
      for (const button of within(bar).getAllByRole('button')) {
        expect(button.className).toMatch(/max-md:(size-11|h-11)/u);
      }
    });
  });

  it('has no back button where the list is beside the message', async () => {
    mockApi(() => message());
    renderPane();

    await screen.findByRole('heading', { name: 'Layouts, second iteration' });
    expect(screen.queryByRole('button', { name: 'Назад к списку' })).not.toBeInTheDocument();
  });

  describe('a very long body', () => {
    const cut = (extra: Record<string, unknown> = {}) =>
      message({
        truncated: true,
        canShowFull: true,
        body: '<div>the beginning</div>',
        bodyFormat: 'text',
        ...extra,
      });

    it('says that only the beginning is shown, with a button to show all of it', async () => {
      mockApi(() => cut());
      renderPane();

      expect(
        await screen.findByText('Письмо очень длинное: показано только его начало.'),
      ).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Показать целиком' })).toBeEnabled();
    });

    it('asks for the whole body when the button is pressed, and shows it without a blank screen in between', async () => {
      const urls: string[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) => {
          const url = String(input);
          urls.push(url);
          return Promise.resolve(
            json(
              url.includes('full=true')
                ? message({
                    truncated: false,
                    canShowFull: false,
                    body: '<div>the beginning and the rest</div>',
                    bodyFormat: 'text',
                  })
                : cut(),
            ),
          );
        }),
      );
      const user = userEvent.setup();
      renderPane();
      await screen.findByText('Письмо очень длинное: показано только его начало.');

      await user.click(screen.getByRole('button', { name: 'Показать целиком' }));

      await waitFor(() =>
        expect(
          (screen.getByTitle('Layouts, second iteration') as HTMLIFrameElement).getAttribute(
            'srcdoc',
          ),
        ).toContain('the beginning and the rest'),
      );
      expect(
        urls.filter((u) => u.includes('/messages/SU5CT1g.1.7') && !u.includes('/flags')),
      ).toEqual(['/api/v1/messages/SU5CT1g.1.7', '/api/v1/messages/SU5CT1g.1.7?full=true']);
      expect(
        screen.queryByText('Письмо очень длинное: показано только его начало.'),
      ).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Показать целиком' })).not.toBeInTheDocument();
    });

    it('keeps the beginning on screen and the button busy while the rest is coming', async () => {
      let release: () => void = () => undefined;
      const gate = new Promise<void>((resolve) => {
        release = resolve;
      });
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>(async (input) => {
          if (String(input).includes('full=true')) await gate;
          return json(
            String(input).includes('full=true')
              ? message({ truncated: false, canShowFull: false })
              : cut(),
          );
        }),
      );
      const user = userEvent.setup();
      renderPane();
      await user.click(await screen.findByRole('button', { name: 'Показать целиком' }));

      expect(screen.getByTitle('Layouts, second iteration')).toBeInTheDocument(); // not a blank screen
      expect(screen.getByRole('button', { name: 'Загрузка…' })).toBeDisabled();
      release();
      await waitFor(() =>
        expect(screen.queryByRole('button', { name: 'Загрузка…' })).not.toBeInTheDocument(),
      );
    });

    it('says so, without a button, when there is nothing more to show', async () => {
      mockApi(() => cut({ canShowFull: false }));
      renderPane();

      expect(
        await screen.findByText('Письмо очень длинное: показано только его начало.'),
      ).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Показать целиком' })).not.toBeInTheDocument();
    });

    it('says nothing about a body that is not cut', async () => {
      mockApi(() => message({ truncated: false, canShowFull: false }));
      renderPane();

      await screen.findByRole('heading', { name: 'Layouts, second iteration' });
      expect(screen.queryByText(/очень длинное/u)).not.toBeInTheDocument();
    });

    it('asks for the whole body of the open message only: another message starts with its beginning', async () => {
      const urls: string[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) => {
          urls.push(String(input));
          return Promise.resolve(json(cut({ id: 'x' })));
        }),
      );
      const user = userEvent.setup();
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const view = render(
        <QueryClientProvider client={client}>
          <ReadingPane messageId="SU5CT1g.1.7" address="user@example.com" />
        </QueryClientProvider>,
      );
      await user.click(await screen.findByRole('button', { name: 'Показать целиком' }));
      await waitFor(() => expect(urls.some((u) => u.includes('full=true'))).toBe(true));

      view.rerender(
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ReadingPane messageId="SU5CT1g.1.8" address="user@example.com" />
        </QueryClientProvider>,
      );

      await waitFor(() => expect(urls).toContain('/api/v1/messages/SU5CT1g.1.8'));
      expect(urls).not.toContain('/api/v1/messages/SU5CT1g.1.8?full=true');
    });
  });

  it('shows a label in the color the user chose for it', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input) =>
        Promise.resolve(
          json(
            String(input).endsWith('/api/v1/settings')
              ? {
                  density: 'comfortable',
                  language: null,
                  labelColors: { Работа: 'bills' },
                  trustedImageSenders: [],
                }
              : message({ labels: ['Работа'] }),
          ),
        ),
      ),
    );
    renderPane();

    const label = await screen.findByText('Работа');
    await waitFor(() => expect(label.className).toContain('label-bills'));
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
