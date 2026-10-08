import { render, screen } from '@testing-library/react';
import { initials } from '../lib/initials';
import { Avatar } from './Avatar';

describe('Avatar', () => {
  it('shows initials of the first two words', () => {
    render(<Avatar name="Anna Smirnova Petrovna" />);
    expect(screen.getByRole('img', { name: 'Anna Smirnova Petrovna' })).toHaveTextContent('AS');
  });

  it('handles a single word and empty names', () => {
    expect(initials('madonna')).toBe('M');
    expect(initials('  ')).toBe('');
  });
});
