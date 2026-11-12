import { useTranslation } from 'react-i18next';
import { Button } from '@/shared/ui';
import { useTrustImageSender, verdictOf, type MessageDetail } from './api';

/**
 * "Always show pictures from this sender" and its undoing, next to the notice about hidden pictures. Offered only for a
 * message that takes pictures from the web and comes from someone whose sender check is "verified": for anyone else
 * the choice would have no effect (the server does not load pictures on trust alone, as the From line can be forged).
 * Withdrawing the trust is always possible.
 */
export function ImageTrust({ message }: { message: MessageDetail }) {
  const { t } = useTranslation();
  const trust = useTrustImageSender();
  const address = message.from?.address;
  if (!address || !message.hasExternalImages) return null;

  const trusted = message.imagesTrusted;
  if (!trusted && verdictOf(message) !== 'verified') return null;

  return (
    <div className="flex flex-wrap items-center gap-3 text-secondary text-text-2">
      <p className="min-w-0 flex-1">
        {trusted
          ? t('reader.imagesAlways', { address })
          : t('reader.imagesAlwaysOffer', { address })}
      </p>
      <Button
        variant="secondary"
        size="sm"
        className="max-md:h-11"
        disabled={trust.isPending}
        onClick={() => trust.mutate({ address, trusted: !trusted })}
      >
        {trusted ? t('reader.imagesAlwaysStop') : t('reader.imagesAlwaysStart')}
      </Button>
      {trust.isError && (
        <p role="alert" className="basis-full text-warning">
          {t('reader.imagesAlwaysFailed')}
        </p>
      )}
    </div>
  );
}
