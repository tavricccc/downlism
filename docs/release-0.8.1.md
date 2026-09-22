# Downlism 0.8.1

- 主視窗與獨立下載視窗改用 WinUI 3 原生 determinate `ProgressBar`，移除自訂分段繪圖控制項。HTTP 的多連線下載與續傳仍保留。
- 主清單改為單列：檔名、下載大小、進度／狀態、平均速度、操作。來源、剩餘時間及完整錯誤保留在提示中。
- 平均速度以進度樣本間實際新增的位元組除以傳輸時間，排除暫停、排隊、重試等待、工具下載，以及續傳前已有的資料。完成後保留，重開 App 也會還原。
- 安裝與解除安裝的進度依目前階段實際處理的檔案數更新，顯示階段名稱和完成數。沒有總量時只顯示狀態文字，不顯示循環動畫或虛構百分比。
- 修正影片工具下載的檔名／資料夾不應覆蓋影片工作資訊；未知串流大小不再被當成 100%。

## 更新與驗證

資料庫新增平均速度統計與檔案大小欄位，沿用既有紀錄。舊下載沒有歷史速度樣本時顯示「—」，不回推不存在的統計。

196 項 C# 測試通過，涵蓋時間加權平均、暫停續傳、統計保存、舊資料庫遷移、工具流量排除，以及安裝／移除的實際進度。App 與安裝程式建置均無警告或錯誤；打包時 34 項擴充功能測試與 TypeScript 型別檢查通過。

隔離 UI 測試已確認原生 ProgressBar 的 RangeValue 介於 0 和 1、下載期間顯示平均速度、完成後保留數值、重開後還原，並核對 8 MiB 測試檔案的 SHA-256。證據：`artifacts/customization-smoke/20260922-202238/`。

影片／BT 真實網路下載、多 DPI 與螢幕閱讀器尚未完整回歸。媒體進度取自 yt-dlp 目前已回報的串流總量；後續出現另一條串流或修正估計大小時，百分比可能調整。

## 重建

```powershell
pwsh -File scripts/Publish-Installer.ps1 -Version 0.8.1
```

安裝檔：`artifacts/installer/Downlism.Setup.exe`，173,444,523 bytes（165.4 MiB）。建置紀錄：`artifacts/publish-0.8.1.log`。

SHA-256：`8937FD007A7F4AFF3CAC17917EDB33D33EF175DCBB8F06C8C23FD9895A23BD36`

本機已更新並啟動 0.8.1。安裝後、首次啟動前，設定與下載資料庫的 SHA-256 均與備份相同；新版首次啟動會加入上述統計欄位。備份：`artifacts/pre-upgrade-0.8.1/`。

安裝期間透過 UI Automation 取得 27 次原生進度數值，含階段內的中間進度；紀錄：`artifacts/install-0.8.1-progress.json`，完成截圖：`artifacts/install-0.8.1-complete.png`。
