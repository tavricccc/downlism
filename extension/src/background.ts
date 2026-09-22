/**
 * Hands downloads to Downlism.
 *
 * Three routes in, because MV3 has no way to block a response.
 *
 *  1. The content script handles explicit magnet links only. HTTP clicks stay with the page;
 *     neither a file extension nor a download attribute proves the URL is a direct file.
 *  2. webRequest watches response headers and decides in advance; when chrome.downloads then
 *     reports the download, the cancel is already decided and lands with nothing awaited in
 *     between. This is how Neat Download Manager does it, and it beats deciding inside the
 *     downloads listener, where the headers are long gone and every asynchronous step is more
 *     of the file written to disk.
 *  3. onDeterminingFilename, for downloads no response header announced.
 *
 * Plus a context menu, for links the click route deliberately leaves alone.
 *
 * Separately from all of that, it sniffs: response headers also reveal the video a page is
 * playing, which is never a download the browser would report at all. Those finds are kept per
 * tab and offered from the popup rather than taken automatically — a page playing a video has
 * not asked for it to be saved.
 *
 * Runs as a service worker, which the browser recycles after roughly thirty seconds of
 * idleness. Nothing here may assume it stays alive between downloads.
 */

import { classify, isPageOnlyHost, merge, type FoundMedia } from "./media.js";
import {
  extensionForMime,
  isPageManagedDownload,
  TakeoverState,
  type PendingResponse,
} from "./takeover.js";

import { DEFAULTS, normalizeSettings, permitsAutomatic, type Settings } from "./settings.js";

const HOST_NAME = "com.downlism.host";

/**
 * Fail open while persisted settings load: a cold worker must not cancel a browser download
 * using defaults when the person has disabled takeover.
 */
let cached: Settings = { ...DEFAULTS, enabled: false };

async function refreshSettings(): Promise<Settings> {
  cached = normalizeSettings(await chrome.storage.local.get(DEFAULTS));
  return cached;
}

const settingsReady = refreshSettings().catch(() => cached);
chrome.storage.onChanged.addListener(() => {
  cached = { ...cached, enabled: false };
  void refreshSettings().catch(() => { /* Keep automatic takeover disabled on storage failure. */ });
});

/**
 * Header responses are only candidates. Fetch/XHR traffic can carry Content-Disposition too;
 * handing it over before chrome.downloads confirms a real download is what caused API
 * "response" files and MEGA's encrypted chunk requests to open a storm of prompts.
 */
const takeovers = new TakeoverState();

function headerValue(headers: chrome.webRequest.HttpHeader[] | undefined, name: string): string {
  const found = headers?.find((header) => header.name.toLowerCase() === name);
  return found?.value ?? "";
}

function extensionOf(filename: string): string {
  const clean = filename.split(/[?#]/)[0];
  const dot = clean.lastIndexOf(".");
  return dot < 0 ? "" : clean.slice(dot + 1).toLowerCase();
}

function shouldSkip(fileName: string | undefined, mime: string | undefined): boolean {
  const fileExtension = extensionOf(fileName ?? "");
  const mimeExtension = extensionForMime(mime);
  return cached.skipExtensions.includes(fileExtension)
    || (mimeExtension !== undefined && cached.skipExtensions.includes(mimeExtension));
}

/** Pulls the filename out of a Content-Disposition value, RFC 5987 form first. */
function nameFromDisposition(value: string): string | undefined {
  const extended = /filename\*\s*=\s*[^']*'[^']*'([^;]+)/i.exec(value);
  if (extended) {
    try {
      return decodeURIComponent(extended[1].trim());
    } catch {
      // Fall through to the plain form.
    }
  }

  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(value);
  return plain ? plain[1].trim() : undefined;
}

/**
 * Serialises the cookies the browser would have sent. Without them, anything behind a login
 * answers the handover with a sign-in page, which would then be saved as the file.
 */
async function cookieHeaderFor(url: string): Promise<string> {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ");
  } catch {
    return "";
  }
}

interface Handover {
  url: string;
  fileName?: string;
  referrer?: string;
  totalBytes?: number;
  /** Which engine Downlism should use. Omitted means it decides from the URL. */
  kind?: "file" | "media" | "torrent";
  /** The page a sniffed stream was playing on; yt-dlp resolves it far better than the stream. */
  pageUrl?: string;
}

async function handOver(item: Handover): Promise<boolean> {
  const message = {
    url: item.url,
    fileName: item.fileName,
    referrer: item.referrer,
    cookies: await cookieHeaderFor(item.url),
    userAgent: navigator.userAgent,
    totalBytes: item.totalBytes ?? 0,
    kind: item.kind,
    pageUrl: item.pageUrl,
  };

  try {
    // One-shot rather than a persistent port: the worker may be torn down at any moment, and
    // a dead port produces a silent failure the user would only notice as a missing file.
    const reply = await chrome.runtime.sendNativeMessage(HOST_NAME, message);
    return Boolean(reply?.accepted);
  } catch {
    return false;
  }
}

async function notifyFailure(): Promise<void> {
  await chrome.action.setBadgeText({ text: "!" });
  await chrome.action.setBadgeBackgroundColor({ color: "#B4443A" });
  setTimeout(() => void chrome.action.setBadgeText({ text: "" }), 5000);
}

/**
 * The cancel call is made by the event listener before this function starts. We still wait for
 * its result before handing anything over: if Chrome refused the cancellation, starting the
 * same file in Downlism would create a duplicate.
 */
async function completeTakeover(
  item: chrome.downloads.DownloadItem,
  candidate: PendingResponse,
  cancellation: Promise<void>,
): Promise<void> {
  try {
    await cancellation;
  } catch {
    takeovers.release(item.id);
    return;
  }

  if (await handOver(candidate)) {
    await chrome.downloads.erase({ id: item.id });
    return;
  }

  // Hand the download back rather than leaving the person with a cancelled entry and no file.
  // The restarted one carries byExtensionId, so all takeover routes let it through.
  await notifyFailure();
  await chrome.downloads.erase({ id: item.id });
  try {
    await chrome.downloads.download({ url: candidate.url });
  } catch {
    // Nothing further to try; the badge already said so.
  }
}

/**
 * Route 2: watch response headers and decide before the download exists.
 *
 * Not blocking — Chrome's MV3 removed that, so this only observes. What it buys is the
 * decision itself: Content-Disposition, Content-Length and Content-Type are all here, and all
 * gone by the time chrome.downloads reports anything.
 */
chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (!permitsAutomatic(cached, details.url, details.initiator) || details.method !== "GET" || details.statusCode < 200 || details.statusCode >= 300) return;
    if (isPageManagedDownload(details.url, details.initiator)) return;
    // API attachments are not navigations. Never let an XHR response become takeover
    // evidence for a later download with the same URL.
    if (details.type === "xmlhttprequest") return;

    const disposition = headerValue(details.responseHeaders, "content-disposition");
    // Remember GET metadata without requiring attachment: chrome.downloads, not a header,
    // is the authority on whether the browser is saving a file.
    const fileName = nameFromDisposition(disposition);
    const contentType = headerValue(details.responseHeaders, "content-type");
    if (shouldSkip(fileName, contentType)) return;

    const length = Number(headerValue(details.responseHeaders, "content-length")) || 0;
    if (length > 0 && length < cached.minimumBytes) return;

    // Do not hand this response over yet. Content-Disposition is also used by API responses
    // and cloud download chunks; only chrome.downloads can confirm that Chrome will save it.
    takeovers.remember({
      url: details.url,
      fileName,
      referrer: details.initiator,
      totalBytes: length,
    });
  },
  { urls: ["http://*/*", "https://*/*"], types: ["main_frame", "sub_frame", "xmlhttprequest", "other"] },
  ["responseHeaders"],
);

/**
 * Cancels the download route 2 already decided on. Nothing is awaited before the cancel,
 * because every await is more of the file arriving.
 */
chrome.downloads.onCreated.addListener((item) => {
  // Downloads this extension started must never be taken over, or restoring one would loop.
  if (item.byExtensionId || !permitsAutomatic(cached, item.url, item.finalUrl, item.referrer)) return;
  // A restored history entry is not a new download.
  if (item.endTime || item.state !== "in_progress") return;
  if (!/^https?:/i.test(item.finalUrl || item.url)) return;
  if (shouldSkip(item.filename, item.mime)) return;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  if (size < cached.minimumBytes || size < 0) return;
  if (isPageManagedDownload(item.finalUrl, item.url, item.referrer)) return;

  const candidate = takeovers.claimResponse(item.id, [item.finalUrl, item.url]);
  if (!candidate) return;

  // Issue cancellation synchronously. The native handover waits until Chrome confirms it.
  const cancellation = chrome.downloads.cancel(item.id);
  void completeTakeover(item, candidate, cancellation);
});

// Claimed IDs live until Chrome removes or finishes the item, so onDeterminingFilename cannot
// race the header route and hand the same file over a second time.
chrome.downloads.onErased.addListener((downloadId) => takeovers.release(downloadId));
chrome.downloads.onChanged.addListener((delta) => {
  if (delta.state?.current === "complete") {
    takeovers.release(delta.id);
  }
});

/**
 * Route 3, the last resort: a redirect chain, a blob, a server that sends no
 * Content-Disposition. Later than route 2 and it wastes a little of the file, which is why it
 * runs last.
 */
chrome.downloads.onDeterminingFilename.addListener((item) => {
  if (item.byExtensionId || item.endTime || item.state !== "in_progress") return;
  if (!permitsAutomatic(cached, item.url, item.finalUrl, item.referrer)) return;
  if (!/^https?:/i.test(item.finalUrl || item.url)) return;
  if (isPageManagedDownload(item.finalUrl, item.url, item.referrer)) return;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  // Unknown-length downloads remain in the browser; there is not enough evidence to apply
  // the configured threshold. The context menu remains available for an explicit handover.
  if (size < cached.minimumBytes || size < 0) return;
  if (shouldSkip(item.filename, item.mime)) return;
  // No observed GET means no automatic takeover: POST responses cannot be replayed by an
  // external downloader. Manual context-menu downloads remain available.
  const candidate = takeovers.claimResponse(item.id, [item.finalUrl, item.url]);
  if (!candidate) return;

  const url = candidate.url;
  const fileName = item.filename ? item.filename.split(/[\\/]/).pop() : candidate.fileName;
  const cancellation = chrome.downloads.cancel(item.id);

  void completeTakeover(
    item,
    { url, fileName, referrer: item.referrer, totalBytes: item.fileSize },
    cancellation,
  );
});

/**
 * Videos seen on each tab.
 *
 * Held in session storage rather than a module variable because the service worker is torn
 * down after about thirty seconds of idleness, and a page can sit playing for far longer than
 * that. Session storage is cleared when the browser closes, which is the right lifetime: a
 * find is only meaningful while the page that produced it is still open.
 */
function mediaKey(tabId: number): string {
  return `media:${tabId}`;
}

/**
 * Every read-modify-write of the finds runs in turn.
 *
 * A video page issues several media responses within the same tick, and session storage has no
 * atomic update: run them concurrently and each one reads the list before the others wrote,
 * so all but the last find is lost. A navigation's clear joins the same queue for the same
 * reason — otherwise a write already in flight resurrects the previous page's list.
 */
let mediaWrites: Promise<unknown> = Promise.resolve();

function queueMediaWrite(work: () => Promise<void>): void {
  mediaWrites = mediaWrites.then(work, work);
}

async function readMedia(tabId: number): Promise<FoundMedia[]> {
  const stored = await chrome.storage.session.get(mediaKey(tabId));
  return (stored[mediaKey(tabId)] as FoundMedia[] | undefined) ?? [];
}

async function rememberMedia(tabId: number, found: FoundMedia): Promise<void> {
  const existing = await readMedia(tabId);
  const merged = merge(existing, found);
  if (merged === existing) return;

  await chrome.storage.session.set({ [mediaKey(tabId)]: merged });

  // Per-tab, so the count belongs to the page the person is actually looking at.
  await chrome.action.setBadgeText({ tabId, text: String(merged.length) });
  await chrome.action.setBadgeBackgroundColor({ tabId, color: "#165674" });
}

async function forgetMedia(tabId: number): Promise<void> {
  await chrome.storage.session.remove(mediaKey(tabId));
  try {
    await chrome.action.setBadgeText({ tabId, text: "" });
  } catch {
    // The tab is already gone; there is no badge left to clear.
  }
}

/**
 * Route 4: the sniffer. Watches media responses rather than downloads, because a video that
 * plays in a page is never reported to chrome.downloads at all.
 */
chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (!cached.sniffMedia || !permitsAutomatic(cached, details.url, details.initiator) || details.tabId < 0) return;
    if (isPageManagedDownload(details.url, details.initiator)) return;

    // A signed, expiring, range-split URL from one of these sites cannot be downloaded on its
    // own. The popup offers the page instead, which is the only thing that works there.
    if (isPageOnlyHost(details.url)) return;

    const found = classify(
      details.url,
      headerValue(details.responseHeaders, "content-type"),
      Number(headerValue(details.responseHeaders, "content-length")) || 0,
    );

    if (found) queueMediaWrite(() => rememberMedia(details.tabId, found));
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "object", "other"] },
  ["responseHeaders"],
);

// A navigation replaces what the tab is playing, so the previous page's finds are stale.
chrome.tabs.onUpdated.addListener((tabId, changes) => {
  if (changes.url) queueMediaWrite(() => forgetMedia(tabId));
});

chrome.tabs.onRemoved.addListener((tabId) => queueMediaWrite(() => forgetMedia(tabId)));

/** A way to take any link, including the ones the click route deliberately leaves alone. */
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "downlism-link",
      title: "用 Downlism 下載",
      contexts: ["link", "image", "video", "audio"],
    });

    // Offered on the page itself, because the video element on a modern site has no usable
    // src attribute for the link entry above to pick up.
    chrome.contextMenus.create({
      id: "downlism-page-video",
      title: "用 Downlism 下載這個頁面的影片",
      contexts: ["page", "video", "frame"],
    });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === "downlism-page-video") {
    const page = info.pageUrl || tab?.url;
    if (!page || !/^https?:/i.test(page)) return;

    void (async () => {
      if (!(await handOver({ url: page, kind: "media", pageUrl: page }))) await notifyFailure();
    })();
    return;
  }

  const url = info.linkUrl || info.srcUrl;
  // magnet is allowed here and nowhere else in this file: it is the one scheme that names a
  // download without naming a server, so it can only ever arrive through a deliberate click.
  if (!url || !/^(https?|magnet):/i.test(url)) return;

  void (async () => {
    const kind = url.toLowerCase().startsWith("magnet:") ? "torrent" : undefined;
    if (!(await handOver({ url, kind, referrer: info.pageUrl || tab?.url }))) await notifyFailure();
  })();
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  // Route 1: the content script has already stopped the click, so this only decides whether
  // the download happens in Downlism or is replayed to the browser.
  if (message?.type === "link") {
    void (async () => {
      await settingsReady;
      if (!cached.enabled || typeof message.url !== "string" || !/^magnet:\?/i.test(message.url)) {
        sendResponse({ handled: false });
        return;
      }

      const handled = await handOver({
        url: message.url,
        fileName: message.fileName,
        referrer: message.referrer,
      });

      if (!handled) await notifyFailure();
      sendResponse({ handled });
    })();
    return true;
  }

  if (message?.type === "ping") {
    chrome.runtime
      .sendNativeMessage(HOST_NAME, { url: "", ping: true })
      .then((reply) => sendResponse({ running: Boolean(reply?.accepted) }))
      .catch(() => sendResponse({ running: false }));
    return true;
  }

  // The popup asks what was found on the tab it was opened over.
  if (message?.type === "found") {
    void (async () => {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      const media = tab?.id === undefined ? [] : await readMedia(tab.id);
      sendResponse({ media, pageUrl: tab?.url ?? "", pageOnly: isPageOnlyHost(tab?.url ?? "") });
    })();
    return true;
  }

  if (message?.type === "download-media") {
    void (async () => {
      const handled = await handOver({
        url: message.url,
        // A stream has to be assembled; a whole file can be fetched as it is, and going
        // through yt-dlp for it would only add a process.
        kind: message.form === "file" ? "file" : "media",
        pageUrl: message.form === "file" ? undefined : message.pageUrl,
        referrer: message.pageUrl,
      });

      if (!handled) await notifyFailure();
      sendResponse({ handled });
    })();
    return true;
  }

  if (message?.type === "settings") {
    refreshSettings().then(sendResponse);
    return true;
  }

  if (message?.type === "save") {
    void (async () => {
      try {
        const settings = normalizeSettings({ ...cached, ...message.settings }, true);
        await chrome.storage.local.set(settings);
        cached = settings;
        sendResponse({ saved: true });
      } catch (error) {
        sendResponse({ saved: false, error: error instanceof Error ? error.message : "設定未儲存" });
      }
    })();
    return true;
  }

  return false;
});

// Declared a module so each entry point keeps its own scope; without this TypeScript treats
// these files as one global script and the shared helper names collide.
export {};
