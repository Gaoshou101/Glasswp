import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ClockFormats, WorldClockFormats, clockNow, normalizeWorldZones, validTimezone } from '../src/clock-format';
import { applyClockConfig, defaults, loadSettings, validClockConfig } from '../src/settings';

test('world clocks follow the same instant across dates, fractional offsets and DST', () => {
  const formats = new WorldClockFormats();
  const winter = new Date('2026-01-01T02:30:00Z');
  const ny = formats.format('America/New_York', 'Asia/Shanghai', false, true, winter);
  assert.equal(ny.time, '21:30:00');
  assert.equal(ny.minutesOfDay, 1290);
  assert.equal(formats.format('UTC', 'UTC', true, true, new Date('2026-01-01T00:00:00Z')).minutesOfDay, 0);
  assert.equal(ny.dayDifference, -1);
  assert.equal(ny.offset, 'UTC-05:00');
  assert.equal(formats.format('America/New_York', 'Asia/Shanghai', false, false, new Date('2026-07-01T02:30:00Z')).offset, 'UTC-04:00');
  const kathmandu = formats.format('Asia/Kathmandu', 'UTC', false, false, winter);
  assert.equal(kathmandu.offset, 'UTC+05:45');
  assert.equal(kathmandu.time, '08:15');
  assert.equal(kathmandu.minutesOfDay, 495);
  assert.equal(formats.format('Pacific/Kiritimati', 'America/Adak', false, false, new Date('2026-01-01T10:30:00Z')).dayDifference, 1);
  assert.equal(formats.format('Asia/Shanghai', 'Asia/Shanghai', false, false, winter).dayLabel, '同日');
});

test('calibration corrects only the supplied display instant and clamps invalid offsets', () => {
  const now = Date.parse('2026-01-01T23:59:59Z');
  const corrected = clockNow(2.5, now);
  assert.equal(corrected.toISOString(), '2026-01-02T00:00:01.500Z');
  assert.equal(now, Date.parse('2026-01-01T23:59:59Z'));
  assert.equal(clockNow(NaN, now).getTime(), now);
  assert.equal(clockNow(999999, now).getTime(), now + 86400000);
  assert.equal(new ClockFormats().format('UTC', false, true, corrected).time, '00:00:01');
});

test('clock settings accept arbitrary Intl zones and reject unsafe persisted values', () => {
  assert.equal(validTimezone('Asia/Kathmandu'), true);
  assert.equal(validTimezone(' Mars/Olympus '), false);
  assert.equal(validTimezone(undefined), false);
  assert.equal(validTimezone(''), false);
  assert.deepEqual(normalizeWorldZones(['Asia/Tokyo', null, 'Invalid/Zone', ' Asia/Tokyo ', 'UTC']), ['Asia/Tokyo', 'UTC']);
  assert.deepEqual(normalizeWorldZones([]), []);
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: { getItem: (key: string) => key === 'wallpaper.dashboard.v1' ? JSON.stringify({ timezone: 'Mars/Olympus', offsetSeconds: 'bad', worldZones: ['UTC', 'evil'] }) : null } });
  try {
    const settings = loadSettings();
    assert.equal(settings.timezone, 'Asia/Shanghai');
    assert.equal(settings.offsetSeconds, 0);
    assert.deepEqual(settings.worldZones, ['UTC']);
  } finally { if (previous) Object.defineProperty(globalThis, 'localStorage', previous); else Reflect.deleteProperty(globalThis, 'localStorage'); }
});

test('management clock updates independently from data revisions and old bridges retain local settings', () => {
  const settings = defaults();
  assert.equal(applyClockConfig(settings, null), false);
  assert.equal(applyClockConfig(settings, undefined), false);
  const config = { timezone: 'Asia/Kathmandu', offsetSeconds: -1.25, seconds: true, hour12: true, worldZones: ['UTC', 'Asia/Tokyo'] };
  assert.equal(applyClockConfig(settings, config), true);
  assert.equal(settings.timezone, config.timezone);
  assert.equal(settings.offsetSeconds, config.offsetSeconds);
  assert.deepEqual(settings.worldZones, config.worldZones);
  assert.equal(applyClockConfig(settings, config), false);
  assert.equal(applyClockConfig(settings, { ...config, offsetSeconds: 5 }), true);
  assert.equal(settings.offsetSeconds, 5);
  assert.equal(applyClockConfig(settings, { ...config, timezone: '' }), false);
  assert.equal(validClockConfig({ ...config, offsetSeconds: NaN }), false);
  assert.equal(settings.timezone, 'Asia/Kathmandu');
  assert.equal(applyClockConfig(settings, { ...config, worldZones: [] }), true);
  assert.deepEqual(settings.worldZones, []);
});
