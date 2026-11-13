import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@/shared/ui';

/** A question with a way out (cancel) and a way on (the action), for what cannot be taken back. */
export function Confirm({
  title,
  text,
  confirm,
  onConfirm,
  onClose,
}: {
  title: string;
  text: string;
  confirm: string;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  return (
    <Dialog open onClose={onClose} title={title} closeLabel={t('common.close')}>
      <p className="text-base text-text-2">{text}</p>
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button onClick={onConfirm}>{confirm}</Button>
      </div>
    </Dialog>
  );
}
