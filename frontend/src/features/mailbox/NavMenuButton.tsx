import { Menu } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '@/shared/ui';
import { useShell } from './shell-context';

/** The "menu" button that opens the folder drawer on mobile; renders nothing where the folders are always visible. */
export function NavMenuButton() {
  const { t } = useTranslation();
  const { layout, openNav } = useShell();
  if (layout !== 'mobile') return null;
  return (
    <IconButton aria-label={t('mailbox.menu')} onClick={openNav}>
      <Menu size={20} strokeWidth={1.8} aria-hidden />
    </IconButton>
  );
}
