/**
 * Only intercept explicit magnet links. HTTP links, including download attributes and file
 * extensions, stay with the page until chrome.downloads confirms an actual download.
 * A URL ending in .zip can still be an HTML preview or a page-managed encrypted transfer.
 */

import { isMagnet } from "./links.js";

/** Marks a click this script generated, so the fallback navigation is not caught again. */
const REPLAY = "data-downlism-replay";

/**
 * Sends the click somewhere else entirely. Returning false means Downlism did not take it, and
 * the navigation is replayed so the browser handles it as it normally would.
 */
async function handOver(anchor: HTMLAnchorElement, url: URL): Promise<void> {
  try {
    const accepted = await chrome.runtime.sendMessage({
      type: "link",
      url: url.href,
      fileName: anchor.getAttribute("download") || undefined,
      referrer: location.href,
    });

    if (accepted?.handled) return;
  } catch {
    // The service worker was gone or the host refused; fall through to the browser.
  }

  anchor.setAttribute(REPLAY, "1");
  try {
    anchor.click();
  } finally {
    anchor.removeAttribute(REPLAY);
  }
}

document.addEventListener(
  "click",
  (event) => {
    if (event.defaultPrevented || event.button !== 0) return;
    // Modified clicks mean the person asked for something specific: a new tab, a save dialog.
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;

    const anchor = (event.target as Element | null)?.closest?.("a[href]") as HTMLAnchorElement | null;
    if (!anchor || anchor.hasAttribute(REPLAY)) return;

    if (!isMagnet(anchor.href)) return;

    let url: URL;
    try {
      url = new URL(anchor.href, location.href);
    } catch {
      return;
    }

    // Stopping the click here is what makes this an interception rather than a cancellation.
    event.preventDefault();
    event.stopPropagation();
    void handOver(anchor, url);
  },
  // Capture, so a page that swallows clicks in its own handlers does not hide them.
  true,
);

// Declared a module so each entry point keeps its own scope; without this TypeScript treats
// these files as one global script and the shared helper names collide.
export {};
