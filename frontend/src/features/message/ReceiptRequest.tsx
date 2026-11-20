import { useMutation, useQueryClient } from '@tanstack/react-query';
import { MailCheck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { apiRequest } from '@/shared/api/client';
import { describeApiError } from '@/shared/api/describe-error';
import { useToast } from '@/shared/toast';
import { Button } from '@/shared/ui';
import { messageKey, type MessageDetail } from './api';

/**
 * Answers the sender's request for a read receipt (`POST /messages/{id}/receipt`): send it, or decline. Either way the question is not
 * asked again, so the open messages are marked at once; on a refusal of the server they stay as they were and the reader sees why.
 */
function useReceiptAnswer(id: string) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const { t } = useTranslation();
  return useMutation({
    mutationFn: (send: boolean) =>
      apiRequest<void>('POST', `/api/v1/messages/${encodeURIComponent(id)}/receipt`, {
        body: { send },
      }),
    onSuccess: (_result, send) => {
      queryClient.setQueriesData<MessageDetail>({ queryKey: messageKey(id) }, (old) =>
        old ? { ...old, receiptRequested: false } : old,
      );
      if (send) toast.show({ message: t('reader.receipt.sentToast') });
    },
  });
}

/**
 * The question above the text of a message whose sender asks to be told it was read (DESIGN.md §5, VIN-106): nothing is sent unless the
 * reader chooses to, because the answer tells the sender when the message was opened. Shown only when the server says the request is
 * the sender's own and has not been answered.
 */
export function ReceiptRequest({ message }: { message: MessageDetail }) {
  return message.receiptRequested ? <Question message={message} /> : null;
}

/** The question itself; it needs the toasts and the mutation, so it exists only for a message that asks. */
function Question({ message }: { message: MessageDetail }) {
  const { t } = useTranslation();
  const answer = useReceiptAnswer(message.id);

  return (
    <section
      aria-label={t('reader.receipt.title')}
      className="flex flex-wrap items-center gap-3 rounded-md bg-bg px-4 py-3 text-secondary text-text-2"
    >
      <MailCheck size={18} strokeWidth={1.8} aria-hidden className="shrink-0 text-muted" />
      <p className="min-w-0 flex-1">{t('reader.receipt.question')}</p>
      <div className="flex flex-wrap gap-2">
        <Button
          variant="secondary"
          size="sm"
          className="max-md:h-11"
          disabled={answer.isPending}
          onClick={() => answer.mutate(true)}
        >
          {t('reader.receipt.send')}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          className="max-md:h-11"
          disabled={answer.isPending}
          onClick={() => answer.mutate(false)}
        >
          {t('reader.receipt.decline')}
        </Button>
      </div>
      {answer.isError && (
        <p role="alert" className="basis-full text-warning">
          {describeApiError(answer.error, t)}
        </p>
      )}
    </section>
  );
}
