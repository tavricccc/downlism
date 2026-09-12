/**
 * Hands downloads to Downlism.
 *
 * Two routes in, because MV3 has no way to block a response. The content script catches
 * download links at the click, so the browser never opens the connection at all — that is the
 * route that actually intercepts. Anything that starts some other way (a script, a redirect, a
 * form post) only becomes visible once chrome.downloads reports it, and by then the response
 * headers have arrived and bytes are in flight; those are cancelled as early as possible.
 *
 * Runs as a service worker, which the browser recycles after roughly thirty seconds of
 * idleness. Nothing here may assume it stays alive between downloads: there is no long-lived
 * native port, and settings live in chrome.storage.
 */

const HOST_NAME = "com.downlism.host";

/** URLs handed back to the browser, which must not be taken over a second time. */
const restoring = new Set<string>();

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
 * The settings as of the last read, kept in memory so the download listener can decide
 * synchronously. Awaiting chrome.storage inside that listener would mean more of the file
 * arriving before the cancel lands, which is exactly what this is trying to avoid. The cache
 * starts from the defaults after every worker restart, so at worst one download immediately
 * after a cold start is judged by the defaults.
 */
let cached: Settings = { ...DEFAULTS };

async function refreshSettings(): Promise<Settings> {
  cached = { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) } as Settings;
  return cached;
}

void refreshSettings();
chrome.storage.onChanged.addListener(() => void refreshSettings());

function extensionOf(filename: string): string {
  const dot = filename.lastIndexOf(".");
  return dot < 0 ? "" : filename.slice(dot + 1).toLowerCase();
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

function shouldTakeOver(item: chrome.downloads.DownloadItem): boolean {
  if (!cached.enabled) return false;
  if (!/^https?:/i.test(item.finalUrl || item.url)) return false;

  // A negative or zero size means the server did not declare one; those are judged on name
  // alone rather than assumed to be small.
  if (item.fileSize > 0 && item.fileSize < cached.minimumBytes) return false;
  if (cached.skipExtensions.includes(extensionOf(item.filename || ""))) return false;

  return true;
}

/**
 * The fallback route, for downloads that did not start from a link. onDeterminingFilename
 * rather than onCreated: by this point the browser has followed redirects and resolved the
 * name and MIME type, which are what decide whether the download is worth taking over.
 */
chrome.downloads.onDeterminingFilename.addListener((item) => {
  // A download handed back to the browser must survive this listener, or cancelling and
  // restarting it would loop forever.
  if (restoring.delete(item.finalUrl || item.url)) return;
  if (!shouldTakeOver(item)) return;

  // Cancelled before anything is awaited, because every await is more of the file arriving.
  void chrome.downloads.cancel(item.id);

  void (async () => {
    const url = item.finalUrl || item.url;
    const fileName = item.filename ? item.filename.split(/[\\/]/).pop() : undefined;

    if (await handOver({ url, fileName, referrer: item.referrer, totalBytes: item.fileSize })) {
      await chrome.downloads.erase({ id: item.id });
      return;
    }

    // Downlism did not take it, so give the download back rather than leaving the person with
    // a cancelled entry and no file.
    await notifyFailure();
    await chrome.downloads.erase({ id: item.id });
    restoring.add(url);
    try {
      await chrome.downloads.download({ url });
    } catch {
      // Nothing further to try; the badge already said so.
    }
  })();
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  // The interception route: the content script has already stopped the click, so this only
  // decides whether the download happens in Downlism or is replayed to the browser.
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
