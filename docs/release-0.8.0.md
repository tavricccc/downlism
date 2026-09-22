# Downlism 0.8.0

## 更新內容

- 下載視窗的「這次下載的設定」移至取消按鈕左側，改用灰色下拉按鈕開啟浮出面板。
- 主視窗、設定、瀏覽器擴充功能指引與新增下載的操作訊息改用 WinUI ContentDialog。同一視窗的對話框依序顯示，下載進度仍留在原處。
- 設定頁標題、分頁與擴充功能操作步驟改用 SemiBold。
- 工具下載改用最多 8 條分段連線。網路中斷最多嘗試 3 次，沿用已完成的分段；不支援 Range 的來源使用單一串流。
- App 在解析影片前自動補齊 yt-dlp 與 Deno，下載前補齊 FFmpeg／ffprobe。舊安裝缺少 Deno 也會補裝，不需要手動安裝或設定 PATH。
- yt-dlp 不再讀取使用者的命令列設定；品質查詢不必先選定可下載格式。修正 JSON 中高度或位元率為 null 時的解析錯誤。
- 工具先下載至暫存位置，完成才替換已安裝檔案，避免更新失敗時刪掉舊版。

## 驗證

- 187 項 C# 測試通過，涵蓋乾淨工具目錄、舊安裝缺少執行環境、同時初始化、下載失敗重試、Range 與非 Range 來源。
- WinUI 建置成功，無警告或錯誤；打包時 34 項擴充功能測試及 TypeScript 型別檢查通過。
- 實際透過 App 的 MediaProbe 自動下載原本不存在的 Deno，成功解析 `LYANS5AwwnA` 的 8 種影片畫質及音訊品質。這是格式查詢測試，未下載整部影片。
- 隔離 UI 測試通過：下載設定浮出面板、設定儲存、深色對話框、批次新增、暫停續傳、8 MiB 檔案 SHA-256、搜尋與重開還原。
- UI 證據：`artifacts/customization-smoke/20260922-193951/`。

媒體工具首次取得仍需要連線至 GitHub；網站限制、登入與 DRM 不在這次修正範圍。乾淨 VM、完整影片下載、螢幕閱讀器與多 DPI 尚未完整驗證。安裝程式未簽章，SmartScreen 可能警告。更新會保留現有設定與下載紀錄。

## 重建

```powershell
pwsh -File scripts/Publish-Installer.ps1 -Version 0.8.0
```

安裝檔：`artifacts/installer/Downlism.Setup.exe`，173,443,547 bytes（165.4 MiB）。建置紀錄：`artifacts/publish-0.8.0.log`。

SHA-256：`46223298027EC2362E3A7D3263F0EBCA68D2F541F6292FA97586290F031C5672`

本機已從舊版更新並啟動 0.8.0。啟動前比對 `settings.json` 與 `downloads.db` 的 SHA-256，兩者均與更新前一致；備份位於 `artifacts/pre-upgrade-0.8.0/`。安裝完成截圖：`artifacts/install-0.8.0-complete.png`。
