import { render, screen } from '@testing-library/react';
import { LabelDot, LabelTag } from './LabelTag';

describe('LabelTag', () => {
  it.each(['work', 'bills', 'personal'] as const)('renders the %s label', (color) => {
    render(<LabelTag color={color}>Tag</LabelTag>);
    expect(screen.getByText('Tag')).toHaveAttribute('data-color', color);
  });

  it('renders a decorative dot', () => {
    const { container } = render(<LabelDot color="work" />);
    expect(container.firstChild).toHaveAttribute('aria-hidden', 'true');
  });
});
