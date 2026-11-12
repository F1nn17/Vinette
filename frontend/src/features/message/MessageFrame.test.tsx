import { render, screen } from '@testing-library/react';
import { MessageFrame, FRAME_SANDBOX } from './MessageFrame';
import { buildFrameDocument, FRAME_CSP } from './frame-document';

const HOSTILE =
  '<p>visible</p><script>alert(1)</script><img src="x" onerror="alert(2)"><a href="javascript:alert(3)">x</a><iframe src="https://evil.example"></iframe>';

function frame() {
  return screen.getByTitle('Subject') as HTMLIFrameElement;
}

describe('MessageFrame', () => {
  it('is a frame named for assistive technology', () => {
    render(<MessageFrame title="Subject" body="<p>hello</p>" format="html" />);

    expect(frame().tagName).toBe('IFRAME');
  });

  it('is sandboxed without scripts and without its own origin, which no other permission replaces', () => {
    render(<MessageFrame title="Subject" body="<p>hello</p>" format="html" />);

    const tokens = (frame().getAttribute('sandbox') ?? '').split(/\s+/).filter(Boolean);
    expect(tokens).not.toContain('allow-scripts');
    expect(tokens).not.toContain('allow-same-origin');
    expect(tokens).toEqual(FRAME_SANDBOX.split(' '));
    // Everything else that would let the page act on its own or on this one stays out too.
    for (const forbidden of [
      'allow-forms',
      'allow-top-navigation',
      'allow-top-navigation-by-user-activation',
      'allow-modals',
      'allow-downloads',
      'allow-pointer-lock',
      'allow-presentation',
      'allow-orientation-lock',
    ]) {
      expect(tokens).not.toContain(forbidden);
    }
  });

  it('opens links in a new tab that is not sandboxed, and nothing else', () => {
    expect(FRAME_SANDBOX).toBe('allow-popups allow-popups-to-escape-sandbox');
  });

  it('gets the content through srcdoc, not through an address, and sends no referrer', () => {
    render(<MessageFrame title="Subject" body="<p>hello</p>" format="html" />);

    expect(frame().hasAttribute('src')).toBe(false);
    expect(frame().getAttribute('srcdoc')).toContain('<p>hello</p>');
    expect(frame().getAttribute('referrerpolicy')).toBe('no-referrer');
  });

  it('keeps hostile markup inside the frame: nothing of it becomes part of this page', () => {
    const { container } = render(<MessageFrame title="Subject" body={HOSTILE} format="html" />);

    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('a')).toBeNull();
    expect(container.querySelectorAll('iframe')).toHaveLength(1); // only ours
    expect(document.body.querySelector('script')).toBeNull();
    expect(frame().getAttribute('srcdoc')).toContain('<script>alert(1)</script>'); // as text of an attribute
  });

  it('lets the frame size follow the space it is given', () => {
    render(<MessageFrame title="Subject" body="x" format="text" className="extra" />);

    expect(frame().className).toContain('h-full');
    expect(frame().className).toContain('w-full');
    expect(frame().className).toContain('extra');
  });

  it('builds the page again when the body changes', () => {
    const { rerender } = render(<MessageFrame title="Subject" body="<p>one</p>" format="html" />);
    rerender(<MessageFrame title="Subject" body="<p>two</p>" format="html" />);

    expect(frame().getAttribute('srcdoc')).toContain('<p>two</p>');
    expect(frame().getAttribute('srcdoc')).not.toContain('<p>one</p>');
  });
});

describe('the page inside the frame', () => {
  const page = buildFrameDocument('<p>body</p>', 'html');

  it('carries its own policy first: no scripts, no network but data pictures, no forms, no base', () => {
    expect(page).toContain(`<meta http-equiv="Content-Security-Policy" content="${FRAME_CSP}">`);
    expect(page.indexOf('Content-Security-Policy')).toBeLessThan(page.indexOf('<p>body</p>'));
    const directives = new Map(
      FRAME_CSP.split('; ').map((d) => [d.split(' ')[0], d.slice(d.indexOf(' ') + 1)]),
    );
    expect(directives.get('default-src')).toBe("'none'");
    expect(directives.get('script-src')).toBe("'none'");
    expect(directives.get('img-src')).toBe('data:');
    expect(directives.get('connect-src')).toBe("'none'");
    expect(directives.get('form-action')).toBe("'none'");
    expect(directives.get('base-uri')).toBe("'none'");
    expect(directives.get('frame-src')).toBe("'none'");
    expect(directives.get('object-src')).toBe("'none'");
  });

  it('never allows script execution or any outside address in the policy', () => {
    expect(FRAME_CSP).not.toContain('unsafe-eval');
    for (const directive of FRAME_CSP.split('; ').filter((d) => /^(script|default)-src/u.test(d))) {
      expect(directive).not.toContain('unsafe-inline');
    }
    expect(FRAME_CSP).not.toMatch(/https?:|\*/u);
  });

  it('opens links in a new tab by default', () => {
    expect(page).toContain('<base target="_blank">');
  });

  it('gives text bodies the reading width of the design and html bodies the full width', () => {
    expect(buildFrameDocument('x', 'text')).toContain('.mail{max-width:68ch;}');
    expect(buildFrameDocument('x', 'html')).toContain('.mail{}');
  });

  it('uses the colors it is given', () => {
    const colored = buildFrameDocument('x', 'html', {
      text: '#111111',
      link: '#222222',
      muted: '#333333',
      border: '#444444',
    });

    for (const color of ['#111111', '#222222', '#333333', '#444444']) {
      expect(colored).toContain(color);
    }
  });

  it('puts the body inside the mail container and nothing of ours after it but the closing tags', () => {
    expect(page).toMatch(/<div class="mail"><p>body<\/p><\/div><\/body><\/html>$/u);
  });
});
