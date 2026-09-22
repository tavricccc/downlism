/**
 * Correlates response headers with real entries in chrome.downloads.
 *
 * A Content-Disposition header does not mean the browser is downloading a file: fetch/XHR
 * responses use the same header, and cloud storage clients can issue hundreds of them while
 * assembling one encrypted file. A response is therefore only evidence. It becomes a takeover
 * after chrome.downloads reports the matching URL.
 */

export interface PendingResponse {
  url: string;
  fileName?: string;
  referrer?: string;
  totalBytes: number;
}

interface TimedResponse {
  response: PendingResponse;
  seenAt: number;
}

const PAGE_MANAGED_HOSTS = ["mega.nz", "mega.co.nz", "mega.io"];

/**
 * MEGA decrypts and assembles downloads in the page. Its network URLs identify encrypted
 * pieces, not the file the person selected, so an external downloader must not take them.
 */
export function isPageManagedDownload(...urls: Array<string | undefined>): boolean {
  return urls.some((value) => {
    if (!value) return false;

    try {
      const parsed = new URL(value);
      const host = (parsed.protocol === "blob:" ? new URL(parsed.pathname) : parsed).hostname.toLowerCase();
      return PAGE_MANAGED_HOSTS.some((domain) => host === domain || host.endsWith(`.${domain}`));
    } catch {
      return false;
    }
  });
}

/** Maps browser-viewable response types to the existing skip-extension setting. */
export function extensionForMime(value: string | undefined): string | undefined {
  const mime = value?.split(";", 1)[0].trim().toLowerCase();
  if (!mime) return undefined;
  if (mime === "application/json" || mime.endsWith("+json")) return "json";
  if (mime === "application/xhtml+xml") return "html";
  if (mime === "image/svg+xml") return "svg";
  if (mime === "application/xml" || mime.endsWith("+xml")) return "xml";
  if (mime === "application/pdf") return "pdf";
  if (mime === "text/html") return "html";
  if (mime === "text/plain") return "txt";
  return undefined;
}

export class TakeoverState {
  private readonly pending = new Map<string, TimedResponse>();
  // Keep cancellation tombstones: interrupted is emitted before the filename listener can
  // finish, and releasing then would let that listener hand the same file over twice.
  private readonly claimed = new Map<number, number>();

  public constructor(
    private readonly lifetimeMs = 30_000,
    private readonly maximumPending = 256,
  ) {}

  /** Remembers metadata, but deliberately performs no handover. */
  public remember(response: PendingResponse, now = Date.now()): void {
    this.prune(now);

    // Refreshing an existing URL should also move it to the newest position in insertion order.
    this.pending.delete(response.url);
    this.pending.set(response.url, { response, seenAt: now });

    while (this.pending.size > this.maximumPending) {
      const oldest = this.pending.keys().next().value as string | undefined;
      if (oldest === undefined) break;
      this.pending.delete(oldest);
    }
  }

  /** Claims a browser download once and consumes matching response metadata, if any. */
  public claimResponse(downloadId: number, urls: readonly string[], now = Date.now()): PendingResponse | undefined {
    this.prune(now);
    if (this.claimed.has(downloadId)) return undefined;
    for (const url of urls) {
      const candidate = this.pending.get(url);
      if (!candidate) continue;

      this.pending.delete(url);
      this.claimed.set(downloadId, now);
      return candidate.response;
    }

    return undefined;
  }

  /** Claims the no-header fallback route, preventing another listener from handling it too. */
  public claimFallback(downloadId: number, now = Date.now()): boolean {
    this.prune(now);
    if (this.claimed.has(downloadId)) return false;
    this.claimed.set(downloadId, now);
    return true;
  }

  public release(downloadId: number): void {
    this.claimed.delete(downloadId);
  }

  public get pendingCount(): number {
    return this.pending.size;
  }

  private prune(now: number): void {
    for (const [id, seenAt] of this.claimed) {
      if (now - seenAt >= 300_000) this.claimed.delete(id);
    }
    while (this.claimed.size > this.maximumPending) {
      const oldest = this.claimed.keys().next().value;
      if (oldest === undefined) break;
      this.claimed.delete(oldest);
    }
    for (const [url, candidate] of this.pending) {
      if (now - candidate.seenAt < this.lifetimeMs) continue;
      this.pending.delete(url);
    }
  }
}
