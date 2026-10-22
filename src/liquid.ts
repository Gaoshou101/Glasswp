/** One demand-driven light controller for every card. No per-card RAF loops. */
export class LiquidInteraction {
  private active?: HTMLElement;
  private bounds?: DOMRect;
  private frame = 0;
  private enabled = false;
  private fps = 30;
  private lastFrame = 0;
  private x = 0;
  private y = 0;
  private velocityX = 0;
  private velocityY = 0;
  private targetX = 0;
  private targetY = 0;
  private resting = new WeakMap<HTMLElement, { x: number; y: number; leftAt: number }>();
  private pressed?: HTMLElement;

  constructor(private root: HTMLElement) {
    for (const card of root.querySelectorAll<HTMLElement>('.glass')) {
      const glint = document.createElement('span'); glint.className = 'liquid-glint'; glint.setAttribute('aria-hidden', 'true'); card.prepend(glint);
    }
    root.addEventListener('pointermove', event => {
      if (!this.enabled || event.pointerType === 'touch') return;
      this.follow(event);
    });
    root.addEventListener('pointerleave', () => this.activate(undefined));
    root.addEventListener('pointerdown', event => {
      if (!this.enabled || event.button !== 0) return;
      const card = this.card(event.target);
      if (event.pointerType !== 'touch') this.follow(event);
      this.pressed = card; card?.classList.add('liquid-pressed');
    });
    for (const name of ['pointerup', 'pointercancel']) window.addEventListener(name, () => this.release());
    window.addEventListener('blur', () => { this.release(); this.activate(undefined); });
    root.addEventListener('focusin', event => { if (this.enabled) this.activate(this.card(event.target)); });
    root.addEventListener('focusout', event => { if (!root.contains(event.relatedTarget as Node | null)) this.activate(undefined); });
    window.addEventListener('resize', () => this.invalidate());
  }
  configure(enabled: boolean, fps: number) {
    this.enabled = enabled; this.fps = Math.max(1, Math.min(30, fps));
    this.root.dataset.dynamic = String(enabled);
    if (!enabled) { this.activate(undefined); this.release(); }
    else if (this.active) this.invalidate();
  }
  invalidate() { if (this.active?.isConnected) this.bounds = this.active.getBoundingClientRect(); else this.activate(undefined); }
  private card(target: EventTarget | null) {
    if (!(target instanceof Element) || target.closest('.card-details')) return undefined;
    const card = target.closest<HTMLElement>('.quota-card,.quota-account,.glass');
    return card && this.root.contains(card) && !card.closest('.widget-hidden') ? card : undefined;
  }
  private release() { this.pressed?.classList.remove('liquid-pressed'); this.pressed = undefined; }
  private follow(event: PointerEvent) {
    const card = this.card(event.target);
    this.activate(card, event);
    if (!card || !this.bounds) return;
    const point = this.point(event, this.bounds);
    if (Math.abs(point.x - this.targetX) + Math.abs(point.y - this.targetY) < .05) return;
    this.targetX = point.x; this.targetY = point.y;
    this.schedule(true);
  }
  private point(event: PointerEvent, bounds: DOMRect) {
    // The cursor steers a broad reflection, with less travel than the cursor itself.
    return {
      x: Math.max(-.5, Math.min(.5, (event.clientX - bounds.left) / Math.max(1, bounds.width) - .5)) * bounds.width * .44,
      y: Math.max(-.5, Math.min(.5, (event.clientY - bounds.top) / Math.max(1, bounds.height) - .5)) * bounds.height * .36,
    };
  }
  private activate(card?: HTMLElement, event?: PointerEvent) {
    if (this.active === card) return;
    cancelAnimationFrame(this.frame); this.frame = 0;
    if (this.active) {
      this.active.classList.remove('liquid-hover');
      this.resting.set(this.active, { x: this.x, y: this.y, leftAt: performance.now() });
    }
    this.active = card; this.bounds = card?.getBoundingClientRect();
    this.velocityX = this.velocityY = this.lastFrame = 0;
    if (!card || !this.bounds) return;
    const point = event ? this.point(event, this.bounds) : { x: 0, y: 0 };
    const previous = this.resting.get(card);
    // Keep a still-visible reflection continuous during quick exit/re-entry.
    const fading = previous && performance.now() - previous.leftAt < 420;
    this.x = fading ? previous.x : point.x; this.y = fading ? previous.y : point.y;
    this.targetX = point.x; this.targetY = point.y;
    card.classList.toggle('liquid-entering', !fading);
    this.render();
    // Commit the entry position while invisible, before enabling transform smoothing.
    if (!fading) {
      const glint = card.querySelector<HTMLElement>(':scope > .liquid-glint');
      if (glint) void getComputedStyle(glint, '::before').transform;
    }
    card.classList.add('liquid-hover'); this.schedule(true);
  }
  private render() {
    this.active?.style.setProperty('--light-x', `${this.x.toFixed(2)}px`);
    this.active?.style.setProperty('--light-y', `${this.y.toFixed(2)}px`);
  }
  private schedule(wake = false) {
    if (!this.frame && this.enabled && this.active) {
      if (wake) this.lastFrame = 0;
      this.frame = requestAnimationFrame(time => this.draw(time));
    }
  }
  private draw(time: number) {
    this.frame = 0;
    if (!this.enabled || !this.active?.isConnected) { this.activate(undefined); return; }
    const dt = this.lastFrame ? time - this.lastFrame : 1000 / this.fps;
    if (dt < 1000 / this.fps - .5) { this.schedule(); return; }
    this.lastFrame = time;
    this.active.classList.remove('liquid-entering');
    // Analytic critically damped motion: soft acceleration/braking, no bouncing.
    const seconds = Math.min(dt, 100) / 1000, damping = 15, decay = Math.exp(-damping * seconds);
    const offsetX = this.x - this.targetX, offsetY = this.y - this.targetY;
    const motionX = this.velocityX + damping * offsetX, motionY = this.velocityY + damping * offsetY;
    this.x = this.targetX + (offsetX + motionX * seconds) * decay;
    this.y = this.targetY + (offsetY + motionY * seconds) * decay;
    this.velocityX = (this.velocityX - damping * motionX * seconds) * decay;
    this.velocityY = (this.velocityY - damping * motionY * seconds) * decay;
    const moving = Math.abs(this.targetX - this.x) + Math.abs(this.targetY - this.y) > .2 || Math.abs(this.velocityX) + Math.abs(this.velocityY) > 2;
    if (!moving) { this.x = this.targetX; this.y = this.targetY; this.velocityX = this.velocityY = 0; }
    this.render();
    if (moving) this.schedule();
  }
}

