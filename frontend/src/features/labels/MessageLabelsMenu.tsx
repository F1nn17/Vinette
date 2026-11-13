import { useLabels, useSetMessageLabels } from './api';
import { LabelMenu } from './LabelMenu';
import { stateOf } from './label-state';

export interface MessageLabelsMenuProps {
  /** The messages the labels are put on (or taken off): the open one, or the selected ones. */
  ids: string[];
  /** The labels each of those messages has now (as the list or the open message shows them). */
  labelsOfMessages: (readonly string[])[];
  onClose: () => void;
  className?: string;
}

/**
 * The "Labels" panel of a message or of a selection: puts a label on all the messages or takes it off them (a box that is
 * half full means some have it), and makes a new label from a name typed at the bottom.
 */
export function MessageLabelsMenu({
  ids,
  labelsOfMessages,
  onClose,
  className,
}: MessageLabelsMenuProps) {
  const labels = useLabels();
  const setLabels = useSetMessageLabels();
  const known = labels.data ?? [];
  return (
    <LabelMenu
      labels={known}
      stateOf={(name) => stateOf(name, labelsOfMessages)}
      onToggle={(name, on) =>
        setLabels.mutate({ ids, ...(on ? { add: [name] } : { remove: [name] }) })
      }
      onCreate={(name) => {
        // A name that is a label already (capitals aside) is that label, not a second one.
        const existing = known.find((l) => l.name.toLowerCase() === name.toLowerCase());
        setLabels.mutate({ ids, add: [existing?.name ?? name] });
      }}
      onClose={onClose}
      className={className}
    />
  );
}
