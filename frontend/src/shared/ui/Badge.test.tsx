import { render, screen } from '@testing-library/react';
import { Badge } from './Badge';

describe('Badge', () => {
  it.each(['success', 'warning'] as const)('renders the %s tone', (tone) => {
    render(<Badge tone={tone}>Status</Badge>);
    expect(screen.getByText('Status')).toHaveAttribute('data-tone', tone);
  });
});
