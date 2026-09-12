/** Settings surface for the extension. Talks to the worker, never to the host directly. */

const state = document.getElementById("state") as HTMLSpanElement;
const enabled = document.getElementById("enabled") as HTMLInputElement;
const minimum = document.getElementById("minimum") as HTMLInputElement;

const MEGABYTE = 1024 * 1024;

async function refresh(): Promise<void> {
  const settings = await chrome.runtime.sendMessage({ type: "settings" });
  enabled.checked = settings.enabled;
  minimum.value = String(Math.round(settings.minimumBytes / MEGABYTE));

  const { running } = await chrome.runtime.sendMessage({ type: "ping" });
  // Say what the user can act on, not what the code observed.
  state.textContent = running ? "已連線" : "Downlism 未啟動";
  state.dataset.running = String(running);
}

async function save(): Promise<void> {
  await chrome.runtime.sendMessage({
    type: "save",
    settings: {
      enabled: enabled.checked,
      minimumBytes: Math.max(0, Number(minimum.value) || 0) * MEGABYTE,
    },
  });
}

enabled.addEventListener("change", () => void save());
minimum.addEventListener("change", () => void save());

void refresh();
