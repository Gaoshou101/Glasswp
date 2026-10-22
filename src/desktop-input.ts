export interface DesktopInputPoint { x: number; y: number; width: number; height: number }

/** Some WE desktop hosts forward releases from covered windows without downs. */
export function installDesktopInput(root: HTMLElement, enabled: () => boolean, authorize: (point: DesktopInputPoint) => Promise<boolean>, active: () => boolean = () => true) {
  let pressed = false;
  let pending: number | undefined;
  let replaying = false;
  let replay: { x: number; y: number; at: number } | undefined;
  let generation = 0;
  const cancel = () => { clearTimeout(pending); pending = undefined; generation++; };
  const activate = async (node: HTMLElement, x: number, y: number, current: number) => {
    let allowed = false;
    try { allowed = await authorize({ x, y, width: innerWidth, height: innerHeight }); } catch { }
    if (!allowed || current !== generation || !enabled() || !active() || !node.isConnected || node.closest('[inert],:disabled')) return;
    replay = { x, y, at: performance.now() };
    replaying = true;
    try {
      node.closest<HTMLElement>('button,a,input,textarea,select,[tabindex]')?.focus({ preventScroll: true });
      const card = node.closest<HTMLElement>('.glass');
      if (root.dataset.dynamic === 'true' && card) {
        card.classList.add('liquid-pressed');
        window.setTimeout(() => card.classList.remove('liquid-pressed'), 100);
      }
      node.click();
    } finally { replaying = false; }
  };
  // Do not paint a press effect before the native desktop hit test has passed.
  window.addEventListener('pointerdown', event => { if (enabled() && event.isTrusted && root.contains(event.target as Node)) event.stopImmediatePropagation(); }, true);
  window.addEventListener('mousedown', event => {
    pressed = true; replay = undefined; cancel();
    if (enabled() && event.isTrusted && root.contains(event.target as Node)) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  window.addEventListener('blur', () => { pressed = false; replay = undefined; cancel(); });
  window.addEventListener('mouseup', event => {
    const hadDown = pressed; pressed = false;
    if (!enabled() || !active() || hadDown || event.button !== 0) return;
    const target = event.target instanceof Element ? event.target : undefined;
    if (!target || !root.contains(target) || target.closest('[inert],:disabled')) return;
    const bounds = root.getBoundingClientRect(), x = event.clientX, y = event.clientY;
    // WE also forwards releases from other monitors to the document element.
    if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight || x < bounds.left || x >= bounds.right || y < bounds.top || y >= bounds.bottom) return;
    const node = target instanceof HTMLElement ? target : target.closest<HTMLElement>('button,a,[data-detail-title]');
    if (!node) return;
    cancel();
    const current = generation;
    // Give a native click from this release the first opportunity to activate.
    pending = window.setTimeout(() => {
      pending = undefined;
      void activate(node, x, y, current);
    }, 0);
  }, true);
  window.addEventListener('click', event => {
    if (replaying || !enabled()) return;
    cancel();
    // A late host click following our replay must not toggle/close the new panel.
    if (replay && event.isTrusted && event.detail > 0 && performance.now() - replay.at < 500 && Math.abs(event.clientX - replay.x) <= 4 && Math.abs(event.clientY - replay.y) <= 4) {
      event.preventDefault(); event.stopImmediatePropagation(); replay = undefined;
      return;
    }
    // Guard real host clicks too; keyboard and our own approved replay remain native.
    if (!event.isTrusted || event.detail === 0 || !(event.target instanceof Element) || !root.contains(event.target)) return;
    event.preventDefault(); event.stopImmediatePropagation();
    const node = event.target instanceof HTMLElement ? event.target : event.target.closest<HTMLElement>('button,a,[data-detail-title]');
    if (node && active()) void activate(node, event.clientX, event.clientY, generation);
  }, true);
  return { cancel };
}

/** Fail closed if the bridge is unavailable, outdated, or cannot check native windows. */
export async function authorizeDesktopInput(bridgeUrl: string, readToken: string, point: DesktopInputPoint) {
  if (!readToken) return false;
  const controller = new AbortController(), timeout = window.setTimeout(() => controller.abort(), 500);
  try {
    const response = await fetch(bridgeUrl + '/v1/desktop-input', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Wallpaper-Token': readToken }, body: JSON.stringify(point), signal: controller.signal, cache: 'no-store' });
    if (!response.ok) return false;
    const result = await response.json();
    return result.available === true && result.allowed === true;
  } catch { return false; }
  finally { clearTimeout(timeout); }
}
