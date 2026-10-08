import { render, screen } from '@testing-library/react';
import { ProgressBar } from './ProgressBar';

describe('ProgressBar', () => {
  it('exposes value and name', () => {
    render(<ProgressBar aria-label="Upload" value={40} />);
    const bar = screen.getByRole('progressbar', { name: 'Upload' });
    expect(bar).toHaveAttribute('aria-valuenow', '40');
    expect(bar.firstElementChild).toHaveStyle({ width: '40%' });
  });

  it('clamps out-of-range values', () => {
    render(<ProgressBar aria-label="Upload" value={150} />);
    expect(screen.getByRole('progressbar').firstElementChild).toHaveStyle({ width: '100%' });
  });
});
