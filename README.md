<p align="center"><img src="docs/assets/downlism-icon-fluent.png" width="96" height="96" alt="Downlism" /></p>

<h1 align="center">Downlism</h1>
<p align="center">多執行緒下載、斷點續傳，並接手 Chrome 與 Edge 的下載。</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-0.2.1-165674?style=flat-square" alt="Version 0.2.1" />
  <img src="https://img.shields.io/badge/Windows_11-26100%2B-0078D4?style=flat-square" alt="Windows 11 build 26100+" />
  <img src="https://img.shields.io/badge/WinUI-3-0078D4?style=flat-square" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET 10" />
  <img src="https://img.shields.io/badge/C%23-239120?style=flat-square" alt="C#" />
  <img src="https://img.shields.io/badge/SQLite-003B57?style=flat-square" alt="SQLite" />
  <img src="https://img.shields.io/badge/status-preview-D97706?style=flat-square" alt="Preview" />
</p>

Downlism 把一個檔案切成多段、用多條連線同時下載，中斷後從原處接回去。它是獨立產品，與 [Flowlism](https://github.com/tavricccc/flowlism) 啟動器、[Peeklism](https://github.com/tavricccc/peeklism) 預覽工具分開安裝、分開更新；三者共用同一套視覺語言（WinUI 3、Mica、Fluent），但不共用行程，也沒有互相依賴。

## 介面

每一列下載都帶著一條**分段進度條**，顯示這次傳輸真正的分段界線，每一段從自己的起點往前填。這條進度條直接讀取續傳用的 sidecar，所以畫面上看到的就是當機後真正能保留下來的進度。一條平均過的進度條會把下載管理員存在的理由藏起來——哪一條連線卡住了，從平均值上永遠看不出來。

數字固定在靠右對齊的欄位裡。一列每秒更新四次，若讓數字自由伸縮，整列會在游標下不停抖動。

## 現況

| 功能 | 狀態 |
| --- | --- |
| 分段下載、斷點續傳、佇列、速度上限 | 完成 |
| 貼上網址下載 | 完成 |
| 清單跨重啟保留（SQLite） | 完成 |
| 瀏覽器擴充功能與 Native Messaging Host（Chrome、Edge） | 完成 |
| 安裝程式與瀏覽器登記 | 完成 |
| 系統匣常駐與開機自啟 | 完成 |
| Firefox 擴充功能 | 未開始（見下） |
| 影片嗅探（HLS／DASH） | 未開始 |

## 幾個刻意的決定

**探測用 `Range: bytes=0-0`，不用 `HEAD`。** CDN 拒絕或誤報 HEAD 的比例太高，而一次一位元組的 GET 就能同時定案總長度、續傳支援、最終網址、驗證碼與檔名。

**分段下載強制 HTTP/1.1。** 在 HTTP/2 上，多條「連線」會被多工到同一條 TCP、共用同一個壅塞視窗，分段完全失去意義卻仍付出每段的額外成本。

**關閉自動解壓縮。** `Range` 的位移計算的是壓縮後的位元組；開啟自動解壓會讓每一段落在錯誤的檔案位移上，而且產出的檔案大小正確、內容損壞，不會有任何錯誤訊息。

**單一目標檔搭配 `RandomAccess`，不做分段檔合併。** `SafeFileHandle` 自 .NET 6 起保證可由多執行緒併發寫入不同位移。分段檔再合併要多一倍磁碟空間與 I/O，且合併階段無法中斷。

**分段進度存在 sidecar，不進 SQLite。** 八條連線每秒回報數十次的寫入速率會撐大 WAL 並卡住 checkpoint；崩潰復原只需要「這個檔案下到哪」的局部事實。SQLite 只保管清單本身。

**關閉視窗只是收進系統匣。** 傳輸繼續，瀏覽器仍然可以遞交新的下載，真正結束程式的入口在系統匣選單裡。下載管理員被關掉等於下一個下載悄悄回到瀏覽器手上，而使用者不會發現。

**不保存 Cookie。** Cookie 是工作階段憑證，為了讓續傳方便一點而把它寫到磁碟上並不划算。需要登入的續傳會直接失敗並說明原因。

**瀏覽器溝通走 Named Pipe，不走 localhost 連接埠。** 連接埠會觸發防火牆詢問、會衝突，而且任何本機程式都能連上去排下載。Pipe 的 ACL 只開放目前使用者。

完整取捨記錄見 [docs/tech-selection.md](docs/tech-selection.md)。

## 瀏覽器擴充功能

擴充功能以開發者模式載入，安裝程式已經先幫 Chrome、Edge 登記好 Native Messaging Host。設定步驟見 [docs/extension.md](docs/extension.md)。

Firefox 暫時不支援：Firefox 強制簽章，`about:debugging` 的臨時載入重開瀏覽器就消失，必須先跑完 AMO 簽章流程。這是技術限制，不是取捨。

## 未簽章

Downlism 目前沒有程式碼簽章憑證。SmartScreen 會顯示「發行者：不明」，防毒也可能誤報——寫登錄檔、掛進瀏覽器、大量連線、下載檔案到磁碟，這組行為與木馬下載器高度重疊。這是已知且刻意的決定，背景見技術選型文件第 10 節。

## 建置

```
dotnet build Downlism.slnx
dotnet test Downlism.slnx
pwsh -File scripts/Publish-Installer.ps1
```

需要 .NET SDK 10.0.401、Node.js（建置擴充功能）與 Windows 11 build 26100 以上。安裝程式產出在 `artifacts/installer/current`：最外層只有 `Downlism.Setup.exe`，其餘檔案在 `resources` 子資料夾，兩者必須一起保留。預設安裝到 `%LocalAppData%\Programs\Downlism`。

`scripts/Smoke-Ui.ps1` 會啟動本機測試伺服器、實際跑一次下載並截圖；`scripts/Smoke-Restore.ps1` 重開程式，確認上一輪的下載有回來而且可以繼續；`scripts/Smoke-Tray.ps1` 確認關閉視窗只是隱藏、視窗叫得回來，以及 `--background` 啟動時不會跳出視窗。
