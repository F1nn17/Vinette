import { colorsOf, stateOf } from './label-state';

describe('stateOf', () => {
  it('says "all" when every message has the label', () => {
    expect(stateOf('Work', [['Work'], ['Work', 'Bills']])).toBe('all');
  });

  it('says "some" when only part of the messages have it', () => {
    expect(stateOf('Work', [['Work'], []])).toBe('some');
  });

  it('says "none" when no message has it, and for no messages at all', () => {
    expect(stateOf('Work', [['Bills'], []])).toBe('none');
    expect(stateOf('Work', [])).toBe('none');
  });

  it('compares names without regard to case', () => {
    expect(stateOf('work', [['WORK']])).toBe('all');
    expect(stateOf('Работа', [['работа']])).toBe('all');
  });
});

describe('colorsOf', () => {
  it('maps the names that have a chosen color and leaves out the others', () => {
    expect(
      colorsOf([
        { name: 'Work', color: 'work' },
        { name: 'Home', color: null },
      ]),
    ).toEqual({ Work: 'work' });
  });

  it('is empty before the labels are known', () => {
    expect(colorsOf(undefined)).toEqual({});
  });
});
