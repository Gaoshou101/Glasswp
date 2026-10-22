import type { Snapshot } from './contract';

/** A late response/finally can affect only the request that still owns the connection. */
export class CurrentRequest {
  private active?: AbortController;
  get pending() { return !!this.active; }
  begin() { this.cancel(); return this.active = new AbortController(); }
  current(controller: AbortController) { return this.active === controller && !controller.signal.aborted; }
  owns(controller: AbortController) { return this.active === controller; }
  finish(controller: AbortController) { if (this.active === controller) this.active = undefined; }
  cancel() { this.active?.abort(); this.active = undefined; }
}

export class SnapshotChanges {
  private keys = new Map<string, string>();
  reset() { this.keys.clear(); }
  changed(snapshot: Snapshot, appearance: string) {
    const changed = { hardware: false, quota: false, weather: false };
    const revisions = snapshot.revisions;
    for (const module of ['hardware', 'quota', 'weather'] as const) {
      // Old bridges remain compatible. A new bridge supplies O(1) per-source revisions.
      const key = appearance + ':' + (revisions ? revisions.epoch + ':' + revisions[module] : JSON.stringify(snapshot[module]));
      changed[module] = this.keys.get(module) !== key;
      this.keys.set(module, key);
    }
    return changed;
  }
}
