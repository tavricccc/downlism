# 下載管理員 — 技術選型

> 產品名稱：**Downlism**
> 日期：2026-09-12（2026-09-12 更新：名稱、瀏覽器範圍、簽章方案已拍板）

---

## 0. 直接沿用 Flowlism / Peeklism 的基礎（不重新選）

| 項目 | 選擇 |
| --- | --- |
| TFM | `net10.0-windows10.0.26100.0`，SDK 10.0.401 |
| UI | WinUI 3 / WindowsAppSDK 2.4.0，`WindowsPackageType=None` + `WindowsAppSDKSelfContained` |
| MVVM | CommunityToolkit.Mvvm 8.4.2 |
| 資料庫 | Microsoft.Data.Sqlite 10.0.10 |
| 測試 | xunit 2.9.3 |
| 建置 | 中央套件版本管理、`TreatWarningsAsErrors`、`Deterministic`、x64 only |
| 安裝 | Bootstrap + Setup 兩段式，`scripts/Publish-Installer.ps1` |

`Flowlism.Core/Lifecycle/SingleInstanceGate.cs` 可以原樣搬過來。下載管理員**必須**單一實例——兩個行程同時寫同一個目標檔會直接毀檔，這比啟動器的單例需求更硬。

---

## 1. 專案結構

```
src/
  Downlism.Core        引擎。無 UI、無 WinUI 依賴，全部可單元測試
  Downlism.App         WinUI 3 主程式（常駐 + 系統匣）
  Downlism.Host        Native Messaging Host（console Exe）
  Downlism.Bootstrap   安裝引導
  Downlism.Setup       安裝程式
extension/          TypeScript 擴充功能，同一個 repo
tests/
```

`Downlism.Host` 必須是 `OutputType=Exe`（console）而**不是** `WinExe`——瀏覽器透過重導的 stdin/stdout 溝通，WinExe 拿不到 stdio。不會閃黑窗：Chrome 以 `CREATE_NO_WINDOW` 啟動 NMH。

---

## 2. HTTP 層

**選 `SocketsHttpHandler`**（`HttpClient` 預設）。不用 WinHttpHandler、不用 libcurl、不用 aria2 當後端。

四個必須在第一天就寫對的細節：

### 2.1 關閉自動解壓縮
```csharp
AutomaticDecompression = DecompressionMethods.None
```
`Range` 的 byte offset 是對**壓縮後**的內容算的。開了自動解壓，分段 offset 會全部錯位，而且錯得很安靜。帶 `Content-Encoding` 的回應只能單執行緒整檔下載。

### 2.2 強制 HTTP/1.1 做分段
```csharp
request.Version = HttpVersion.Version11;
request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
```
HTTP/2 會把 8 條「連線」多工到同一條 TCP 上，分段下載的加速效果完全消失，還共用同一個壅塞視窗。這是很多自製下載器「開 8 線跟 1 線一樣快」的真正原因。

### 2.3 用 Range 探測，不用 HEAD
HEAD 被 CDN 擋掉的比例很高。改用：
```
GET  Range: bytes=0-0
```
- 回 `206` + `Content-Range: bytes 0-0/12345` → 拿到總長度，**同時**證明支援續傳
- 回 `200` → 不支援 Range，退回單線
- 順便拿到 `Content-Disposition`、最終 URL、`ETag`

一次往返解決三件事，比 `HEAD` + `Accept-Ranges` 可靠得多。

### 2.4 自己處理重定向
```csharp
AllowAutoRedirect = false
```
理由：要逐跳累積 cookie、要保留 `Referer`、要用**最終** URL 推檔名。自動重定向會把這些資訊吃掉。

### 2.5 HttpClient 生命週期
每個「站台設定檔」一個 `HttpClient`，**不是**每個下載任務一個。任務量大時每任務一個會 socket 耗盡。`MaxConnectionsPerServer` 對應使用者設定的每站連線數，`PooledConnectionLifetime` 設 2 分鐘避免 DNS 失效。

---

## 3. 檔案寫入

**單一目標檔 + `RandomAccess`**，不用 `.part` 分段檔再合併。

```csharp
using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write,
                                   FileShare.Read, FileOptions.Asynchronous);
await RandomAccess.WriteAsync(handle, buffer, offset, ct);
```

`SafeFileHandle` + `RandomAccess` 從 .NET 6 起明確保證可多執行緒併發寫同一 handle 的不同 offset。不要每段開一個 `FileStream`（各自有 buffer 跟獨立的 file position，會互相打架）。

分段檔再合併的做法要多一倍磁碟空間、多一倍 I/O，且合併階段無法中斷——直接淘汰。

其他決定：
- 開檔後立刻 `SetLength(total)` 預先配置，減少碎片
- 下載中檔名為 `xxx.ext.download`，完成才 rename（原子性，也讓防毒不會在半成品上誤判）
- 完成後寫入 `Zone.Identifier` ADS（mark-of-the-web）。這是瀏覽器的標準行為，不寫的話使用者會覺得「這個下載器繞過了系統保護」
- 放棄 `SetFileValidData`（需要 `SeManageVolumePrivilege`，得提權，不值得）

---

## 4. 狀態持久化 —— 刻意分兩層

| 資料 | 儲存 | 理由 |
| --- | --- | --- |
| 任務清單、歷史、設定、分類 | SQLite | 沿用既有慣例，要查詢 |
| **每段的即時進度** | sidecar `.dlstate` 二進位檔 | 見下 |

分段進度**不要**進 SQLite。8 條連線每秒回報數十次，寫進 SQLite 會讓 WAL 暴漲、checkpoint 卡住主執行緒。而且崩潰復原要的是「這個檔案下到哪」的局部資訊，不需要全域交易一致性。

固定大小的 sidecar（header + 每段 `{start, end, completed}`），定期 flush，崩潰後最多重下幾 MB。

續傳驗證存 `ETag` / `Last-Modified` / `Content-Length`，續傳時送 `If-Range`——伺服器換檔就回 `200` 而不是 `206`，此時整檔重下。這比自己比對 header 可靠。

---

## 5. IPC：Native Host ↔ 主程式

**Named Pipe + 長度前綴 JSON。**

```
NamedPipeServerStream(@"Downlism.Ingest", PipeDirection.InOut, ...)
```

不用 localhost HTTP / gRPC，理由：
- localhost 埠會觸發防火牆詢問
- 埠衝突要處理
- 任何本機程式都能連上去（下載任意檔案的 RCE 面）

Pipe 用 `PipeSecurity` 限定目前使用者 SID。`Downlism.Host` 保持極薄——收到瀏覽器訊息就轉發，主程式沒在跑就 `Process.Start` 拉起來（路徑從自身所在目錄推算，不依賴登錄檔）。

---

## 6. 擴充功能

- **TypeScript + esbuild，無框架。** 整個擴充大概 500 行，上 React 是負收益
- `manifest.json` 內嵌 `"key"` 固定 extension ID（見上一輪討論），Chrome 與 Edge 共用同一份 build
- 權限：`downloads`、`cookies`、`nativeMessaging`、`<all_urls>`
- 攔截點 `chrome.downloads.onDeterminingFilename`，不是 `onCreated`（後者拿不到最終檔名與 MIME）
- MV3 service worker 會被回收，每次攔截重新 `sendNativeMessage`（one-shot），不維持長連線

Firefox 版延後，原因是強制簽章讓 unpacked 無法常駐（技術限制，非流程問題），等 AMO 簽章流程跑完再開。

---

## 7. 序列化

`System.Text.Json` + source generator（`JsonSerializerContext`）。NMH 要求毫秒級啟動，反射式序列化的暖機成本佔比太高。

---

## 8. 影片下載（v0.3 預留位）

**外掛 yt-dlp + ffmpeg 執行檔，不自己寫 HLS/DASH parser。**

網站的取流規則每週在變，自己維護等於一份全職工作。yt-dlp 有數千名貢獻者在追這件事。

不打包進安裝程式（體積 + 授權），首次使用時下載到 `%LocalAppData%`。

---

## 9. 明確不選

| 技術 | 不選的理由 |
| --- | --- |
| BitTorrent / MonoTorrent | 與其餘部分零共用，等於第二個產品 |
| NativeAOT | WinUI 3 不支援 |
| Entity Framework | 對這個 schema 過重，Flowlism 也沒用 |
| aria2 / libcurl 當後端 | 多一個行程、進度回報粒度粗、錯誤處理不可控。自己寫引擎反而簡單 |
| LSP / 協定 hook 攔截（IDM 舊招） | 現代 Defender 會直接當可疑行為處理 |

---

## 10. 程式碼簽章（已決議：暫不處理）

**決議：跳過程式碼簽章，先以未簽章方式開發與散布。**

記錄當時評估過的事實，供日後重啟此議題時參考：

- 未簽章時，SmartScreen 會顯示「發行者：不明」，且預設只給「不要執行」一顆按鈕。Downlism 的行為特徵（寫登錄檔、掛進瀏覽器、大量網路連線、下載檔案到磁碟、常駐行程被瀏覽器喚起）與木馬下載器高度重疊，防毒誤報機率高於 Flowlism 與 Peeklism。
- 自 2023-06 起，OV 憑證私鑰亦須存放於 FIPS 140-2 Level 2 硬體 token 或 HSM。
- 自 2026-03-01 起，公開信任的程式碼簽章憑證最長效期為 458 天。
- Azure Artifact Signing（USD 9.99/月，免硬體）個人開發者限美國／加拿大，台灣身分預期不符資格。
- 曾評估的方案：Certum Open Source Developer（€69 含智慧卡與讀卡機，續約 €29，禁止商業散布）與 SSL.com IV + eSigner（本人姓名、可接 CI、成本高一個量級）。
- OV 與 IV 均不會讓 SmartScreen 立即放行，需靠簽章量累積 reputation，通常數月。只有 EV 有立即豁免。

### 對建置流程的影響

`scripts/Publish-Installer.ps1` 仍預留簽章階段，但預設為停用；日後取得憑證時只需開啟，不需重寫流程。簽章對象應涵蓋 `Downlism.App.exe`、`Downlism.Host.exe`、`Downlism.Install.exe`、`Downlism.Setup.exe`，並加上時間戳記。

### 仍應執行的降低誤報措施（與憑證無關）

- NMH 不得放在 `%TEMP%` 或任何使用者可寫入的路徑
- 安裝路徑維持 `%LocalAppData%\Programs\Downlism`，與既有兩個產品一致
- 下載中的檔案使用 `.download` 副檔名，完成才更名

---

## 已拍板

1. **名稱：Downlism**
2. **首版僅支援 Chromium（Chrome / Edge）**，Firefox 待 AMO 簽章流程後再開
3. **簽章流程優先於程式開發啟動**

## 已拍板

1. **名稱：Downlism**
2. **首版僅支援 Chromium（Chrome / Edge）**，Firefox 待 AMO 簽章流程後再開
3. **跳過程式碼簽章**，以未簽章方式開發與散布；建置流程預留階段但停用
