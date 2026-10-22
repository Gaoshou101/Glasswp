import test from 'node:test';
import assert from 'node:assert/strict';
import { CurrentRequest, SnapshotChanges } from '../src/snapshot-control';
import { ClockFormats } from '../src/clock-format';
import type { Snapshot } from '../src/contract';

test('late response/finally cannot commit or unlock a replacement request', () => {
  const requests = new CurrentRequest();
  const old = requests.begin();
  requests.cancel();
  const next = requests.begin();
  assert.equal(requests.current(old), false);
  requests.finish(old);
  assert.equal(requests.pending, true);
  assert.equal(requests.current(next), true);
  next.abort();
  assert.equal(requests.current(next), false);
  assert.equal(requests.owns(next), true); // Current timeout can still report an error.
  requests.finish(next);
  assert.equal(requests.pending, false);
});

test('sixty seconds of new hardware leave unchanged quota/weather calculations idle', () => {
  const snapshot = { revisions: { epoch: 'first', hardware: 0, quota: 0, weather: 0 } } as Snapshot;
  const changes = new SnapshotChanges();
  const counts = { hardware: 0, quota: 0, weather: 0 };
  for (let seconds = 0; seconds < 60; seconds += 2) {
    snapshot.revisions!.hardware++;
    const changed = changes.changed(snapshot, 'settings');
    for (const module of ['hardware','quota','weather'] as const) if (changed[module]) counts[module]++;
  }
  assert.deepEqual(counts, { hardware: 30, quota: 1, weather: 1 });
  snapshot.revisions!.quota++;
  assert.deepEqual(changes.changed(snapshot, 'settings'), { hardware: false, quota: true, weather: false });
  snapshot.revisions!.epoch = 'restarted';
  assert.deepEqual(changes.changed(snapshot, 'settings'), { hardware: true, quota: true, weather: true });
  assert.deepEqual(changes.changed(snapshot, 'changed-settings'), { hardware: true, quota: true, weather: true });
});

test('legacy snapshots still update same-time status transitions', () => {
  const snapshot = { hardware: { status: 'ok' }, quota: { status: 'ok' }, weather: { status: 'ok' } } as Snapshot;
  const changes = new SnapshotChanges();
  changes.changed(snapshot, '');
  assert.deepEqual(changes.changed(snapshot, ''), { hardware: false, quota: false, weather: false });
  snapshot.weather.status = 'stale';
  assert.deepEqual(changes.changed(snapshot, ''), { hardware: false, quota: false, weather: true });
});

test('clock formatter cache follows seconds, 12-hour display and timezone', () => {
  const cache = new ClockFormats(), now = new Date('2026-10-22T16:00:07Z');
  const first = cache.format('Asia/Shanghai', false, true, now);
  assert.ok(first.time.includes('00:00:07'));
  assert.ok(first.date.includes('2026.10.23'));
  assert.equal(cache.format('Asia/Shanghai', false, false, now).time, '00:00');
  assert.ok(cache.format('UTC', true, true, now).time.includes('04:00:07'));
});
