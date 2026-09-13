/**
 * Decides which of a page's requests are worth offering as a video.
 *
 * A modern video page issues hundreds of requests, and a sniffer that lists all of them is
 * worse than no sniffer: the one useful entry is buried under fragments, thumbnails, beacons
 * and advertisement rolls. Everything here exists to throw things away.
 *
 * Kept free of the chrome APIs so it can be tested directly, the same way links.ts is.
 */

/** A manifest is one request that describes the whole video. It is always worth listing. */
const MANIFEST_EXTENSIONS = ["m3u8", "m3u", "mpd", "f4m", "ism"];

/** Whole-file media, the kind that can simply be downloaded. */
const FILE_EXTENSIONS = ["mp4", "webm", "mkv", "mov", "m4v", "flv", "avi", "m4a", "mp3", "ogg", "wav", "flac", "opus"];

/**
 * Fragments. Each is a second or two of a stream that a manifest already accounts for, and
 * listing them would produce hundreds of rows describing one video.
 */
const FRAGMENT_EXTENSIONS = ["ts", "m4s", "cmfv", "cmfa", "aac", "vtt", "srt", "key"];

const FRAGMENT_TYPES = ["video/mp2t", "application/octet-stream"];

/**
 * Sites where a sniffed URL is worthless: the media address is signed, short-lived, split
 * across ranges, or all three. Only the page address can be downloaded, and yt-dlp knows how.
 * Mirrors the list in TransferRouting.cs.
 */
const PAGE_ONLY_HOSTS = [
  "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com", "twitch.tv",
  "bilibili.com", "nicovideo.jp", "tiktok.com", "twitter.com", "x.com",
  "facebook.com", "instagram.com", "soundcloud.com", "bandcamp.com",
  "reddit.com", "streamable.com", "odysee.com", "rumble.com",
];

/** Below this a direct media file is a preview, a sound effect or an advertisement sting. */
export const MINIMUM_MEDIA_BYTES = 512 * 1024;

export interface FoundMedia {
  url: string;
  /** "stream" needs yt-dlp to assemble it; "file" is a single download. */
  form: "stream" | "file";
  label: string;
  bytes: number;
}

export function extensionOfUrl(url: string): string {
  try {
    const path = new URL(url).pathname;
    const dot = path.lastIndexOf(".");
    const slash = path.lastIndexOf("/");
    return dot > slash ? path.slice(dot + 1).toLowerCase() : "";
  } catch {
    return "";
  }
}

export function isPageOnlyHost(url: string): boolean {
  let host: string;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return false;
  }

  return PAGE_ONLY_HOSTS.some((known) => host === known || host.endsWith("." + known));
}

/** The last path segment, which is the only human-readable part a media URL usually has. */
export function labelFor(url: string): string {
  try {
    const parsed = new URL(url);
    const segments = parsed.pathname.split("/").filter(Boolean);
    const last = segments.pop();
    if (!last) return parsed.hostname;

    // A manifest is nearly always called master.m3u8 or index.mpd, which identifies nothing.
    // The folder above it usually carries the title or at least the video id.
    const generic = /^(master|index|manifest|playlist|stream|video|main)\.[a-z0-9]+$/i.test(last);
    const parent = segments.pop();
    return generic && parent ? `${parent}/${last}` : last;
  } catch {
    return url;
  }
}

/**
 * Classifies one response. Returns null for everything that is not a video worth offering,
 * which is the overwhelming majority of what a page requests.
 */
export function classify(url: string, contentType: string, contentLength: number): FoundMedia | null {
  if (!/^https?:/i.test(url)) return null;

  const extension = extensionOfUrl(url);
  const type = contentType.split(";")[0].trim().toLowerCase();

  if (FRAGMENT_EXTENSIONS.includes(extension)) return null;

  if (MANIFEST_EXTENSIONS.includes(extension) || type === "application/vnd.apple.mpegurl" || type === "application/x-mpegurl" || type === "application/dash+xml") {
    return { url, form: "stream", label: labelFor(url), bytes: 0 };
  }

  const looksLikeMedia = type.startsWith("video/") || type.startsWith("audio/") || FILE_EXTENSIONS.includes(extension);
  if (!looksLikeMedia) return null;

  // An octet-stream of unknown length is as likely to be a fragment as a file, and a wrong
  // guess here is what fills the list with noise.
  if (FRAGMENT_TYPES.includes(type) && !FILE_EXTENSIONS.includes(extension)) return null;
  if (contentLength > 0 && contentLength < MINIMUM_MEDIA_BYTES) return null;

  return { url, form: "file", label: labelFor(url), bytes: contentLength };
}

/**
 * Adds a find to a tab's list, keeping it deduplicated and bounded.
 *
 * A live stream re-requests its manifest every few seconds, so without the first check one
 * video would fill the list on its own; without the second, a page left open overnight would
 * grow an unbounded array in session storage.
 */
export function merge(existing: FoundMedia[], found: FoundMedia, limit = 24): FoundMedia[] {
  if (existing.some((entry) => entry.url === found.url)) return existing;

  const merged = [...existing, found];
  return merged.length > limit ? merged.slice(merged.length - limit) : merged;
}
