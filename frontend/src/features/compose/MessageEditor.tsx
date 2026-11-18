import { EditorContent, useEditor, useEditorState, type Editor } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import { Code, Link as LinkIcon, List, ListOrdered, Quote } from 'lucide-react';
import { useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/shared/lib/cn';
import { textToHtml, normalizeLink, type ComposeBody } from './editor-text';

export interface MessageEditorProps {
  value: ComposeBody;
  onChange: (value: ComposeBody) => void;
}

const buttonClass =
  'inline-flex size-10 shrink-0 items-center justify-center rounded-sm text-text-2 transition-colors hover:bg-bg max-md:size-11 ' +
  'aria-pressed:bg-accent-soft aria-pressed:text-accent disabled:cursor-not-allowed disabled:opacity-50';

interface ToolbarButtonProps {
  label: string;
  pressed?: boolean;
  onClick: () => void;
  children: ReactNode;
}

function ToolbarButton({ label, pressed, onClick, children }: ToolbarButtonProps) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={pressed}
      // The mouse must not take the cursor out of the text before the format is applied.
      onMouseDown={(e) => e.preventDefault()}
      onClick={onClick}
      className={buttonClass}
    >
      {children}
    </button>
  );
}

const bodyOf = (editor: Editor, plain: boolean): ComposeBody => ({
  plain,
  html: editor.isEmpty ? '' : editor.getHTML(),
  text: editor.getText({ blockSeparator: '\n' }),
});

/**
 * The body of the message (DESIGN.md §6): Tiptap with a toolbar (bold, italic, underline, lists, quote, link, code) and the "Plain text"
 * switch. Only what the toolbar offers can be in the message: pasted text keeps these formats and loses the rest, and a link may lead to
 * `http`, `https` or `mailto` only. "Plain text" drops the formatting and shows a text field; going back to formatting brings the old
 * formatting back if the text was not changed, and otherwise builds paragraphs from the text.
 */
export function MessageEditor({ value, onChange }: MessageEditorProps) {
  const { t } = useTranslation();
  const [linkOpen, setLinkOpen] = useState(false);
  const [linkText, setLinkText] = useState('');
  const [linkError, setLinkError] = useState(false);
  // What the formatted body was when the user went to plain text, to bring it back if the text stays as it was.
  const stashed = useRef<ComposeBody | null>(null);

  const editor = useEditor({
    extensions: [
      StarterKit.configure({
        heading: false,
        strike: false,
        horizontalRule: false,
        codeBlock: false,
        link: {
          openOnClick: false,
          autolink: true,
          defaultProtocol: 'https',
          // Nothing but web and mail addresses (the default also lets through ftp, tel, sms and others).
          isAllowedUri: (url) => url !== undefined && /^(https?|mailto):/i.test(url),
          HTMLAttributes: { rel: 'noopener noreferrer nofollow', target: '_blank' },
        },
      }),
    ],
    content: value.html,
    editorProps: {
      // Ctrl+Enter sends the message; the editor must not take it for a line break.
      handleKeyDown: (_view, event) => event.key === 'Enter' && (event.ctrlKey || event.metaKey),
      attributes: {
        role: 'textbox',
        'aria-multiline': 'true',
        'aria-label': t('compose.body'),
        class: 'min-h-full px-6 py-5 outline-none',
      },
    },
    onUpdate: ({ editor: e }) => onChange(bodyOf(e, false)),
  });

  const active = useEditorState({
    editor,
    selector: ({ editor: e }) => ({
      bold: e?.isActive('bold') ?? false,
      italic: e?.isActive('italic') ?? false,
      underline: e?.isActive('underline') ?? false,
      bulletList: e?.isActive('bulletList') ?? false,
      orderedList: e?.isActive('orderedList') ?? false,
      blockquote: e?.isActive('blockquote') ?? false,
      link: e?.isActive('link') ?? false,
      code: e?.isActive('code') ?? false,
    }),
  });

  const toPlain = () => {
    if (!editor) return;
    const body = bodyOf(editor, false);
    stashed.current = body;
    setLinkOpen(false);
    onChange({ plain: true, html: '', text: body.text });
  };

  const toRich = () => {
    if (!editor) return;
    const back = stashed.current;
    stashed.current = null;
    const html = back && back.text === value.text ? back.html : textToHtml(value.text);
    editor.commands.setContent(html, { emitUpdate: false });
    onChange({ plain: false, html, text: value.text });
  };

  const openLink = () => {
    if (!editor) return;
    setLinkText((editor.getAttributes('link').href as string | undefined) ?? '');
    setLinkError(false);
    setLinkOpen(true);
  };

  const applyLink = () => {
    if (!editor) return;
    if (linkText.trim() === '') {
      editor.chain().focus().extendMarkRange('link').unsetLink().run();
      setLinkOpen(false);
      return;
    }
    const href = normalizeLink(linkText);
    if (!href) {
      setLinkError(true);
      return;
    }
    if (editor.state.selection.empty && !editor.isActive('link')) {
      // A link on nothing selected shows its own address, so that the link is visible.
      editor
        .chain()
        .focus()
        .insertContent({
          type: 'text',
          text: linkText.trim(),
          marks: [{ type: 'link', attrs: { href } }],
        })
        .run();
    } else {
      editor.chain().focus().extendMarkRange('link').setLink({ href }).run();
    }
    setLinkOpen(false);
  };

  const plain = value.plain;

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div
        role="toolbar"
        aria-label={t('compose.toolbar')}
        className="flex min-h-[50px] flex-wrap items-center gap-1 border-b border-subtle px-3 py-1"
      >
        {!plain && editor && (
          <>
            <ToolbarButton
              label={t('compose.bold')}
              pressed={active?.bold}
              onClick={() => editor.chain().focus().toggleBold().run()}
            >
              <span aria-hidden className="text-base font-bold">
                {t('compose.boldMark')}
              </span>
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.italic')}
              pressed={active?.italic}
              onClick={() => editor.chain().focus().toggleItalic().run()}
            >
              <span aria-hidden className="text-base italic">
                {t('compose.italicMark')}
              </span>
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.underline')}
              pressed={active?.underline}
              onClick={() => editor.chain().focus().toggleUnderline().run()}
            >
              <span aria-hidden className="text-base underline">
                {t('compose.underlineMark')}
              </span>
            </ToolbarButton>
            <span aria-hidden className="mx-1 h-5 w-px bg-line" />
            <ToolbarButton
              label={t('compose.bulletList')}
              pressed={active?.bulletList}
              onClick={() => editor.chain().focus().toggleBulletList().run()}
            >
              <List size={18} strokeWidth={1.8} aria-hidden />
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.orderedList')}
              pressed={active?.orderedList}
              onClick={() => editor.chain().focus().toggleOrderedList().run()}
            >
              <ListOrdered size={18} strokeWidth={1.8} aria-hidden />
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.quote')}
              pressed={active?.blockquote}
              onClick={() => editor.chain().focus().toggleBlockquote().run()}
            >
              <Quote size={18} strokeWidth={1.8} aria-hidden />
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.link')}
              pressed={active?.link || linkOpen}
              onClick={openLink}
            >
              <LinkIcon size={18} strokeWidth={1.8} aria-hidden />
            </ToolbarButton>
            <ToolbarButton
              label={t('compose.code')}
              pressed={active?.code}
              onClick={() => editor.chain().focus().toggleCode().run()}
            >
              <Code size={18} strokeWidth={1.8} aria-hidden />
            </ToolbarButton>
          </>
        )}
        <button
          type="button"
          aria-pressed={plain}
          onClick={plain ? toRich : toPlain}
          className="ml-auto inline-flex h-8 items-center rounded-xs border border-control px-3 text-meta text-text-2 transition-colors hover:bg-bg aria-pressed:bg-accent-soft aria-pressed:text-accent max-md:h-11"
        >
          {t('compose.plainText')}
        </button>
      </div>

      {!plain && linkOpen && (
        <form
          className="flex items-center gap-2 border-b border-subtle px-4 py-2"
          onSubmit={(e) => {
            e.preventDefault();
            applyLink();
          }}
        >
          <input
            autoFocus
            type="text"
            inputMode="url"
            autoComplete="off"
            aria-label={t('compose.linkAddress')}
            aria-invalid={linkError || undefined}
            aria-describedby={linkError ? 'compose-link-error' : undefined}
            value={linkText}
            onChange={(e) => {
              setLinkText(e.target.value);
              setLinkError(false);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                e.preventDefault();
                setLinkOpen(false);
                editor?.commands.focus();
              }
            }}
            className="h-9 min-w-0 flex-1 rounded-sm border border-field bg-surface px-3 text-base text-ink placeholder:text-muted focus:border-accent focus:outline-2 focus:outline-accent aria-invalid:border-warning max-md:h-11"
          />
          <button
            type="submit"
            className="inline-flex h-9 items-center rounded-sm bg-accent px-3 text-secondary font-semibold text-on-accent hover:bg-accent-hover max-md:h-11"
          >
            {t('compose.linkApply')}
          </button>
          {linkError && (
            <p id="compose-link-error" role="alert" className="text-meta text-warning">
              {t('compose.linkInvalid')}
            </p>
          )}
        </form>
      )}

      {plain ? (
        <textarea
          aria-label={t('compose.body')}
          value={value.text}
          onChange={(e) => onChange({ plain: true, html: '', text: e.target.value })}
          className="min-h-[320px] w-full flex-1 resize-none bg-transparent px-6 py-5 text-message leading-[1.65] text-body outline-none"
        />
      ) : (
        <div
          className={cn(
            'min-h-[320px] flex-1 cursor-text text-message leading-[1.65] text-body',
            '[&_ol]:my-3 [&_ol]:list-decimal [&_ol]:pl-6 [&_p]:my-0 [&_p+p]:mt-3 [&_ul]:my-3 [&_ul]:list-disc [&_ul]:pl-6',
            '[&_blockquote]:my-3 [&_blockquote]:border-l-2 [&_blockquote]:border-line [&_blockquote]:pl-4 [&_blockquote]:text-text-3',
            '[&_a]:text-accent [&_a]:underline [&_code]:rounded-xs [&_code]:bg-bg [&_code]:px-1 [&_code]:text-secondary',
          )}
          onClick={(e) => {
            if (e.target === e.currentTarget) editor?.commands.focus('end');
          }}
        >
          <EditorContent editor={editor} className="min-h-full" />
        </div>
      )}
    </div>
  );
}
