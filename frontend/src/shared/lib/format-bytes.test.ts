import { formatBytes } from './format-bytes';

const GB = 1024 ** 3;

describe('formatBytes', () => {
  it('picks the unit and keeps one decimal at most', () => {
    expect(formatBytes(0, 'en')).toBe('0 byte');
    expect(formatBytes(1536, 'en')).toBe('1.5 kB');
    expect(formatBytes(5 * 1024 * 1024, 'en')).toBe('5 MB');
    expect(formatBytes(2.1 * GB, 'en')).toBe('2.1 GB');
    expect(formatBytes(10 * GB, 'en')).toBe('10 GB');
  });

  it('writes the number and the unit in the language of the interface', () => {
    expect(formatBytes(2.1 * GB, 'ru')).toMatch(/^2,1\s(?:ГБ|Гбайт)$/u);
  });

  it('does not go negative', () => {
    expect(formatBytes(-5, 'en')).toBe('0 byte');
  });
});
