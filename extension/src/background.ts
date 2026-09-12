/**
 * Intercepts downloads and hands them to Downlism.
 *
 * Runs as an MV3 service worker, which the browser recycles after roughly thirty seconds of
 * idleness. Nothing here may assume it stays alive between downloads: there is no long-lived
 * native port, and all state lives in chrome.storage rather than in module scope.
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

async function loadSettings(): Promise<Settings> {
  const stored = await chrome.storage.local.get(DEFAULTS);
  return { ...DEFAULTS, ...stored } as Settings;
}

function extensionOf(filename: string): string {
  const dot = filename.lastIndexOf(".");
  return dot < 0 ? "" : filename.slice(dot + 1).toLowerCase();
}

function shouldIntercept(item: chrome.downloads.DownloadItem, settings: Settings): boolean {
  if (!settings.enabled) return false;
  if (!/^https?:/i.test(item.finalUrl || item.url)) return false;

  // A negative size means the server did not declare one. Those are usually streams or
  // generated files, and are left to the browser.
  if (item.fileSize > 0 && item.fileSize < settings.minimumBytes) return false;
  if (settings.skipExtensions.includes(extensionOf(item.filename || ""))) return false;

  return true;
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

async function handOver(item: chrome.downloads.DownloadItem): Promise<boolean> {
  const url = item.finalUrl || item.url;
  const message = {
    url,
    fileName: item.filename ? item.filename.split(/[\\/]/).pop() : undefined,
    referrer: item.referrer || undefined,
    cookies: await cookieHeaderFor(url),
    userAgent: navigator.userAgent,
    totalBytes: item.fileSize > 0 ? item.fileSize : 0,
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
 * onDeterminingFilename rather than onCreated: by this point the browser has followed
 * redirects and resolved the name and MIME type, which are exactly what decides whether the
 * download is worth taking over.
 */
chrome.downloads.onDeterminingFilename.addListener((item) => {
  void (async () => {
    const settings = await loadSettings();
    if (!shouldIntercept(item, settings)) return;

    if (await handOver(item)) {
      // Cancel only after Downlism has accepted it, so a failed handover still leaves the
      // browser's own download running.
      await chrome.downloads.cancel(item.id);
      await chrome.downloads.erase({ id: item.id });
    } else {
      await notifyFailure();
    }
  })();
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "ping") {
    chrome.runtime
      .sendNativeMessage(HOST_NAME, { url: "", ping: true })
      .then((reply) => sendResponse({ running: Boolean(reply?.accepted) }))
      .catch(() => sendResponse({ running: false }));
    return true;
  }

  if (message?.type === "settings") {
    loadSettings().then(sendResponse);
    return true;
  }

  if (message?.type === "save") {
    chrome.storage.local.set(message.settings).then(() => sendResponse({ saved: true }));
    return true;
  }

  return false;
});
