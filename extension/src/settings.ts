export interface Settings {
  enabled: boolean;
  minimumBytes: number;
  skipExtensions: string[];
  blockedHosts: string[];
  allowedHosts: string[];
  sniffMedia: boolean;
}

export const DEFAULTS: Settings = {
  enabled: true, minimumBytes: 1024 * 1024,
  skipExtensions: ["pdf", "html", "htm", "txt", "svg", "json", "xml"],
  blockedHosts: [], allowedHosts: [], sniffMedia: true,
};

export function parseExtensions(text: string): string[] {
  const values = [...new Set(text.toLowerCase().split(/[\s,;]+/).filter(Boolean).map(value => value.replace(/^\./, "")))];
  if (values.length > 100 || values.some(value => !/^[a-z0-9]{1,20}$/.test(value))) throw new Error("副檔名只能包含英文字母或數字，最多 100 個。");
  return values;
}

export function parseHosts(text: string): string[] {
  const values = [...new Set(text.toLowerCase().split(/[\s,;]+/).filter(Boolean).map(value => value.replace(/^\*\./, "").replace(/\.$/, "")))];
  if (values.length > 100) throw new Error("每份網站清單最多 100 個網域。");
  return values.map(value => {
    if (!value || /[/@:#?\\]/.test(value)) throw new Error("網站只填網域，例如 example.com，不含 https:// 或路徑。");
    const parsed = new URL(`https://${value}`);
    if (!parsed.hostname || parsed.pathname !== "/") throw new Error("網站網域格式不正確。");
    return parsed.hostname;
  });
}

/** Validate at the worker boundary too; storage and popup input are not type-safe. */
export function normalizeSettings(raw: Partial<Settings>, strict = false): Settings {
  try {
    const merged = { ...DEFAULTS, ...raw };
    if (typeof merged.enabled !== "boolean" || typeof merged.sniffMedia !== "boolean" ||
        !Number.isFinite(merged.minimumBytes) || merged.minimumBytes < 0 || merged.minimumBytes > 10 * 1024 ** 3 ||
        !Array.isArray(merged.skipExtensions) || !Array.isArray(merged.blockedHosts) || !Array.isArray(merged.allowedHosts) ||
        [...merged.skipExtensions, ...merged.blockedHosts, ...merged.allowedHosts].some(value => typeof value !== "string"))
      throw new Error("設定格式不正確。最小檔案須介於 0 與 10240 MiB。");
    return {
      enabled: merged.enabled, sniffMedia: merged.sniffMedia, minimumBytes: Math.round(merged.minimumBytes),
      skipExtensions: parseExtensions(merged.skipExtensions.join(",")),
      blockedHosts: parseHosts(merged.blockedHosts.join(",")), allowedHosts: parseHosts(merged.allowedHosts.join(",")),
    };
  } catch (error) {
    if (strict) throw error;
    // Corrupt preferences must not silently re-enable automatic takeover.
    return { ...DEFAULTS, enabled: false };
  }
}

function matches(host: string, domain: string): boolean { return host === domain || host.endsWith(`.${domain}`); }
export function permitsAutomatic(settings: Settings, ...urls: Array<string | undefined>): boolean {
  if (!settings.enabled) return false;
  const hosts = urls.flatMap(value => {
    try { return value ? [new URL(value).hostname.toLowerCase()] : []; } catch { return []; }
  });
  if (hosts.some(host => settings.blockedHosts.some(domain => matches(host, domain)))) return false;
  return settings.allowedHosts.length === 0 || hosts.some(host => settings.allowedHosts.some(domain => matches(host, domain)));
}
