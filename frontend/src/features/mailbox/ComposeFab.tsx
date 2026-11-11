import { Pencil } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

/** The floating "Compose" button of the mobile list: 56 px high, radius 18, shadow (DESIGN.md §8). */
export function ComposeFab() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  return (
    <button
      type="button"
      onClick={() => void navigate('/compose')}
      className="fixed right-4 bottom-4 z-10 inline-flex h-14 items-center gap-2 rounded-[18px] bg-accent px-6 text-base font-semibold text-on-accent shadow-fab transition-colors hover:bg-accent-hover"
    >
      <Pencil size={18} strokeWidth={1.8} aria-hidden />
      {t('sidebar.compose')}
    </button>
  );
}
