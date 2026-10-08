import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Checkbox } from './Checkbox';

describe('Checkbox', () => {
  it('toggles via its label', async () => {
    render(<Checkbox label="Stay signed in" />);
    const box = screen.getByRole('checkbox', { name: 'Stay signed in' });
    expect(box).not.toBeChecked();

    await userEvent.click(screen.getByText('Stay signed in'));

    expect(box).toBeChecked();
  });
});
