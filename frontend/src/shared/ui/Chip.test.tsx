import { render, screen } from '@testing-library/react';
import { Chip } from './Chip';

describe('Chip', () => {
  it('reflects the pressed state', () => {
    render(
      <>
        <Chip pressed>All</Chip>
        <Chip pressed={false}>Unread</Chip>
      </>,
    );
    expect(screen.getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Unread' })).toHaveAttribute('aria-pressed', 'false');
  });
});
