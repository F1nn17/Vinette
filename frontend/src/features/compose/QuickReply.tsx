import { useQueryClient } from '@tanstack/react-query';
import { Send } from 'lucide-react';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { apiPost } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import type { components } from '@/shared/api/schema';
import { useSignatures } from '@/features/settings';
import { Button } from '@/shared/ui';
import { useToast } from '@/shared/toast';
import { sendBlocker } from './send';
import { startOf, withTypedText } from './reply';
import { htmlWithSignature } from './signature';
import { quoteQuery } from './useQuote';

type Sent = components['schemas']['SentDto'];

export interface QuickReplyProps {
  /** The message that is answered. */
  messageId: string;
  /** How the sender is called (the name, or the address when there is no name): "Reply: Anna". */
  senderName: string;
  /** The signed-in mailbox: it is never a recipient of its own answer. */
  address: string;
}

/**
 * The quick reply under an open message (DESIGN.md §5): a card with a text field, "Open in editor" and "Send". It answers the sender
 * (or where the sender asked answers to go) with the original quoted, in the conversation of the message, exactly as "Reply" would; the
 * message is read when the answer is sent, not before. "Open in editor" takes what was typed into the editor, above the quote.
 */
export function QuickReply({ messageId, senderName, address }: QuickReplyProps) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const signatures = useSignatures();
  const fieldId = useId();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const busyRef = useRef(false);
  const [problem, setProblem] = useState<string | null>(null);
  const empty = text.trim() === '';

  const send = async () => {
    if (busyRef.current || empty) return;
    busyRef.current = true;
    setBusy(true);
    setProblem(null);
    try {
      const quote = await queryClient.fetchQuery(quoteQuery('reply', messageId));
      const start = withTypedText(startOf(quote, 'reply', address, t, i18n.language), text);
      if (sendBlocker(start.values, []) !== null) {
        setProblem(t('quickReply.noRecipient'));
        return;
      }
      const sent = await apiPost<Sent>('/api/v1/messages', {
        fromName: null,
        to: start.values.to,
        cc: [],
        bcc: [],
        subject: start.values.subject,
        text: null,
        html: htmlWithSignature(start.body.html, signatures.defaultSignature), // the default signature goes under a quick reply too
        attachmentIds: [],
        inReplyTo: start.inReplyTo,
        references: start.references,
        draftId: null,
      });
      toast.show({ message: t(sent.copy === 'saved' ? 'compose.sent' : 'compose.sentNoCopy') });
      setText('');
    } catch (error) {
      setProblem(describeApiError(error, t));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void send();
    }
  };

  return (
    <section className="rounded-lg border border-control bg-surface px-4 pt-3 pb-4">
      <label htmlFor={fieldId} className="text-secondary font-semibold text-text-2">
        {t('quickReply.title')}
      </label>
      <textarea
        id={fieldId}
        rows={3}
        value={text}
        disabled={busy}
        placeholder={t('quickReply.placeholder', { name: senderName })}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        className="mt-2 block min-h-20 w-full resize-y bg-transparent text-base leading-normal text-ink placeholder:text-muted focus:outline-2 focus:outline-offset-4 focus:outline-accent"
      />
      {problem && (
        <p role="alert" className="mt-2 text-secondary text-warning">
          {problem}
        </p>
      )}
      <div className="mt-3 flex items-center justify-between gap-3">
        <button
          type="button"
          disabled={busy}
          onClick={() =>
            void navigate(`/compose?reply=${encodeURIComponent(messageId)}`, {
              state: { typed: text },
            })
          }
          className="inline-flex min-h-10 items-center text-secondary text-accent hover:underline max-md:min-h-11"
        >
          {t('quickReply.openEditor')}
        </button>
        <Button
          disabled={busy || empty}
          onClick={() => void send()}
          icon={<Send size={18} strokeWidth={1.8} aria-hidden />}
        >
          {t(busy ? 'compose.sending' : 'compose.send')}
        </Button>
      </div>
    </section>
  );
}
