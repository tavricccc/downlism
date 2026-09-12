# 瀏覽器擴充功能

Downlism 透過一個 MV3 擴充功能接管 Chrome 與 Edge 的下載。擴充功能本身不下載任何東西，它只是把「這個檔案交給 Downlism」這件事連同 Cookie、Referer 與 User-Agent 一起遞過去。

## 安裝

安裝程式已經把擴充功能放進安裝資料夾，也已經替 Chrome、Edge 與 Chromium 登記好 Native Messaging Host。剩下的只有載入：

1. 開啟 `chrome://extensions`（Edge 為 `edge://extensions`）
2. 打開右上角的「開發人員模式」
3. 按「載入未封裝項目」
4. 選擇 `%LocalAppData%\Programs\Downlism\extension`

從原始碼直接測試時，選 `extension/dist`（先跑 `npm install && node build.mjs`）。

載入後，擴充功能圖示的彈出視窗會顯示「已連線」或「Downlism 未啟動」。顯示未啟動時，先確認 Downlism 曾經安裝過一次——登記是安裝程式寫進 `HKCU` 的。

## 為什麼擴充功能 ID 是寫死的

未封裝的擴充功能，ID 是從載入它的資料夾路徑算出來的：換一台機器、換一個位置，ID 就變了。Native Messaging 的 manifest 必須在 `allowed_origins` 裡指名擴充功能的 ID，所以浮動的 ID 等於 manifest 沒辦法隨安裝程式一起出貨。

解法是把公鑰嵌進 `manifest.json` 的 `key` 欄位，ID 就固定下來，而且日後真的上架時仍是同一個 ID。金鑰由 `scripts/New-ExtensionKey.ps1` 產生，私鑰寫到 `artifacts/`（不進版本控制），只有簽 `.crx` 或在商店宣告同一個 ID 時才需要。

目前的 ID 是 `fcnaeaaphjcgmiojojkjehnealidjodm`，在 `BrowserRegistration.ExtensionId` 與 `extension/manifest.json` 兩處各有一份。`BrowserRegistrationTests` 會從 manifest 的公鑰重新推導 ID 並比對常數——金鑰若被重新產生而常數沒跟著改，登記會安靜地失效，下載只是「不再被接手」而不會報錯。

## 兩條攔截路徑

MV3 拿掉了可封鎖的 `webRequest`，所以擴充功能沒有辦法在回應階段擋下一個下載。等到 `chrome.downloads` 通報時，回應標頭已經到了、位元組已經在路上——在那裡取消，永遠會白下載一部分。

因此攔截分成三條路，外加一個右鍵選單：

**點擊攔截（content script）。** 在使用者按下下載連結的當下就 `preventDefault()`，Chrome 根本不會發出那個請求。這才是真正的「從頭攔截」，一個位元組都不會浪費。判斷刻意保守：只認 `download` 屬性，或副檔名明確不是網頁的那一類（zip、exe、iso、mp4……）。誤判會擋掉正常的頁面導覽，比漏接一個下載嚴重得多，判斷規則在 `src/links.ts`，由 `npm test` 覆蓋。

Downlism 若沒接手，連結會被重新觸發一次交還給瀏覽器，使用者不會察覺中間發生過什麼。

**標頭預判（service worker）。** `webRequest.onHeadersReceived` 在回應標頭抵達時就看得到 `Content-Disposition`、`Content-Length`、`Content-Type`，當場決定要不要接手並記下網址；等 `chrome.downloads.onCreated` 通報時，取消是零延遲送出的。這是 Neat Download Manager 的做法，比在 downloads 事件裡才開始判斷嚴格更好——到那時候標頭早就沒了。決定會在 30 秒後過期，否則一個沒有產生下載的判斷會在很久以後誤殺另一個下載。

**最後手段（service worker）。** 沒有任何標頭宣告的下載——重新導向鏈、blob、伺服器不送 `Content-Disposition`——只剩 `onDeterminingFilename` 看得到。這條會在**任何 `await` 之前**就先取消，設定因此快取在記憶體裡讓判斷同步完成。它排在最後，因為它一定會浪費一小段檔案。

兩條 downloads 路徑都會跳過 `byExtensionId` 不為空的項目（那是擴充功能自己發起的，交還下載時就靠這個避免無限迴圈）以及已經有 `endTime` 的項目（那是歷史紀錄，不是新下載）。這兩個守則來自 AB Download Manager。

**右鍵選單。** 「用 Downlism 下載」對連結、圖片、影片、音訊都能用，適合點擊攔截刻意放過的那些。

## 哪些下載會被接手

取消路徑預設只接手 1 MB 以上、且副檔名不在瀏覽器內建檢視清單（pdf、html、txt、svg、json、xml）裡的 http／https 下載。點擊攔截沒有大小門檻可用——還沒發出請求，誰也不知道檔案多大——所以它只靠副檔名清單判斷。小檔案在遞交的來回之間就結束了，接手它們只會讓瀏覽器用起來像在跟擴充功能打架。兩項門檻都可以在彈出視窗裡改。

攔截點是 `chrome.downloads.onDeterminingFilename` 而不是 `onCreated`：到這一步瀏覽器已經跟完重新導向、決定好檔名與 MIME 類型，而這些正是判斷值不值得接手的依據。

取消是在遞交之前就送出的，所以遞交失敗時瀏覽器的下載已經停了。這種情況下擴充功能會重新發起一次同樣的下載交還給瀏覽器，並在圖示上顯示一個驚嘆號；重新發起的那一次帶有標記，不會再被接手一次。

## MV3 的服務工作者會被回收

閒置大約三十秒後，MV3 的背景服務工作者就會被瀏覽器回收，長連線跟著斷。所以擴充功能每次攔截都重新呼叫一次 `sendNativeMessage`，不維持常駐的 port，設定的來源一律是 `chrome.storage`。

記憶體裡只留一份設定快取，目的單純是讓取消能同步發生。它在每次工作者重啟後從預設值開始，所以最壞情況是冷啟動後緊接著的第一個下載用預設門檻判斷。這是刻意的取捨：多等一次非同步讀取，換來的是更多位元組落地。

## Native Messaging Host

`Downlism.Host.exe` 是 console 執行檔而不是 WinExe：瀏覽器透過重新導向的 stdin／stdout 與它溝通，WinExe 兩者都拿不到。Chrome 以 `CREATE_NO_WINDOW` 啟動它，所以畫面上不會閃出黑窗。

它本身不下載任何東西。瀏覽器擁有這個行程，服務工作者一被回收就可能連它一起收掉，而那遠短於一次傳輸的壽命。它收到的每一則訊息都透過 named pipe 轉給 Downlism 主程式；主程式沒在跑時，它會從自己所在的資料夾啟動一次主程式再重試。啟動路徑不從登錄檔讀、也不從訊息裡取，否則一個被竄改的登記就能把瀏覽器變成任意程式的啟動器。
