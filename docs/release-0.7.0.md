# Downlism 0.7.0 測試版

安裝檔：`artifacts/installer/Downlism.Setup.exe`，173,446,366 bytes（165.4 MiB）。

SHA-256：`C8C6981A728EF7AA348AD8DB13D8C610EBED292B10AE7D8493451D7304FF17E3`

打包紀錄：`artifacts/publish-0.7.0.log`。上一版安裝檔保留在 `artifacts/installer/history/`。

## 更新前注意

- 瀏覽器擴充功能升為 0.2.0。更新程式後，在 Chrome／Edge 的擴充功能頁重新載入 Downlism，並重新整理原本開著的網頁；只更新 EXE 不會換掉頁面裡已載入的 content script。
- 新版續傳紀錄會綁定原始網址與來源驗證碼。舊版、未綁定來源的 `.dlstate` 不會直接沿用，未完成的 HTTP 下載可能從頭開始。既有完成檔案不會被刪除。
- 自動接手需要觀察到 GET 回應，再由瀏覽器確認實際下載。POST、blob、XHR、MEGA 與無法確認的來源留給瀏覽器；仍可明確用右鍵手動交給 Downlism。
- 未簽章，SmartScreen 仍可能警告。這次產出安裝檔，但沒有自動安裝，也沒有推送 GitHub。

## 新增

- 獨立設定視窗：自訂下載資料夾、1–32 條連線、1–16 個同時下載、KiB/s 限速、HTTP 逾時、重試次數與起始間隔。
- 跟隨系統／淺色／深色主題、關閉到系統匣、剪貼簿提示、接手確認、進度視窗、置頂、完成／失敗通知、啟動續傳與防止重複網址。
- 自訂分類，例如 `電子書=epub,mobi`。規則優先於內建分類，會檢查重複副檔名、非法資料夾與路徑穿越。
- JSON 設定匯入／匯出、還原預設值。匯入與還原必須按儲存才套用，不包含 Cookie、下載歷史或開機自啟登錄。
- 搜尋檔名／來源、檔名與網站排序、Ctrl／Shift 多選、選取項目暫停／繼續／移除／複製網址。
- 最多 500 筆的批次新增、TXT 網址匯入、目前檢視網址匯出。預設先加入暫停清單，重複網址只保留一筆。
- 個別 HTTP 下載可設定連線、限速與預期 SHA-256；校驗失敗不發布為完成檔案。
- 擴充功能可設定不接手副檔名、排除網站、限定網站與影片偵測開關。

## 修正

- 分段下載不再共用續傳紀錄寫入緩衝區，並拒絕重疊、缺漏或越界的紀錄。
- 檢查 HTTP Content-Range 的起點、終點、總長度、編碼與實收長度；分段失敗立即取消其他連線。
- 探測請求帶入必要標頭，跨來源重新導向不轉送原始 Cookie；拒絕 HTTPS 降級。讀取與等待標頭皆有逾時。
- 沒有強 ETag／Last-Modified 時不跨次續傳。遺失 partial 檔案也不會誤把完整 sidecar 當成完整下載。
- 同時下載數共用一個可調整的 FIFO gate，不再讓新舊 semaphore 各自放行。
- 全部暫停包含排隊與重試；重複繼續不會啟動第二份工作；移除後不會被背景取消事件復活。
- 保存解析後的檔名／資料夾及各下載選項，修正歷史倒序與舊設定缺欄位時預設值遺失。
- 檔名避開 Windows 裝置名稱、方向控制字元，長檔名保留 partial／sidecar 所需空間。
- HTTP 每條連線使用 64 KiB 緩衝區；雜湊也改用 64 KiB 串流。主清單將更新合併成每 250 ms 一批，系統匣每 500 ms 更新。
- `.torrent` HTTP 中繼資料限制為 16 MiB，包含無 Content-Length 的回應，避免無界限讀入記憶體。

## 已驗證

- C# 測試 177 項、擴充功能測試 34 項通過；WinUI 專案編譯成功。
- 真正的 WinUI 操作：設定儲存、自訂分類、深色切換、批次去重、暫停／續傳、搜尋、關閉結束與重開後還原。
- 本機 HTTP 伺服器的 8 MiB 檔案下載完成，SHA-256 核對成功。
- 證據：`artifacts/customization-smoke/20260922-150118/`，含截圖及 `result.json`。
- 這次 UI 操作後的一次記憶體取樣：working set 201.5 MiB、private memory 164.2 MiB。不是純待機、不是長時間峰值，也沒有與 AB 或舊版在相同條件比較；不能據此宣稱已達低 RAM 目標。
- NuGet（包含間接相依）與 npm audit 本次沒有回報已知漏洞。這不等於安全稽核完成。

## 還沒做／還沒驗證

- 定時下載、週期排程、下載完成後關機／休眠。
- 真正的全域即時限速、時段限速、每網站連線配額與可拖曳優先順序。目前 HTTP／影片的速度設定套用於新下載；BitTorrent 共用 session 上限。
- 代理伺服器設定、每網站帳密／自訂標頭、安全憑證保存、下載失敗後更換過期網址。
- 多鏡像來源、同名但不同來源 partial 檔案的完整隔離策略。現在會防止同時寫入，但仍可能遇到檔案被使用而失敗，必須稍後重試。
- Torrent 檔案選取／優先順序、長時間大量種子記憶體回收，以及影片／Torrent 的真實網路回歸測試。
- 真實 Chrome／Edge 與 MEGA、登入網站、串流網站的端到端測試。此次擴充功能驗證是模擬瀏覽器事件，不是真實 MEGA 下載。
- 大型檔案、磁碟滿、斷電、睡眠喚醒、數百筆活動下載及長時間記憶體／吞吐量基準。
- 小視窗／高 DPI／螢幕閱讀器的完整驗證。個別下載進階欄位與設定檔選擇器沒有全部跑過 UI 自動化。
- Firefox、商店上架、程式碼簽章、自動更新；乾淨 VM 上的安裝／升級／解除安裝回歸。

## 重跑

```powershell
dotnet test tests/Downlism.Tests/Downlism.Tests.csproj -c Release
dotnet build src/Downlism.App/Downlism.App.csproj -c Release -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
pwsh -File scripts/Smoke-Customization.ps1
pwsh -File scripts/Publish-Installer.ps1 -Version 0.7.0
```

`Smoke-Customization.ps1` 會拒絕在已有 Downlism 行程時執行，並透過 `DOWNLISM_DATA_DIRECTORY` 使用獨立設定與歷史。測試下載放在該次 artifacts 目錄，不清理使用者的 Downloads。不要用舊的 `Smoke-Ui.ps1` 取代：舊腳本仍會清理 Downloads 下的 `sample*`，本次沒有執行它。
