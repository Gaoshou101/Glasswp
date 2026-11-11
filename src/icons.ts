/** Decorative SVG markup. Weather masks use unique IDs so copies can share a document. */
export type WeatherKind = 'clear' | 'partly' | 'cloudy' | 'fog' | 'drizzle' | 'rain' | 'snow' | 'thunder' | 'unknown';

/** WMO weather interpretation codes (Open-Meteo) grouped into drawable kinds. */
export function weatherKind(code: number | null): WeatherKind {
  if (code === null || !Number.isFinite(code)) return 'unknown';
  if (code === 0) return 'clear';
  if (code === 1 || code === 2) return 'partly';
  if (code === 3) return 'cloudy';
  if (code === 45 || code === 48) return 'fog';
  if (code >= 51 && code <= 57) return 'drizzle';
  if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return 'rain';
  if ((code >= 71 && code <= 77) || code === 85 || code === 86) return 'snow';
  if (code >= 95 && code <= 99) return 'thunder';
  return 'unknown';
}

const cloudPath = 'M19 38a13 13 0 1 1 12.18-17.55a9 9 0 1 1 2.82 17.55Z';
// 48 × 48 grid. The cloud is one closed path: two circles (r13, r9) on a flat base at y=38.
const cloud = (cls = 'wi-cloud', transform = '') => `<path class="${cls}"${transform ? ` transform="${transform}"` : ''} d="${cloudPath}"/>`;
const rays = (cx: number, cy: number, inner: number, outer: number) => Array.from({ length: 8 }, (_, i) => {
  const a = i * Math.PI / 4, c = Math.cos(a), s = Math.sin(a), f = (n: number) => +n.toFixed(2);
  return `M${f(cx + c * inner)} ${f(cy + s * inner)}L${f(cx + c * outer)} ${f(cy + s * outer)}`;
}).join('');
const sun = (cx: number, cy: number, r: number) => `<path class="wi-ray" d="${rays(cx, cy, r + 4.5, r + 8)}"/><circle class="wi-sun" cx="${cx}" cy="${cy}" r="${r}"/>`;
const moon = (transform = '') => `<path class="wi-moon"${transform ? ` transform="${transform}"` : ''} d="M26.6 9.23A15 15 0 1 0 36.29 32.6A13.5 13.5 0 0 1 26.6 9.23Z"/>`;
const lifted = 'translate(0 -5)';
let maskSequence = 0;
/** Hides `behind` wherever the front cloud sits, so a translucent cloud still occludes. IDs stay unique per copy. */
const occluded = (behind: string, frontTransform: string) => {
  const id = `wi-mask-${++maskSequence}`;
  return `<defs><mask id="${id}" maskUnits="userSpaceOnUse" x="0" y="0" width="48" height="48"><rect width="48" height="48" fill="#fff"/><path d="${cloudPath}" transform="${frontTransform}" fill="#000" stroke="#000" stroke-width="4.5" stroke-linejoin="round"/></mask></defs><g mask="url(#${id})">${behind}</g>`;
};
const drops = (long: boolean) => long
  ? '<path class="wi-drop" d="M17 36.5l-2.2 6M25 36.5l-2.2 6M33 36.5l-2.2 6"/>'
  : '<path class="wi-drop" d="M17 37l-1.1 3M25 39l-1.1 3M33 37l-1.1 3"/>';

/** Inner SVG markup for a 0 0 48 48 viewBox. Colours come from CSS classes (wi-*). */
export function weatherIconMarkup(code: number | null, isDay = true): string {
  switch (weatherKind(code)) {
    case 'clear': return isDay ? sun(24, 24, 8.5) : moon();
    case 'partly': return occluded(isDay ? sun(17.5, 17.5, 6.5) : moon('translate(-5 -4) scale(.72)'), 'translate(9.5 10.5) scale(.78)') + cloud('wi-cloud', 'translate(9.5 10.5) scale(.78)');
    case 'cloudy': return occluded(cloud('wi-cloud wi-cloud-back', 'translate(-3 -5) scale(.8)'), 'translate(7 6) scale(.82)') + cloud('wi-cloud', 'translate(7 6) scale(.82)');
    case 'fog': return cloud('wi-cloud', `${lifted} scale(1)`) + '<path class="wi-mist" d="M11 39h22M17 44h20"/>';
    case 'drizzle': return cloud('wi-cloud', lifted) + drops(false);
    case 'rain': return cloud('wi-cloud', lifted) + drops(true);
    case 'snow': return cloud('wi-cloud', lifted) + '<g class="wi-snow"><circle cx="16" cy="38.5" r="1.9"/><circle cx="24.5" cy="42.5" r="1.9"/><circle cx="33" cy="38.5" r="1.9"/></g>';
    case 'thunder': return cloud('wi-cloud', lifted) + '<path class="wi-bolt" d="M25.5 31.5 19 40.5h5.2L22 47l8.6-10.2h-5.4l3.1-5.3Z"/>';
    default: return cloud('wi-cloud wi-cloud-muted');
  }
}

/** Display labels and official local assets are keyed by canonical brand. */
export const providerNames: Record<string, string> = {
  codex: 'Codex', claude: 'Claude', antigravity: 'Antigravity', kimi: 'Kimi', xai: 'xAI', devin: 'Devin', meta: 'Meta',
  'agnes-code': 'Agnes Code', gcp: 'Google Cloud', river: 'River', opendesign: 'Open Design', codearts: 'CodeArts', officeace: 'OfficeAce', sap: 'SAP AI Core', accio: 'Accio', lobsterai: 'LobsterAI', magicrew: 'MagiCrew', monkeycode: 'MonkeyCode',
  copilot: 'GitHub Copilot', gemini: 'Gemini', cursor: 'Cursor', workbuddy: 'WorkBuddy', trae: 'Trae', kiro: 'Kiro', zcode: 'ZCode',
  commandcode: 'Command Code', grok: 'Grok', catpaw: 'CatPaw', raccoon: '商汤小浣熊', atomcode: 'AtomCode', warp: 'Warp', agnes: 'Agnes',
  astudio: 'AStudio', codar: 'Codar', deepaicode: 'DeepAI Code', teleagent: 'TeleAgent', autoclaw: 'AutoClaw', qoder: 'Qoder',
  cline: 'Cline', minimax: 'MiniMax Code', opencode: 'OpenCode', zed: 'Zed', factory: 'Factory', doubao: '豆包工作', sensenova: 'SenseNova',
  inkstone: '书生·端砚', xfyun: '讯飞星辰', muse: 'Muse', tabbit: 'Tabbit', aws: 'AWS Bedrock', cloudflare: 'Cloudflare',
  qiniu: '七牛云', loomy: 'Loomy', joycode: 'JoyCode', mimo: 'Xiaomi MiMo', nous: 'Nous Portal', orcaterm: 'OrcaTerm', mclaw: 'MClaw',
};

/** Official assets copied into the package for offline rendering. See PROVIDER-ICONS.md. */
export const providerLogoPaths: Record<string, string> = {
  'codex': './assets/providers/codex.svg',
  'claude': './assets/providers/claude.svg',
  'antigravity': './assets/providers/antigravity.svg',
  'kimi': './assets/providers/kimi.svg',
  'xai': './assets/providers/xai.png',
  'devin': './assets/providers/devin.svg',
  'meta': './assets/providers/meta.svg',
  'copilot': './assets/providers/github-copilot.svg',
  'gemini': './assets/providers/gemini.png',
  'cursor': './assets/providers/cursor.png',
  'workbuddy': './assets/providers/workbuddy.svg',
  'trae': './assets/providers/trae.svg',
  'kiro': './assets/providers/kiro.svg',
  'zcode': './assets/providers/zcode.png',
  'commandcode': './assets/providers/commandcode.png',
  'grok': './assets/providers/xai.png',
  'catpaw': './assets/providers/catpaw.png',
  'raccoon': './assets/providers/raccoon.svg',
  'atomcode': './assets/providers/atomcode.svg',
  'warp': './assets/providers/warp.svg',
  'agnes': './assets/providers/agnes.png',
  'agnes-code': './assets/providers/agnes-code.png',
  'astudio': './assets/providers/astudio.svg',
  'codar': './assets/providers/codar.png',
  'deepaicode': './assets/providers/deepaicode.png',
  'teleagent': './assets/providers/teleagent.jpg',
  'autoclaw': './assets/providers/autoclaw.png',
  'qoder': './assets/providers/qoder.svg',
  'cline': './assets/providers/cline.png',
  'minimax': './assets/providers/minimax.jpg',
  'opencode': './assets/providers/opencode.svg',
  'zed': './assets/providers/zed.png',
  'factory': './assets/providers/factory.svg',
  'doubao': './assets/providers/doubao.png',
  'sensenova': './assets/providers/sensenova.png',
  'inkstone': './assets/providers/inkstone.svg',
  'xfyun': './assets/providers/xfyun.svg',
  'muse': './assets/providers/meta.svg',
  'tabbit': './assets/providers/tabbit.png',
  'aws': './assets/providers/aws.png',
  'cloudflare': './assets/providers/cloudflare.svg',
  'gcp': './assets/providers/gcp.png',
  'qiniu': './assets/providers/qiniu.svg',
  'loomy': './assets/providers/loomy.png',
  'joycode': './assets/providers/joycode.png',
  'mimo': './assets/providers/mimo.png',
  'nous': './assets/providers/nous.svg',
  'orcaterm': './assets/providers/orcaterm.svg',
  'mclaw': './assets/providers/mclaw.png',
  'river': './assets/providers/river.png',
  'opendesign': './assets/providers/opendesign.png',
  'codearts': './assets/providers/codearts.svg',
  'officeace': './assets/providers/officeace.png',
  'sap': './assets/providers/sap.svg',
  'accio': './assets/providers/accio.svg',
  'lobsterai': './assets/providers/lobsterai.png',
  'magicrew': './assets/providers/magicrew.svg',
  'monkeycode': './assets/providers/monkeycode.png',
};

/** Provider IDs emitted by native readers, Magpie and legacy CPAMP snapshots. */
const providerBrandAliases: Record<string, string> = {
  'github-copilot': 'copilot', githubcopilot: 'copilot', copilot: 'copilot', 'gemini-cli': 'gemini', 'google-gemini': 'gemini',
  'qoder-cn': 'qoder', qwenwork: 'qoder', 'qwenwork-enterprise': 'qoder', 'doubao-work': 'doubao', 'agnes-code': 'agnes-code',
  'hub-loomy': 'loomy', 'hub-accio': 'accio', 'hub-lobsterai': 'lobsterai', 'hub-magicrew': 'magicrew', 'hub-magicrew-intl': 'magicrew',
  'hub-monkeycode': 'monkeycode', 'hub-monkeycode-intl': 'monkeycode', 'gcp-ai': 'gcp', 'sap-aicore': 'sap',
  'claude-code': 'claude', 'claude-code-cli': 'claude',
  'workbuddy-ai': 'workbuddy', 'trae-cn': 'trae', 'trae-global': 'trae', 'kiro-cli': 'kiro', 'kiro-ide': 'kiro',
  'commandcode-plan': 'commandcode', commandcode: 'commandcode', 'grok-build': 'grok', grok: 'grok', 'raccoon-cn': 'raccoon', 'kimi-work': 'kimi',
  'autoclaw-cn': 'autoclaw', 'agy-cli': 'antigravity', 'antigravity-cli': 'antigravity', 'deepai-code': 'deepaicode',
  'minimax-code': 'minimax', 'minimax-code-global': 'minimax', 'opencode-zen-free': 'opencode', 'sensenova-token-plan': 'sensenova',
  'intern-inkstone': 'inkstone', 'xfyun-maas': 'xfyun', 'xfyun-coding': 'xfyun', 'xfyun-token': 'xfyun', 'muse-meta': 'muse',
  'mimo-app': 'mimo', 'nous-portal': 'nous', 'hub-astudio': 'astudio', 'hub-autoclaw': 'autoclaw', 'hub-autoclaw-cn': 'autoclaw',
  'hub-qiniu': 'qiniu', 'cloudflare-workers-ai': 'cloudflare', 'bedrock-sso': 'aws',
};

for (const [alias, brand] of Object.entries(providerBrandAliases)) {
  if (providerNames[brand]) providerNames[alias] = providerNames[brand];
}

export function providerBrand(provider: string): string {
  const key = String(provider || '').trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(providerBrandAliases, key) ? providerBrandAliases[key] : key;
}

/** Official provider image; unknown providers retain a neutral dot. */
export function providerGlyph(provider: string): HTMLImageElement | SVGSVGElement {
  const brand = providerBrand(provider);
  const source = Object.prototype.hasOwnProperty.call(providerLogoPaths, brand) ? providerLogoPaths[brand] : undefined;
  if (source) {
    const image = document.createElement('img');
    image.src = source; image.alt = ''; image.dataset.brand = brand; image.setAttribute('aria-hidden', 'true'); image.draggable = false;
    return image;
  }
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true'); svg.setAttribute('focusable', 'false');
  svg.innerHTML = '<circle cx="12" cy="12" r="3.4" fill="currentColor" stroke="none"/>';
  return svg;
}
