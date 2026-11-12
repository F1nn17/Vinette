// Every source file of the application as text (tests and generated types excluded: they may talk about the forbidden things).
const files = import.meta.glob<string>(
  ['/src/**/*.{ts,tsx}', '!/src/**/*.test.{ts,tsx}', '!/src/shared/api/schema.d.ts'],
  { query: '?raw', import: 'default', eager: true },
);

describe('mail content reaches the screen only through the frame', () => {
  const forbidden = [
    /dangerouslySetInnerHTML/u,
    /\.innerHTML\s*=/u,
    /\.outerHTML\s*=/u,
    /insertAdjacentHTML/u,
    /document\.write\s*\(/u,
    /\.srcdoc\s*=/u,
  ];

  it.each(Object.entries(files))('%s does not write HTML into the page', (_name, text) => {
    for (const pattern of forbidden) {
      expect(text, String(pattern)).not.toMatch(pattern);
    }
  });

  it('finds the sources it is meant to guard', () => {
    expect(Object.keys(files).length).toBeGreaterThan(50);
  });
});
