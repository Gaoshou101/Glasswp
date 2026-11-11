interface RefreshReport {
  canRefresh: boolean;
  snapshotFetchedAt: string | null;
  refresh: { state: { status: string; message: string; lastFinishedAt: string | null } };
}

export class QuotaRefreshControl {
  private checkedAt = 0;
  private checking = false;
  private submitting = false;
  private watching = false;
  private busy = false;
  private available = false;
  private finishedWaitingAt = 0;
  private feedbackUntil = 0;
  private key = '';
  private generation = 0;
  private requests = new Set<AbortController>();
  constructor(private button: HTMLButtonElement, private connection: () => { bridgeUrl: string; readToken: string }, private updated: () => void) {
    button.onclick = () => { void this.refresh(); };
  }
  private connect() {
    const c = this.connection(), key = c.bridgeUrl + ':' + c.readToken;
    if (key !== this.key) {
      this.stop(); this.key = key; this.watching = this.busy = this.available = false; this.feedbackUntil = this.finishedWaitingAt = 0;
      this.draw('刷新', '向上游获取最新用量', !c.readToken);
    }
    return c;
  }
  private draw(label: string, title: string, disabled: boolean, state = '') {
    this.button.querySelector('span')!.textContent = label;
    this.button.title = title; this.button.disabled = disabled; this.button.dataset.state = state;
    this.button.setAttribute('aria-busy', String(state === 'busy'));
  }
  stop() {
    this.generation++; for (const controller of this.requests) controller.abort(); this.requests.clear();
    this.checkedAt = 0; this.checking = this.submitting = false;
  }
  tick() {
    if (this.feedbackUntil && Date.now() >= this.feedbackUntil) {
      this.feedbackUntil = 0;
      this.draw('刷新', '向上游获取最新用量', !this.available);
    }
  }
  private async request(method: 'GET' | 'POST', signal: AbortSignal): Promise<RefreshReport> {
    const c = this.connection();
    const response = await fetch(c.bridgeUrl + '/v1/quota-refresh' + (method === 'GET' ? '/status' : ''), {
      method, headers: { 'X-Wallpaper-Token': c.readToken }, cache: 'no-store', signal
    });
    if (!response.ok) throw new Error(response.status === 404 ? '请更新本机桥接后使用刷新按钮' : response.status === 401 ? '展示票据无效，请在本机设置重新连接' : '无法刷新用量，请检查本机桥接与所选来源');
    return response.json();
  }
  private apply(report: RefreshReport) {
    this.available = report.canRefresh;
    const state = report.refresh.state;
    if (['queued', 'running'].includes(state.status)) {
      this.watching = this.busy = true; this.finishedWaitingAt = this.feedbackUntil = 0;
      this.draw('刷新中', state.message, true, 'busy'); return;
    }
    if (this.watching && state.lastFinishedAt) {
      // The upstream task finishes before its newly written snapshot is read back.
      const needsSnapshot = ['ok', 'partial'].includes(state.status);
      if (needsSnapshot && (!report.snapshotFetchedAt || Date.parse(report.snapshotFetchedAt) < Date.parse(state.lastFinishedAt))) {
        this.finishedWaitingAt ||= Date.now();
        if (Date.now() - this.finishedWaitingAt < 30000) { this.busy = true; this.draw('刷新中', '正在读取最新额度', true, 'busy'); return; }
        this.watching = this.busy = false;
        this.draw('重试', '已获取用量，但未读到最新额度，请重试', false, 'failed'); return;
      }
      this.watching = this.busy = false; this.finishedWaitingAt = 0;
      this.updated();
      const ok = state.status === 'ok';
      this.draw(ok ? '已刷新' : state.status === 'partial' ? '部分失败' : '重试', state.message, !this.available, ok ? 'ok' : 'failed');
      this.feedbackUntil = ok ? Date.now() + 5000 : 0; return;
    }
    this.busy = false;
    if (!this.available) this.draw('刷新', '请先在本机设置选择并配置 AI 套餐来源', true);
    else if (!this.feedbackUntil && this.button.dataset.state !== 'failed') this.draw('刷新', '向上游获取最新用量', false);
  }
  async sync(force = false) {
    const c = this.connect();
    if (!c.readToken || this.checking || this.submitting || (!force && Date.now() - this.checkedAt < (this.busy ? 2000 : 5000))) return;
    this.checking = true; this.checkedAt = Date.now();
    const generation = this.generation, controller = new AbortController(); this.requests.add(controller);
    const timeout = setTimeout(() => controller.abort(), 8000);
    try { const report = await this.request('GET', controller.signal); if (generation === this.generation) this.apply(report); }
    catch (error) { if (generation === this.generation) { this.watching = this.busy = false; this.draw('重试', error instanceof Error ? error.message : '暂时无法读取刷新状态', false, 'failed'); } }
    finally { clearTimeout(timeout); this.requests.delete(controller); if (generation === this.generation) this.checking = false; }
  }
  private async refresh() {
    const c = this.connect();
    if (!c.readToken || this.submitting || this.busy) return;
    this.generation++; this.submitting = true; this.watching = true; this.feedbackUntil = 0;
    this.draw('刷新中', '正在请求上游用量', true, 'busy');
    const generation = this.generation, controller = new AbortController(); this.requests.add(controller);
    const timeout = setTimeout(() => controller.abort(), 8000);
    try { const report = await this.request('POST', controller.signal); if (generation === this.generation) this.apply(report); }
    catch (error) { if (generation === this.generation) { this.watching = this.busy = false; this.draw('重试', error instanceof Error ? error.message : '刷新失败，请重试', false, 'failed'); } }
    finally {
      clearTimeout(timeout); this.requests.delete(controller);
      if (generation === this.generation) { this.submitting = this.checking = false; this.checkedAt = 0; }
    }
  }
}
