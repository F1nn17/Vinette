import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useToast } from '@/shared/toast';
import { scheduledPath, useScheduled } from './api';

const SEEN_KEY = 'vinette.scheduled.told';

/** The notes of failures that were already told about in this session of the browser (so that a reload does not tell again). */
function told(): Set<string> {
  try {
    return new Set(JSON.parse(sessionStorage.getItem(SEEN_KEY) ?? '[]') as string[]);
  } catch {
    return new Set();
  }
}

function remember(ids: Set<string>) {
  try {
    sessionStorage.setItem(SEEN_KEY, JSON.stringify([...ids]));
  } catch {
    // the browser does not keep it: it is told again after a reload, which is harmless
  }
}

/**
 * Tells once, with a notice, that a message put off did not go (DESIGN.md §5, "Отложенные"): when the user signs in, or while the application is
 * open (the list is read every minute). A notice for each failure only once. Shows nothing on the screen itself.
 */
export function ScheduledWatcher() {
  const { t } = useTranslation();
  const toast = useToast();
  const navigate = useNavigate();
  const { data } = useScheduled();
  const known = useRef<Set<string> | null>(null);

  useEffect(() => {
    if (!data) return;
    known.current ??= told();
    const fresh = data.filter((i) => i.state === 'failed' && !known.current!.has(i.id));
    if (fresh.length === 0) return;
    for (const item of fresh) known.current.add(item.id);
    remember(known.current);
    toast.show({
      message: t('scheduled.failedNotice', { count: fresh.length }),
      tone: 'error',
      action: { label: t('scheduled.look'), onSelect: () => void navigate(scheduledPath()) },
    });
  }, [data, navigate, t, toast]);

  return null;
}
