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

> **v0.4 更動：** `WindowsAppSDKSelfContained` 兩種版型都產生，一起裝進同一個單檔安裝程式；WinUI 優先來自共用的 MSIX framework package，由安裝程式偵測、詢問並代為登錄，不成則改裝自帶一份的版型。理由與取捨見 §8.9。

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

## 8. 影片下載（v0.4 已實作）

**外掛 yt-dlp + ffmpeg 執行檔，不自己寫 HLS/DASH parser。**

網站的取流規則每週在變，自己維護等於一份全職工作。yt-dlp 有數千名貢獻者在追這件事。

不打包進安裝程式（體積 + 授權），首次使用時下載到 `%LocalAppData%\Downlism\tools`。超過 14 天會在背景重新取得一次，失敗時沿用舊的並把時間戳往前推，避免網路長期不通時每次下載都先做一次註定失敗的更新。

**進度用 `--progress-template` 自訂前綴解析，不解析英文進度文字。** 後者會在第一個改寫措辭的版本壞掉。`--print after_move:` 取得最終檔名；`--newline` 讓每筆進度自成一行，否則預設的 carriage-return 重繪是一條永遠不換行的輸出，`OutputDataReceived` 永遠不會觸發。

**每個串流畫成一段進度。** 影片一般是影像流 + 聲音流分開下載再合併，格式 id 從 `%(info.format_id)s` 取得，兩段依序填滿。合併與後製階段沒有位元組數可報，只報階段名稱。

**取消時 `Kill(entireProcessTree: true)`。** 只殺 yt-dlp 會留下一個握著輸出檔的 ffmpeg，下次續傳會卡在被鎖住的路徑上。

### 8.1 嗅探

擴充功能在 `onHeadersReceived` 上分類回應，只收 manifest（m3u8/mpd）與夠大的完整媒體檔，`.ts`/`.m4s`/`.aac` 這類片段一律丟掉——一小時的影片是數千個片段，列出來等於把唯一有用的 manifest 埋掉。

結果存在 `chrome.storage.session` 而非模組變數：service worker 閒置約三十秒就被回收，而一個頁面可以播很久。

已知只能整頁交給 yt-dlp 的網站（YouTube、bilibili 等）直接不嗅探，popup 改為提供「整頁交給 yt-dlp」。

---

## 8.5 BitTorrent（v0.4 已實作，推翻 §9 的決定）

**MonoTorrent 3.0.2（MIT）。**

原本以「與其餘部分零共用，等於第二個產品」排除。這句話對引擎成立，對引擎以外的一切不成立：佇列、同時下載數、暫停與繼續、清單持久化、系統匣通知、分段進度條全部原封不動適用。真正要加的只有一個 `ITransferEngine` 實作。

分段進度條在這裡比在任何地方都貼切——swarm 本來就是亂序填滿檔案的。bitfield 折成 16 個桶，每桶對應真實位元組範圍。真實片段數動輒上萬，照畫只會是一塊實心色。

**一個 `ClientEngine` 服務所有種子。** 第二個等於第二個監聽埠、第二張 DHT 表，以及一個被切成兩半、彼此看不見的節點池。

**監聽埠用 0，交給系統配。** 固定在慣用的 BitTorrent 埠範圍只會跟機器上其他東西相撞。

**完成即停止做種。** 繼續上傳是關於使用者頻寬與法律暴露的決定，不是下載管理員可以自己開始的。上傳上限保留 256 KB/s 而非 0：多數 swarm 會餓死完全不回饋的節點。

**速率上限設在 session 而非單一 transfer。** MonoTorrent 的節流在 socket 層統一做，而且節點是共用的。

---

## 8.6 每個下載的確認視窗（v0.4）

瀏覽器接手時開一個小視窗，而不是把主清單拉到最前面。

**回覆瀏覽器與詢問使用者分開。** NMH 的回覆必須立刻送出，不能讓瀏覽器等一個人；因此擷取一律先回 `accepted`，視窗裡按取消只代表什麼都不下載。

**視窗高度用 `Measure` 量出來，寬度固定。** 寬度固定是因為來源那一行要靠它折行，而一個網址長到不受限量測會要求比螢幕還寬的視窗。高度則隨內容而定：磁力連結折兩行、貼上的網址一行、瀏覽器擷取時頁尾多一個核取方塊。

**`ResizeClient` 而非 `Resize`。** 標題列高度隨主題與 Windows 版本而異，不該由我們代為計算。

**量測完才把 presenter 設為不可調整大小。** 已經被告知不可調整大小的視窗會忽略後續的 resize，結果是視窗停在框架預設尺寸、最後一列被切掉。

---

## 8.7 擴充功能安裝教學（v0.4）

**安裝程式在安裝成功後以 `--extension-guide` 啟動主程式。** 更新不帶這個旗標——擴充功能已經在位，再跳一次就只是騷擾。

**教學視窗掛在主視窗自己的 `Loaded` 上，再以低優先度排入佇列。** `Activated` 在 app 拿到訂閱機會之前通常就已經觸發過了，而直接排入佇列會和主清單的第一次版面配置搶同一幀。

**瀏覽器用執行檔名啟動並帶上 `chrome://extensions` 當引數。** 這類頁面是 Chromium 保留頁，交給 shell 直接開會被拒絕。執行檔名透過 App Paths 登錄解析，所以裝在哪裡都找得到；兩個都找不到時退回複製網址並說明。

---

## 8.8 程式碼可見性（v0.4）

**Release 建置不輸出 `.pdb`（`DebugType=none`）。**

這不是保護，是不要主動奉送。Downlism 是自帶執行環境的 .NET 應用程式，`Downlism.App.dll` 是 IL，任何反編譯器都能還原出接近原始碼的 C#；擴充功能更是直接以未壓縮的 JavaScript 出貨，因為 Chrome 本來就要求未封裝載入。符號檔會再附上行號與區域變數名稱，對執行安裝程式的人毫無用處。

NativeAOT 能讓反編譯困難得多，但 WinUI 3 不支援（見 §9），所以這條路是關著的。結論：把安裝程式交給別人，等同於把原始碼交給別人。

---

## 8.9 部署模型（v0.4 改為共用執行環境）

**WinUI 改為 framework-dependent，.NET 維持 self-contained。**

§0 原本的理由是「使用者得先自己裝執行環境」。這個理由在安裝程式願意代勞之後不成立：`Downlism.Bootstrap` 是純 .NET、不碰 WinUI，可以在 WinUI 版的安裝介面啟動之前先把執行環境裝好。設定介面自己就是 WinUI，所以這件事只能發生在它之前。

**不需要提權。** 微軟文件明載：有管理員權限時安裝程式會呼叫 `ProvisionPackageForAllUsersAsync` 做全機佈建；失敗時「安裝會只針對目前使用者進行」。回傳碼 `0x80070005` 專指全機那條路。Downlism 本來就是 per-user 安裝，退回那條路正是我們要的。

**MSIX 套件隨安裝程式打包，偵測後才登錄。** 檢查 `Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe` 是否已註冊且版本 ≥ 2.4.0，缺少才詢問並以 `PackageManager.AddPackageAsync` 登錄四個套件。曾經改成從 `aka.ms` 下載微軟的 redist，後來改回打包：安裝程式在最需要能動的那一刻不該依賴網路，而共用的是套件最後放在哪裡，不是它們從哪裡來。代價是安裝程式多 45 MB。

**成敗不看個別套件的結果，看事後狀態。** 部分安裝過的機器會為已存在的套件回報失敗（實測會拿到 `0x80073D02`：套件正被其他程式使用）；真正的判準是之後 framework package 能不能用。

**進度用 shell 的 `IProgressDialog`。** Bootstrap 是純 .NET 且不可能有 UI 框架——它正在裝的就是那個框架。自己刻視窗類別與訊息迴圈是一百行可能出錯的 interop，而 shell 從 Windows 2000 就有這個對話框，成本是一個 COM 介面宣告。`AddPackageAsync` 本身會回報百分比，所以進度是真的。

**不需要開發人員模式。** 執行環境套件的 `SignatureKind` 是 `Store`，微軟簽章；微軟文件裡「需要 sideloading」只適用於 experimental / preview 版本，而 sideloading 自 Windows 10 2004 起預設開啟。實測機器上 `AppModelUnlock` 機碼不存在（開發人員模式未開）仍可正常登錄。

**自帶版型是同一個安裝程式裡的 fallback，不是另一個下載。** 以前有兩個安裝程式，而不肯登錄共用元件的機器會被告知去找另外那一個——一個人得先發現自己需要另一版，才知道自己需要另一版。現在兩種版型一起放在同一個檔案裡，選擇在 `Program.ChooseLayout` 做：framework package 是否已登錄、使用者要不要、登錄有沒有成功，三件事這段程式全看得到。

**兩種版型同檔不等於兩倍大。** 自帶版型是共用版型加上那套 SDK 二進位檔，封裝腳本逐檔比對 SHA-256，相同的存進 `common/`，不同的分別進 `shared/` 與 `standalone/`。實測 270 個檔案只存一份，整個安裝程式 163 MB——比從前單一版型的資料夾式安裝程式還小。

**安裝內容接在執行檔後面，不是編進去。** 那是將近兩百 MB 已經壓縮過的二進位檔，走 `EmbeddedResource` 與 single-file bundler 要花幾分鐘與大量記憶體，換來位元組相同的結果。.NET 的 single-file bundle 由 publish 時寫進 host 的標頭定位，不是從檔尾往回掃，所以尾巴多接資料不會干擾它。結尾 24 位元組是長度加 `DOWNLISM-PAYLOAD` 標記，`Payload.cs` 讀回來。

**同樣的改動已套用到 Flowlism 與 Peeklism。** 三個產品刻意不共用程式碼，所以是複製而非參照——與 `SingleInstanceGate.cs` 的處理方式一致。共用只有在三者釘同一個 WindowsAppSDK 大版本時才成立，這是一個跨三個專案的長期約束。

**升級自動換版型。** `InstallationUpdate.Apply` 本來就會移除新安裝紀錄裡不存在的檔案，所以自帶版型升級到共用版型時那 145 MB 會自己離開。必須如此：`Microsoft.UI.Xaml.dll` 留在執行檔旁邊的載入順序高於 framework package。`VerifySource` 的必要檔案因此從 `Microsoft.UI.Xaml.dll` 改成兩種版型都有的 `Microsoft.WinUI.dll`。

**刪掉 `onnxruntime.dll` 與 `DirectML.dll`。** Windows App SDK 的 metapackage 會連機器學習堆疊一起複製進來，而 Downlism 一個 Windows AI API 都沒呼叫。沒有官方屬性可以排除，所以在封裝腳本裡刪。這是非官方裁剪，已實測：完整 UI、設定面板、新增下載視窗與一次真實下載都正常。

### 體積結果

| | 安裝資料夾 | 安裝程式 |
| --- | --- | --- |
| v0.4 之前（自帶 SDK、含 AI 堆疊） | 233 MB | 235 MB |
| v0.4 自帶版型（已裁剪） | 194 MB | — |
| v0.4 共用版型 | 122 MB | — |
| v0.4 單檔安裝程式（兩種版型 + MSIX） | — | 163 MB |

安裝程式裡有兩種版型與 45 MB 的 MSIX 套件，卻比任何一種版型的安裝資料夾加上套件都小：重複的檔案只存一份，而整包再壓縮過。MSIX 只給安裝程式用，不會被複製進安裝資料夾。

共用的 framework package 由 Windows 集中保管一份（約 110 MB），三個 lism 指向同一份。

---

## 9. 明確不選

| 技術 | 不選的理由 |
| --- | --- |
| ~~BitTorrent / MonoTorrent~~ | ~~與其餘部分零共用，等於第二個產品~~ — v0.4 推翻，見 §8.5 |
| 自製 HLS / DASH parser | 取流規則每週在變，維護成本等於一份全職工作，見 §8 |
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
