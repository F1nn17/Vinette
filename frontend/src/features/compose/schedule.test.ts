import {
  formatWhen,
  fromLocalInput,
  isLaterEnough,
  MIN_LATER_MINUTES,
  presetsFrom,
  toLocalInput,
} from './schedule';

const at = (y: number, m: number, d: number, h = 12, min = 0) => new Date(y, m - 1, d, h, min, 0);

describe('toLocalInput and fromLocalInput', () => {
  it('write and read the form a time field takes, in local time and with leading zeros', () => {
    expect(toLocalInput(at(2026, 3, 5, 7, 4))).toBe('2026-03-05T07:04');
    expect(fromLocalInput('2026-03-05T07:04')?.getTime()).toBe(at(2026, 3, 5, 7, 4).getTime());
  });

  it('read nothing from an empty or odd value', () => {
    for (const value of ['', '2026-03-05', 'tomorrow', '2026-13-45T25:61']) {
      expect(fromLocalInput(value)).toBeNull();
    }
  });
});

describe('presetsFrom', () => {
  const keys = (now: Date) => presetsFrom(now).map((p) => p.key);

  it('offers an hour from now, tomorrow at 8 and the coming Monday at 8', () => {
    const wednesday = at(2026, 11, 18, 15, 37); // a Wednesday

    const presets = presetsFrom(wednesday);

    expect(presets.map((p) => p.key)).toEqual(['inHour', 'tomorrow', 'monday']);
    expect(presets[0]!.at).toEqual(at(2026, 11, 18, 16, 37));
    expect(presets[1]!.at).toEqual(at(2026, 11, 19, 8));
    expect(presets[2]!.at).toEqual(at(2026, 11, 23, 8)); // the Monday after
  });

  it('on a Monday the next Monday is a week away, never today', () => {
    expect(presetsFrom(at(2026, 11, 16, 9))[2]!.at).toEqual(at(2026, 11, 23, 8));
  });

  it('on a Sunday tomorrow is Monday: there is no second offer for the same time', () => {
    expect(keys(at(2026, 11, 22, 20))).toEqual(['inHour', 'tomorrow']);
  });

  it('on a Saturday Monday is after tomorrow', () => {
    expect(presetsFrom(at(2026, 11, 21, 10)).at(-1)!.at).toEqual(at(2026, 11, 23, 8));
  });

  it('puts every offer ahead, with the hour offered on a whole minute', () => {
    const now = at(2026, 11, 18, 23, 59);
    now.setSeconds(40);

    for (const p of presetsFrom(now)) {
      expect(isLaterEnough(p.at, now)).toBe(true);
    }
    expect(presetsFrom(now)[0]!.at.getSeconds()).toBe(0);
  });
});

describe('isLaterEnough', () => {
  const now = at(2026, 11, 18, 12);

  it('wants a little more than the server asks for', () => {
    expect(isLaterEnough(at(2026, 11, 18, 12, MIN_LATER_MINUTES - 1), now)).toBe(false);
    expect(isLaterEnough(at(2026, 11, 18, 12, MIN_LATER_MINUTES), now)).toBe(true);
    expect(isLaterEnough(at(2026, 11, 18, 11), now)).toBe(false);
  });
});

describe('formatWhen', () => {
  it('writes the weekday, the date and the time in the language of the screen', () => {
    expect(formatWhen(at(2026, 11, 19, 8), 'ru')).toMatch(/чт.*19 нояб.*08:00/);
    expect(formatWhen(at(2026, 11, 19, 8), 'en')).toMatch(/Thu.*Nov 19.*8:00/);
  });
});
