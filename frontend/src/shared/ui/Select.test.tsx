import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Select } from './Select';

describe('Select', () => {
  it('is labelled and changes value', async () => {
    render(
      <Select label="From" defaultValue="a">
        <option value="a">a@x.test</option>
        <option value="b">b@x.test</option>
      </Select>,
    );
    const select = screen.getByLabelText('From');

    await userEvent.selectOptions(select, 'b');

    expect(select).toHaveValue('b');
  });
});
