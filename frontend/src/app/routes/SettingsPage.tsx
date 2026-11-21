import { useTranslation } from 'react-i18next';
import { useSession } from '@/features/auth';
import { AppShell, NavMenuButton, Sidebar, useLayout } from '@/features/mailbox';
import { SettingsScreen } from '@/features/settings';

/** `/settings`: the settings in the wide area beside the folders (DESIGN.md §5, "Настройки"). */
export function SettingsPage() {
  const { t } = useTranslation();
  const { address, signOut } = useSession();
  const mobile = useLayout() === 'mobile';
  return (
    <AppShell
      messageOpen
      nav={<Sidebar address={address ?? ''} folderSlug="" onSignOut={() => void signOut()} />}
      reader={
        <div className="h-full overflow-y-auto">
          <div className="mx-auto flex max-w-[720px] flex-col gap-5 px-10 py-7 max-md:px-4 max-md:py-4">
            <div className="flex items-center gap-2">
              {mobile && <NavMenuButton />}
              <h1 className="text-heading font-bold text-ink">{t('settings.title')}</h1>
            </div>
            <SettingsScreen />
          </div>
        </div>
      }
    />
  );
}
