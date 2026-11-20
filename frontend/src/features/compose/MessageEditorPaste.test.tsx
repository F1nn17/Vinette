import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { EMPTY_BODY, type ComposeBody } from './editor-text';
import { MessageEditor } from './MessageEditor';

function Harness({ seen }: { seen: ComposeBody[] }) {
  const [value, setValue] = useState(EMPTY_BODY);
  return (
    <MessageEditor
      value={value}
      onChange={(v) => {
        setValue(v);
        seen.push(v);
      }}
    />
  );
}

/** Pastes `html` (with its plain text) into the editor the way a browser does when something is copied from Word or a page. */
async function paste(html: string, text = '') {
  const seen: ComposeBody[] = [];
  render(<Harness seen={seen} />);
  const editor = await screen.findByRole('textbox', { name: 'Текст письма' });
  await userEvent.click(editor);
  fireEvent.paste(editor, {
    clipboardData: {
      types: ['text/html', 'text/plain'],
      getData: (type: string) => (type === 'text/html' ? html : type === 'text/plain' ? text : ''),
      files: [],
      items: [],
    },
  });
  return { editor, html: () => seen.at(-1)?.html ?? '' };
}

describe('pasting into the message editor', () => {
  it('keeps the formatting the toolbar can make from a paste out of Word, and none of its styles, classes or Office tags', async () => {
    const { html } = await paste(
      '<html xmlns:o="urn:schemas-microsoft-com:office:office"><head><meta charset="utf-8"><style>p.MsoNormal{margin:0;font-family:Calibri}</style></head><body>' +
        '<!--StartFragment--><p class="MsoNormal" style="margin:0cm;font-size:11pt;color:#1F497D">Hello <b style="mso-bidi-font-weight:normal">there</b><o:p></o:p></p>' +
        '<ul style="margin-top:0cm"><li class="MsoListParagraph" style="mso-list:l0 level1 lfo1">one</li><li class="MsoListParagraph">two</li></ul><!--EndFragment--></body></html>',
    );

    // (the editor keeps an empty line after a list to write in)
    expect(html()).toBe(
      '<p>Hello <strong>there</strong></p><ul><li><p>one</p></li><li><p>two</p></li></ul><p></p>',
    );
  });

  it('keeps the text of a paste out of Google Docs without its wrapper that says "not bold" and without any style', async () => {
    const { html } = await paste(
      '<meta charset=\'utf-8\'><b style="font-weight:normal;" id="docs-internal-guid-1c2d"><p dir="ltr" style="line-height:1.38;margin-top:0pt"><span style="font-size:11pt;font-family:Arial;color:#000000;font-weight:400">Plain </span><span style="font-size:11pt;font-weight:700">bold</span></p></b>',
    );

    expect(html()).toBe('<p>Plain <strong>bold</strong></p>');
  });

  it('takes from a web page its text, bold, italic and links, and nothing it could run or load', async () => {
    const { html } = await paste(
      '<div class="post" style="background:url(https://tracker.example/bg.png)"><h1 id="t" onclick="alert(1)">Title</h1>' +
        '<p>See <a href="https://example.org/page" onclick="alert(1)" style="color:red">the page</a>, <em>italic</em> and <a href="javascript:alert(1)">bad link</a>.</p>' +
        '<img src="https://tracker.example/p.gif" onerror="alert(1)"><script>alert(1)</script><iframe src="https://evil.example"></iframe>' +
        '<table><tr><td>cell</td></tr></table><form action="https://evil.example"><input name="x" value="stolen"></form></div>',
    );

    const result = html();
    expect(result).toContain('<a ');
    expect(result).toContain('href="https://example.org/page"');
    expect(result).toContain('<em>italic</em>');
    expect(result).toContain('bad link');
    for (const forbidden of [
      'javascript',
      'onclick',
      'onerror',
      'style',
      'class',
      'id=',
      '<img',
      '<script',
      '<iframe',
      '<table',
      '<form',
      '<input',
      'tracker.example',
      'evil.example',
    ]) {
      expect(result).not.toContain(forbidden);
    }
  });

  it('never makes a link that goes anywhere but the web or an address', async () => {
    const { html } = await paste(
      '<p><a href="javascript:alert(1)">a</a> <a href="data:text/html,x">b</a> <a href="vbscript:x">c</a> <a href="ftp://example.org">d</a> <a href="file:///etc/passwd">e</a> <a href="mailto:anna@example.org">f</a></p>',
    );

    const result = html();
    expect(result.match(/href="[^"]*"/g)).toEqual(['href="mailto:anna@example.org"']);
  });

  it('takes pasted plain text as text, line by line, and never as markup', async () => {
    const { html } = await paste('', 'one <b>not bold</b>\ntwo <script>alert(1)</script>');

    const result = html();
    expect(result).not.toContain('<b>');
    expect(result).not.toContain('<script');
    expect(result).toContain('&lt;b&gt;not bold&lt;/b&gt;');
    expect(result).toContain('<p>two');
  });

  it('leaves nothing but its own kinds of element in what it reports, whatever was pasted', async () => {
    const { html } = await paste(
      '<custom-tag data-x="1"><svg onload="alert(1)"><script>alert(1)</script></svg><math><mi>x</mi></math><object data="x"></object><embed src="x"><p style="x:y">ok</p></custom-tag>',
    );

    const document = new DOMParser().parseFromString(`<body>${html()}</body>`, 'text/html');
    const allowed = new Set([
      'html',
      'head',
      'body',
      'p',
      'br',
      'strong',
      'b',
      'em',
      'i',
      'u',
      'ul',
      'ol',
      'li',
      'blockquote',
      'code',
      'a',
      'pre',
    ]);
    for (const element of document.querySelectorAll('*')) {
      expect(allowed.has(element.localName), `<${element.localName}>`).toBe(true);
      for (const attribute of element.getAttributeNames()) {
        expect(['href', 'rel', 'target', 'start']).toContain(attribute);
      }
    }
    expect(html()).toContain('ok');
  });
});
