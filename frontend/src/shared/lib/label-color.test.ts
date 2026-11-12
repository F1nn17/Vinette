import { describe, expect, it } from 'vitest';
import { labelColor } from './label-color';

describe('labelColor', () => {
  it('gives the same label the same color every time', () => {
    expect(labelColor('Работа')).toBe(labelColor('Работа'));
  });

  it('uses the color the user chose', () => {
    expect(labelColor('Работа', { Работа: 'personal' })).toBe('personal');
    expect(labelColor('Work', { Work: 'bills' })).toBe('bills');
  });

  it('compares the name without regard to case', () => {
    expect(labelColor('WORK', { work: 'bills' })).toBe('bills');
  });

  it('ignores a color that is not in the palette, and a label that has none chosen', () => {
    expect(labelColor('Work', { Work: 'purple' })).toBe(labelColor('Work'));
    expect(labelColor('Work', { Other: 'bills' })).toBe(labelColor('Work'));
    expect(labelColor('Work', {})).toBe(labelColor('Work'));
  });
});
