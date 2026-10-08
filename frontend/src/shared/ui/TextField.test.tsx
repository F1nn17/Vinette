import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TextField } from './TextField';

describe('TextField', () => {
  it('is labelled and accepts input', async () => {
    render(<TextField label="Email" />);
    const input = screen.getByLabelText('Email');

    await userEvent.type(input, 'a@b.c');

    expect(input).toHaveValue('a@b.c');
  });

  it('links the error message to the input', () => {
    render(<TextField label="Email" error="Required" />);
    const input = screen.getByLabelText('Email');
    expect(input).toBeInvalid();
    expect(input).toHaveAccessibleDescription('Required');
  });

  it('renders a trailing slot', () => {
    render(<TextField label="Password" endSlot={<button type="button">Show</button>} />);
    expect(screen.getByRole('button', { name: 'Show' })).toBeInTheDocument();
  });
});
