import { Archive, Flag, Pencil, Trash2 } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import {
  Avatar,
  Badge,
  Button,
  Checkbox,
  Chip,
  IconButton,
  LabelDot,
  LabelTag,
  Logo,
  ProgressBar,
  Select,
  TextArea,
  TextField,
} from '@/shared/ui';

/** Dev-only catalogue of UI primitives in all states (route exists only in dev builds). */
function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-4 border-b border-subtle py-6">
      <h2 className="text-caps font-semibold tracking-[0.05em] text-muted uppercase">{title}</h2>
      <div className="flex flex-wrap items-start gap-4">{children}</div>
    </section>
  );
}

export function DevUiPage() {
  const [flagged, setFlagged] = useState(false);
  const [filter, setFilter] = useState('all');

  return (
    <main className="mx-auto max-w-5xl bg-surface px-8 py-6">
      <h1 className="text-heading font-bold text-ink">UI primitives</h1>

      <Section title="Button">
        <Button>Primary</Button>
        <Button variant="secondary">Secondary</Button>
        <Button variant="ghost">Ghost</Button>
        <Button icon={<Pencil size={18} strokeWidth={1.8} />}>With icon</Button>
        <Button disabled>Disabled</Button>
        <Button variant="secondary" disabled>
          Disabled
        </Button>
        <Button size="sm" variant="secondary">
          Small 36
        </Button>
        <Button size="lg">Large 44</Button>
        <Button size="xl">Extra 48</Button>
      </Section>

      <Section title="IconButton">
        <IconButton aria-label="Archive">
          <Archive size={18} strokeWidth={1.8} />
        </IconButton>
        <IconButton aria-label="Delete" variant="secondary">
          <Trash2 size={18} strokeWidth={1.8} />
        </IconButton>
        <IconButton aria-label="Flag" pressed={flagged} onClick={() => setFlagged(!flagged)}>
          <Flag size={18} strokeWidth={1.8} fill={flagged ? 'currentColor' : 'none'} />
        </IconButton>
        <IconButton aria-label="Disabled" disabled>
          <Archive size={18} strokeWidth={1.8} />
        </IconButton>
      </Section>

      <Section title="Chip">
        {['all', 'unread', 'flagged'].map((id) => (
          <Chip key={id} pressed={filter === id} onClick={() => setFilter(id)}>
            {id}
          </Chip>
        ))}
      </Section>

      <Section title="LabelTag">
        <LabelTag color="work">Work</LabelTag>
        <LabelTag color="bills">Bills</LabelTag>
        <LabelTag color="personal">Personal</LabelTag>
        <LabelDot color="work" />
        <LabelDot color="bills" />
        <LabelDot color="personal" />
      </Section>

      <Section title="Avatar">
        <Avatar name="Anna Smirnova" />
        <Avatar name="Anna Smirnova" size={40} tone="neutral" />
        <Avatar name="Ivan" size={28} />
      </Section>

      <Section title="Badge">
        <Badge tone="success">Verified sender</Badge>
        <Badge tone="warning">Suspicious</Badge>
      </Section>

      <Section title="Fields">
        <TextField label="Email" placeholder="user@example.com" className="w-72" />
        <TextField label="With hint" hint="Helper text" className="w-72" />
        <TextField
          label="With error"
          error="Something is wrong"
          defaultValue="oops"
          className="w-72"
        />
        <TextField label="Disabled" disabled defaultValue="locked" className="w-72" />
        <Select label="From" className="w-72">
          <option>user@example.com</option>
          <option>other@example.com</option>
        </Select>
        <TextArea label="Message" placeholder="Write here" className="w-72" />
      </Section>

      <Section title="Checkbox">
        <Checkbox label="Unchecked" />
        <Checkbox label="Checked" defaultChecked />
        <Checkbox label="Disabled" disabled />
      </Section>

      <Section title="ProgressBar">
        <div className="w-72">
          <ProgressBar aria-label="Upload" value={64} />
        </div>
        <div className="w-72">
          <ProgressBar aria-label="Storage" value={21} tone="ink" />
        </div>
      </Section>

      <Section title="Logo">
        <div className="rounded-md bg-bg p-4">
          <Logo variant="light" />
        </div>
        <div className="rounded-md bg-panel p-4">
          <Logo variant="dark" />
        </div>
        <div className="rounded-md bg-accent p-4">
          <Logo variant="accent" />
        </div>
        <div className="rounded-md bg-bg p-4">
          <Logo size={16} showName={false} />
        </div>
      </Section>
    </main>
  );
}
