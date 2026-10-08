import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TextArea } from './TextArea';

describe('TextArea', () => {
  it('is labelled and accepts input', async () => {
    render(<TextArea label="Message" hint="Be brief" />);
    const area = screen.getByLabelText('Message');

    await userEvent.type(area, 'Hello');

    expect(area).toHaveValue('Hello');
    expect(area).toHaveAccessibleDescription('Be brief');
  });
});
