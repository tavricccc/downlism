/**
 * Decides whether a clicked link is a download worth taking over before the browser fetches it.
 *
 * Kept separate from the content script so it can be tested without a browser, and because
 * this is the one judgement in the extension that can visibly break a page: a false positive
 * stops an ordinary navigation. It is deliberately conservative — an explicit download
 * attribute, or a file extension that is unambiguously not a page.
 */

/** Extensions that are downloads rather than pages, kept narrow on purpose. */
export const DOWNLOAD_EXTENSIONS: ReadonlySet<string> = new Set([
  "7z", "apk", "appimage", "bin", "bz2", "deb", "dmg", "exe", "flac", "gz", "img", "iso",
  "jar", "m4a", "mkv", "mov", "mp3", "mp4", "msi", "msix", "pkg", "rar", "rpm", "tar",
  "tgz", "torrent", "wav", "webm", "whl", "xz", "zip", "zst",
]);

/**
 * A magnet link is the one non-http scheme worth intercepting. Left to the browser it opens
 * whatever torrent client is registered, or nothing at all — and clicking it is already an
 * unambiguous statement that the person wants the thing downloaded.
 */
export function isMagnet(href: string): boolean {
  return /^magnet:\?/i.test(href.trim());
}

export function extensionOfPath(pathname: string): string {
  const name = pathname.slice(pathname.lastIndexOf("/") + 1);
  const dot = name.lastIndexOf(".");
  return dot < 0 ? "" : name.slice(dot + 1).toLowerCase();
}

export function isDownloadUrl(href: string, base?: string): boolean {
  if (isMagnet(href)) return true;

  let url: URL;
  try {
    url = new URL(href, base);
  } catch {
    return false;
  }

  if (url.protocol !== "http:" && url.protocol !== "https:") return false;
  return DOWNLOAD_EXTENSIONS.has(extensionOfPath(url.pathname));
}

/**
 * The full test applied at click time. <paramref name="hasDownloadAttribute" /> is the author
 * stating outright that the link saves a file, which outranks any guess made from the name.
 */
export function looksLikeADownload(href: string, hasDownloadAttribute: boolean, base?: string): boolean {
  if (isMagnet(href)) return true;

  let url: URL;
  try {
    url = new URL(href, base);
  } catch {
    return false;
  }

  if (url.protocol !== "http:" && url.protocol !== "https:") return false;
  return hasDownloadAttribute || DOWNLOAD_EXTENSIONS.has(extensionOfPath(url.pathname));
}
