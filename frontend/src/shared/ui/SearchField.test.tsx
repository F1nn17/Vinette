import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SearchField } from './SearchField';

describe('SearchField', () => {
  it('is a search box with an accessible name and passes input through', async () => {
    const onChange = vi.fn();
    render(<SearchField aria-label="Search mail" placeholder="Search" onChange={onChange} />);

    const field = screen.getByRole('searchbox', { name: 'Search mail' });
    await userEvent.type(field, 'ab');

    expect(onChange).toHaveBeenCalledTimes(2);
    expect(field).toHaveAttribute('placeholder', 'Search');
  });

  it('can be disabled', () => {
    render(<SearchField aria-label="Search mail" disabled />);

    expect(screen.getByRole('searchbox')).toBeDisabled();
  });
});
