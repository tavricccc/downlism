# Downlism

Windows 下載管理員。多執行緒分段下載、斷點續傳，並透過瀏覽器擴充功能接管 Chrome 與 Edge 的下載。

Downlism 是獨立產品，與 [Flowlism](https://github.com/tavricccc/flowlism) 啟動器、[Peeklism](https://github.com/tavricccc/peeklism) 預覽工具分開安裝、分開更新。三者共用同一套視覺語言（WinUI 3、Mica、Fluent），但不共用行程，也沒有互相依賴。

## 現況

專案剛起步。目前完成的是引擎最底層、也最容易寫錯的一段：**在任何位元組落地之前，正確判斷一個 URL 能不能分段下載、要存成什麼檔名，以及中斷後如何接回去**。

| 元件 | 狀態 |
| --- | --- |
| `Content-Range` 解析 | 完成 |
| `Content-Disposition` 檔名推導（含 RFC 5987 與惡意路徑淨化） | 完成 |
| 分段規劃 | 完成 |
| 分段進度 sidecar（崩潰復原） | 完成 |
| HTTP 連線設定與探測 | 完成 |
| 傳輸迴圈 | 未開始 |
| WinUI 3 介面 | 未開始 |
| 瀏覽器擴充與 Native Messaging Host | 未開始 |

## 幾個刻意的決定

**探測用 `Range: bytes=0-0`，不用 `HEAD`。** CDN 拒絕或誤報 HEAD 的比例太高，而一次一位元組的 GET 就能同時定案總長度、續傳支援、最終網址、驗證碼與檔名。

**分段下載強制 HTTP/1.1。** 在 HTTP/2 上，多條「連線」會被多工到同一條 TCP、共用同一個壅塞視窗，分段完全失去意義卻仍付出每段的額外成本。

**關閉自動解壓縮。** `Range` 的位移計算的是壓縮後的位元組；開啟自動解壓會讓每一段落在錯誤的檔案位移上，而且產出的檔案大小正確、內容損壞，不會有任何錯誤訊息。

**單一目標檔搭配 `RandomAccess`，不做分段檔合併。** `SafeFileHandle` 自 .NET 6 起保證可由多執行緒併發寫入不同位移。分段檔再合併要多一倍磁碟空間與 I/O，且合併階段無法中斷。

**分段進度存在 sidecar，不進 SQLite。** 八條連線每秒回報數十次的寫入速率會撐大 WAL 並卡住 checkpoint；崩潰復原只需要「這個檔案下到哪」的局部事實，不需要全域交易一致性。

完整的技術選型與取捨記錄見 `docs/tech-selection.md`。

## 建置

```
dotnet build Downlism.slnx
dotnet test Downlism.slnx
```

需要 .NET SDK 10.0.401 與 Windows 11 build 26100 以上。
