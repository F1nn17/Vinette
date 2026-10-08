import { cleanup, render, screen } from '@testing-library/react';
import { Logo } from './Logo';

function setBranding(branding: object | null) {
  document.getElementById('vinette-config')?.remove();
  if (branding) {
    const el = document.createElement('script');
    el.id = 'vinette-config';
    el.type = 'application/json';
    el.textContent = JSON.stringify({ branding });
    document.head.appendChild(el);
  }
}

describe('Logo', () => {
  afterEach(() => {
    cleanup();
    setBranding(null);
  });

  it('shows the default mark and name', () => {
    render(<Logo />);
    expect(screen.getByTestId('logo-mark')).toBeInTheDocument();
    expect(screen.getByText('Vinette')).toBeInTheDocument();
  });

  it('shows a letter tile for a custom name without a logo', () => {
    setBranding({ name: 'Acme Mail' });
    render(<Logo />);
    expect(screen.getByTestId('logo-tile')).toHaveTextContent('A');
    expect(screen.getByText('Acme Mail')).toBeInTheDocument();
    expect(screen.queryByTestId('logo-mark')).not.toBeInTheDocument();
  });

  it('shows the custom logo image', () => {
    setBranding({ name: 'Acme Mail', logoUrl: '/branding/logo' });
    const { container } = render(<Logo />);
    expect(container.querySelector('img')).toHaveAttribute('src', '/branding/logo');
  });

  it('keeps an accessible name when the text is hidden', () => {
    render(<Logo showName={false} />);
    expect(screen.getByRole('img', { name: 'Vinette' })).toBeInTheDocument();
  });
});
