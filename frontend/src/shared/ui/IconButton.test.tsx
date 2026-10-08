import { render, screen } from '@testing-library/react';
import { Flag } from 'lucide-react';
import { IconButton } from './IconButton';

describe('IconButton', () => {
  it('is named by its aria-label', () => {
    render(
      <IconButton aria-label="Delete">
        <Flag />
      </IconButton>,
    );
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument();
  });

  it('exposes the toggle state', () => {
    render(
      <IconButton aria-label="Flag" pressed>
        <Flag />
      </IconButton>,
    );
    expect(screen.getByRole('button', { name: 'Flag' })).toHaveAttribute('aria-pressed', 'true');
  });
});
