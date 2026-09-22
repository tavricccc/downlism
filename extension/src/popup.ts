/** Settings surface for the extension. Talks to the worker, never to the host directly. */

import type { FoundMedia } from "./media.js";
import { parseExtensions, parseHosts } from "./settings.js";

const state = document.getElementById("state") as HTMLSpanElement;
const enabled = document.getElementById("enabled") as HTMLInputElement;
const minimum = document.getElementById("minimum") as HTMLInputElement;
const media = document.getElementById("media") as HTMLDivElement;
const pageAction = document.getElementById("page-video") as HTMLButtonElement;

const sniff = document.getElementById("sniff") as HTMLInputElement;
const skip = document.getElementById("skip") as HTMLTextAreaElement;
const blocked = document.getElementById("blocked") as HTMLTextAreaElement;
const allowed = document.getElementById("allowed") as HTMLTextAreaElement;
const saveStatus = document.getElementById("save-status") as HTMLParagraphElement;
let loaded = false;
const MEGABYTE = 1024 * 1024;

let pageUrl = "";

async function refresh(): Promise<void> {
  const settings = await chrome.runtime.sendMessage({ type: "settings" });
  enabled.checked = settings.enabled;
  minimum.value = String(settings.minimumBytes / MEGABYTE);
  sniff.checked = settings.sniffMedia;
  skip.value = settings.skipExtensions.join(", ");
  blocked.value = settings.blockedHosts.join("\n");
  allowed.value = settings.allowedHosts.join("\n");
  loaded = true;

  const { running } = await chrome.runtime.sendMessage({ type: "ping" });
  // Say what the user can act on, not what the code observed.
  state.textContent = running ? "已連線" : "Downlism 未啟動";
  state.dataset.running = String(running);

  const found = await chrome.runtime.sendMessage({ type: "found" });
  pageUrl = found.pageUrl ?? "";
  render(found.media ?? [], Boolean(found.pageOnly));
}

function size(bytes: number): string {
  if (bytes <= 0) return "";
  const megabytes = bytes / MEGABYTE;
  return megabytes >= 1024 ? `${(megabytes / 1024).toFixed(1)} GB` : `${megabytes.toFixed(0)} MB`;
}

function render(found: FoundMedia[], pageOnly: boolean): void {
  media.replaceChildren();

  // The page button is always available: the sniffer only sees what the page has already
  // requested, and on a site that signs its media URLs it will correctly see nothing.
  pageAction.disabled = !/^https?:/i.test(pageUrl);

  if (found.length === 0) {
    const empty = document.createElement("p");
    empty.className = "empty";
    empty.textContent = pageOnly
      ? "這個網站的影片網址無法單獨下載，請用下方的按鈕把整個頁面交給 Downlism。"
      : "還沒有在這個頁面上發現影片。開始播放後再打開一次。";
    media.append(empty);
    return;
  }

  for (const entry of found) {
    const row = document.createElement("button");
    row.className = "found";
    row.type = "button";

    const label = document.createElement("span");
    label.className = "label";
    label.textContent = entry.label;
    label.title = entry.url;

    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = entry.form === "stream" ? "串流" : size(entry.bytes) || "檔案";

    row.append(label, meta);
    row.addEventListener("click", () => void take(row, { ...entry }));
    media.append(row);
  }
}

async function take(row: HTMLButtonElement, entry: FoundMedia): Promise<void> {
  row.disabled = true;
  const { handled } = await chrome.runtime.sendMessage({
    type: "download-media",
    url: entry.url,
    form: entry.form,
    pageUrl,
  });

  // The popup closes the moment focus leaves it, so the result has to be visible immediately
  // or not at all.
  row.dataset.result = handled ? "taken" : "failed";
  row.disabled = handled;
}

pageAction.addEventListener("click", () => {
  void (async () => {
    pageAction.disabled = true;
    const { handled } = await chrome.runtime.sendMessage({
      type: "download-media",
      url: pageUrl,
      form: "page",
      pageUrl,
    });

    pageAction.textContent = handled ? "已交給 Downlism" : "Downlism 未接受";
    pageAction.disabled = handled;
  })();
});

async function save(): Promise<void> {
  if (!loaded) return;
  try {
    if (minimum.value.trim() === "" || !Number.isFinite(Number(minimum.value))) throw new Error("請輸入最小檔案大小。");
    const reply = await chrome.runtime.sendMessage({
      type: "save",
      settings: {
        enabled: enabled.checked, minimumBytes: Number(minimum.value) * MEGABYTE,
        sniffMedia: sniff.checked, skipExtensions: parseExtensions(skip.value),
        blockedHosts: parseHosts(blocked.value), allowedHosts: parseHosts(allowed.value),
      },
    });
    saveStatus.textContent = reply.saved ? "" : (reply.error ?? "設定未儲存");
  } catch (error) {
    saveStatus.textContent = error instanceof Error ? error.message : "設定未儲存，請重開擴充功能再試。";
  }
}

enabled.addEventListener("change", () => void save());
minimum.addEventListener("change", () => void save());

for (const field of [sniff, skip, blocked, allowed]) field.addEventListener("change", () => void save());
void refresh().catch(() => { state.textContent = "無法連線"; saveStatus.textContent = "請重新開啟擴充功能；設定尚未變更。"; });

// Declared a module so each entry point keeps its own scope; without this TypeScript treats
// these files as one global script and the shared helper names collide.
export {};
