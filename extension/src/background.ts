/**
 * Hands downloads to Downlism.
 *
 * Three routes in, because MV3 has no way to block a response.
 *
 *  1. The content script catches download links at the click, so the browser never opens the
 *     connection at all. This is the only route that intercepts rather than cancels.
 *  2. webRequest watches response headers and decides in advance; when chrome.downloads then
 *     reports the download, the cancel is already decided and lands with nothing awaited in
 *     between. This is how Neat Download Manager does it, and it beats deciding inside the
 *     downloads listener, where the headers are long gone and every asynchronous step is more
 *     of the file written to disk.
 *  3. onDeterminingFilename, for downloads no response header announced.
 *
 * Plus a context menu, for links the click route deliberately leaves alone.
 *
 * Runs as a service worker, which the browser recycles after roughly thirty seconds of
 * idleness. Nothing here may assume it stays alive between downloads.
 */

const HOST_NAME = "com.downlism.host";

interface Settings {
  enabled: boolean;
  minimumBytes: number;
  skipExtensions: string[];
}

const DEFAULTS: Settings = {
  enabled: true,
  // Small files finish before the handover is worth its round trip, and intercepting them
  // makes ordinary browsing feel like it is fighting the extension.
  minimumBytes: 1024 * 1024,
  // Things the browser opens rather than saves; taking these over breaks in-page viewing.
  skipExtensions: ["pdf", "html", "htm", "txt", "svg", "json", "xml"],
};

/**
 * The settings as of the last read, kept in memory so the download listeners can decide
 * synchronously. The cache starts from the defaults after every worker restart, so at worst
 * one download immediately after a cold start is judged by the defaults.
 */
let cached: Settings = { ...DEFAULTS };

async function refreshSettings(): Promise<Settings> {
  cached = { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) } as Settings;
  return cached;
}

void refreshSettings();
chrome.storage.onChanged.addListener(() => void refreshSettings());

/**
 * URLs the header watcher has handed to Downlism, waiting for the matching download entry to
 * appear so it can be cancelled at once. Decisions expire: one that never produced a download
 * would otherwise cancel an unrelated download much later.
 */
const decided = new Map<string, number>();
const DECISION_LIFETIME = 30_000;

function takeDecision(url: string): boolean {
  const at = decided.get(url);
  if (at === undefined) return false;

  decided.delete(url);
  return Date.now() - at < DECISION_LIFETIME;
}

function headerValue(headers: chrome.webRequest.HttpHeader[] | undefined, name: string): string {
  const found = headers?.find((header) => header.name.toLowerCase() === name);
  return found?.value ?? "";
}

function extensionOf(filename: string): string {
  const clean = filename.split(/[?#]/)[0];
  const dot = clean.lastIndexOf(".");
  return dot < 0 ? "" : clean.slice(dot + 1).toLowerCase();
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
}

async function handOver(item: Handover): Promise<boolean> {
  const message = {
    url: item.url,
    fileName: item.fileName,
    referrer: item.referrer,
    cookies: await cookieHeaderFor(item.url),
    userAgent: navigator.userAgent,
    totalBytes: item.totalBytes ?? 0,
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
 * Route 2: watch response headers and decide before the download exists.
 *
 * Not blocking — Chrome's MV3 removed that, so this only observes. What it buys is the
 * decision itself: Content-Disposition, Content-Length and Content-Type are all here, and all
 * gone by the time chrome.downloads reports anything.
 */
chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (!cached.enabled || details.method !== "GET") return;

    const disposition = headerValue(details.responseHeaders, "content-disposition");
    if (!/attachment/i.test(disposition)) return;

    const fileName = nameFromDisposition(disposition);
    if (fileName && cached.skipExtensions.includes(extensionOf(fileName))) return;

    const length = Number(headerValue(details.responseHeaders, "content-length")) || 0;
    if (length > 0 && length < cached.minimumBytes) return;

    decided.set(details.url, Date.now());
    void (async () => {
      if (await handOver({ url: details.url, fileName, referrer: details.initiator, totalBytes: length })) return;

      // Downlism refused it, so let the browser keep the download it already started.
      decided.delete(details.url);
      await notifyFailure();
    })();
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
  if (item.byExtensionId) return;
  // A restored history entry is not a new download.
  if (item.endTime) return;

  if (!takeDecision(item.finalUrl || item.url)) return;

  void chrome.downloads.cancel(item.id);
  void chrome.downloads.erase({ id: item.id });
});

/**
 * Route 3, the last resort: a redirect chain, a blob, a server that sends no
 * Content-Disposition. Later than route 2 and it wastes a little of the file, which is why it
 * runs last.
 */
chrome.downloads.onDeterminingFilename.addListener((item) => {
  if (item.byExtensionId || item.endTime) return;
  if (!cached.enabled) return;
  if (!/^https?:/i.test(item.finalUrl || item.url)) return;
  if (item.fileSize > 0 && item.fileSize < cached.minimumBytes) return;
  if (cached.skipExtensions.includes(extensionOf(item.filename || ""))) return;

  void chrome.downloads.cancel(item.id);

  void (async () => {
    const url = item.finalUrl || item.url;
    const fileName = item.filename ? item.filename.split(/[\\/]/).pop() : undefined;

    if (await handOver({ url, fileName, referrer: item.referrer, totalBytes: item.fileSize })) {
      await chrome.downloads.erase({ id: item.id });
      return;
    }

    // Hand the download back rather than leaving the person with a cancelled entry and no
    // file. The restarted one carries byExtensionId, so the guards above let it through.
    await notifyFailure();
    await chrome.downloads.erase({ id: item.id });
    try {
      await chrome.downloads.download({ url });
    } catch {
      // Nothing further to try; the badge already said so.
    }
  })();
});

/** A way to take any link, including the ones the click route deliberately leaves alone. */
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "downlism-link",
      title: "用 Downlism 下載",
      contexts: ["link", "image", "video", "audio"],
    });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;

  void (async () => {
    if (!(await handOver({ url, referrer: info.pageUrl || tab?.url }))) await notifyFailure();
  })();
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  // Route 1: the content script has already stopped the click, so this only decides whether
  // the download happens in Downlism or is replayed to the browser.
  if (message?.type === "link") {
    void (async () => {
      if (!cached.enabled) {
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

  if (message?.type === "settings") {
    refreshSettings().then(sendResponse);
    return true;
  }

  if (message?.type === "save") {
    chrome.storage.local.set(message.settings).then(() => sendResponse({ saved: true }));
    return true;
  }

  return false;
});

// Declared a module so each entry point keeps its own scope; without this TypeScript treats
// these files as one global script and the shared helper names collide.
export {};
