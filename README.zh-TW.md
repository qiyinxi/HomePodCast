# HomePodCast

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [日本語](README.ja.md)

把 Windows 電腦的聲音即時串流到 HomePod（AirPlay 2），專為打電動和看影片打造的低延遲方案。

TuneBlade 在 HomePod 軟體 27 上已經發不出聲音；HomePodCast 是從零開始實作的原生 C# 版本，單一 exe 約 350 KB，不需要虛擬音效卡。

## 實測

| 情境 | 數值 |
| --- | --- |
| 聲音比畫面晚（延遲設 120 ms，以手機 60 fps 錄影實測） | 156 ms |
| 聲音比畫面晚（延遲設 105 ms，推算） | 約 141 ms |
| 串流統計（斷音／遲發／重傳） | 0 / 0 / 0 |
| 記憶體（私有） | 約 28 MB |

## 測試裝置

HomePodCast 是個人專案，實際只在一套裝置上測試過：

- 揚聲器：一台 HomePod（第二代），HomePod 軟體 27.0（24J361），Wi‑Fi 連線
- 電腦：一台 Windows 11 電腦，預設輸出是虛擬音效卡

沒有在實際裝置上測過的：

- HomePod mini、HomePod（第一代）、Apple TV、其他品牌的 AirPlay 揚聲器
- **立體聲組合和多房間**：依協定資料實作，只用模擬揚聲器測試過
- 實體音效卡和耳機、其他 Windows 版本，以及電腦走有線或無線網路的差別

如果你在其他裝置上試用，歡迎開 issue 回報：裝置型號、軟體版本、能穩定不斷音的最低延遲設定，以及記錄檔（`%APPDATA%\HomePodCast\homepodcast.log`）。

## 功能

- AirPlay 2 即時音訊：暫時性配對、全程加密，延遲可在 100–500 ms 間調整（以 1 ms 為單位）
- 單一視窗介面：首頁、混音器、麥克風與音效、設定；跟隨 Windows 淺色／深色模式
- 場景模式：推薦（120 ms，預設）、遊戲（105 ms）、音樂（500 ms，最穩）、影視（200 ms，搭配瀏覽器擴充功能和播放器的音訊延遲設定）、自訂
- 系統匣程式：自動找到揚聲器、自動重新連線、睡眠喚醒後恢復，被其他裝置搶走時不會搶回來
- 在系統匣圖示上按右鍵開啟快速面板：音量和靜音、場景、夜間模式、麥克風、連線或中斷連線
- 混音器：每個程式各自的音量、靜音和音量表，可選擇依程式分流（HomePod／本機／兩者）或依音效卡分流（選擇擷取哪個輸出裝置），見下文
- 音量上限、夜間模式（壓縮動態範圍＋減弱低音）、全域快速鍵；鍵盤音量鍵也能調整 HomePod（跟隨 Windows 音量，或串流時只調整 HomePod），見下文
- 麥克風送到 HomePod，附殘響和等化器，可用耳機低延遲監聽；串流到揚聲器的所有聲音可選等化器預設集
- 立體聲組合和多房間同步（實驗性，尚未在實際裝置上驗證）
- 影音同步測試：螢幕閃光加上「喀」一聲，一眼看出聲音比畫面晚多少
- 瀏覽器擴充功能（測試版）：把 YouTube、bilibili 的畫面延後，和 HomePod 的聲音對齊
- 介面支援英文、簡體中文、繁體中文和日文

## 使用方式

1. 安裝 [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）。
2. 從 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 下載 HomePodCast，有兩種套件：
   - **安裝程式（建議）`HomePodCast-<版本>-x64.msi`**：可以選擇安裝位置（預設 `C:\Program Files\HomePodCast`），會自動新增開始功能表捷徑和所需的防火牆規則。升級時直接執行新版 MSI；解除安裝請到「設定 → 應用程式」，如果開啟過「開機時自動啟動」，請先在程式裡關閉。
   - **可攜版 `HomePodCast-<版本>-win-x64.zip`**：解壓縮到任意位置，執行 `HomePodCast.exe`，第一次會要求新增防火牆規則。

   防火牆規則只允許區域網路、只在私人網路下生效：HomePod 需要連回電腦進行對時和要求重傳。兩種套件的設定都儲存在 `%APPDATA%\HomePodCast`。
3. 程式會自動找到 HomePod 並連線。擷取發生在系統音量之前，Windows 音量不會改變送出去的聲音；HomePod 有自己的音量。預設 Windows 音量就是 HomePod 音量：兩邊保持一致（連線時對齊到較小的那個），Windows 靜音時 HomePod 也會靜音。如果預設輸出是電腦喇叭，又只想用 HomePod 聽，請在「設定 → 鍵盤音量鍵」選「串流時控制 HomePod」，再把 Windows 設為靜音（見[鍵盤音量鍵](#鍵盤音量鍵)）。

介面語言跟隨 Windows 顯示語言（沒有對應譯文的語言顯示英文）。若要改用其他語言，在「設定」頁選擇「語言 / Language」，或把 `%APPDATA%\HomePodCast\config.json` 中的 `"Language"` 設為語言代碼（`"zh-CN"`，或 `src/i18n` 中的檔名，例如 `"zh-TW"`、`"en"`、`"de"`）或 `"auto"`。

命令列（排查問題用）：

```
HomePodCast.exe scan                         列出區域網路中的 AirPlay 揚聲器
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     確認靜音後仍能擷取
```

## 全部串流與依程式分流

預設情況下，HomePodCast 擷取電腦的整個聲音輸出，全部送到 HomePod。這條路徑本身不會增加延遲，玩遊戲就用它。電腦本身的喇叭是否出聲由 Windows 音量決定。想只在 HomePod 播放：在「設定 → 鍵盤音量鍵」選「串流時控制 HomePod」（或「僅在 Windows 靜音時」），再把 Windows 設為靜音；在預設的「HomePod 跟隨 Windows 音量」下，Windows 靜音會讓 HomePod 也靜音。

依程式分流可以讓每個程式分別送到 HomePod、本機或兩者（在「混音器」頁設定），例如遊戲聲音送到 HomePod、語音聊天留在耳機裡。代價是延遲：

| | 全部串流（預設） | 依程式分流 |
| --- | --- | --- |
| 何時啟用 | 未開啟分流時 | 有程式單獨設定了去處，或預設去處是「本機」 |
| 如何擷取聲音 | 整個輸出一次擷取 | 逐一擷取各程式（Windows 處理程序回送） |
| 串流到 HomePod 的額外延遲 | 無 | 約 35 ms（實測從播放到送出：21 ms 對 55 ms） |
| 網頁影片影音同步（瀏覽器擴充功能） | 自動補償 | 自動補償，已含額外的 35 ms |
| 遊戲 | 延遲最低 | 多 35 ms，無法補償 |
| 設為「HomePod」的程式 | — | 本機靜音，Windows 音量混音程式中顯示 0% |
| 系統提示音 | 跟隨 Windows 音量 | 只在本機播放 |
| 系統需求 | Windows 10/11 | Windows 10 2004 或更新版本 |

第一次開啟分流時程式會跳出確認。把所有程式改回「預設」並把預設設為 HomePod，就回到全部串流。若分流時 HomePodCast 被強制結束，設為「HomePod」的程式會維持 0%，直到再次執行 HomePodCast。

### 依音效卡分流（擷取裝置）

「混音器 → 擷取裝置」決定串流哪個輸出裝置上的聲音，預設跟隨 Windows 預設輸出。選一個你不用來聽的裝置，再在 Windows 的應用程式音量設定（混音器頁有按鈕直達）或程式自己的設定裡，把要送到 HomePod 的程式（例如遊戲）的輸出設為它；語音聊天等其他程式照常在耳機裡播放。整個裝置一次擷取，和全部串流一樣沒有額外延遲，不像依程式分流那樣多約 35 ms。

- 需要一張閒置的或虛擬的音效卡，例如 VB-CABLE、網易 UU 加速器的虛擬音效卡、Steam Streaming Speakers，或不帶喇叭的 HDMI 螢幕。HomePodCast 無法自行安裝虛擬音效卡：那是需要微軟簽章的核心驅動程式。
- 有些遊戲只在啟動時選擇輸出裝置，改完要重新啟動遊戲。
- 所選裝置被拔除、停用或解除安裝時，不會改用預設輸出（以免把耳機裡的語音串流到音箱）：暫停擷取並在首頁提示，裝置恢復後自動繼續。
- 依程式分流可以疊加使用，規則只作用於這個裝置上的程式。
- 指定了擷取裝置時，「HomePod 跟隨 Windows 音量」跟隨的是這個裝置的音量；音量鍵只調整 Windows 預設輸出。想用音量鍵調整 HomePod，就把這個裝置設為預設輸出。
- 串流時，如果麥克風「本機監聽」用的正是被擷取的裝置（例如兩者都跟隨預設輸出），本機監聽會暫停，否則人聲會被串流到 HomePod 兩次。

## 鍵盤音量鍵

在「設定 → 鍵盤音量鍵」中選擇音量 +、− 和靜音鍵對 HomePod 的作用：

- **HomePod 跟隨 Windows 音量**（預設）：串流時 Windows 音量就是 HomePod 音量。Windows 的 0–100% 對應 HomePod 的 0 到音量上限；按鍵和 Windows 音量滑桿會改變 HomePod，在程式裡調整 HomePod 也會改變 Windows。連線時和變更音量上限時，兩邊對齊到較小的那個，哪一邊都不會自己變大。Windows 靜音時 HomePod 也靜音。
- **串流時控制 HomePod**：串流時這三個鍵只調整 HomePod（每次 2%），Windows 音量不變，螢幕下方會顯示 HomePod 音量（獨佔全螢幕的遊戲中不顯示）；未串流時照常調整 Windows。適合預設輸出是電腦喇叭、又只想聽 HomePod 的情況：把 Windows 設為靜音，再用按鍵調整 HomePod。
- **僅在 Windows 靜音時**：舊版的做法。只有 Windows 靜音或音量為 0 時按鍵才轉給 HomePod，Windows 保持靜音。
- **關**：按鍵只調整 Windows。

電腦沒有可用的輸出裝置時（沒有安裝音效卡驅動程式，或裝置被停用、拔除），「HomePod 跟隨 Windows 音量」和「僅在 Windows 靜音時」也會在串流時直接以按鍵控制 HomePod，直到裝置恢復。以系統管理員身分執行的程式在前景時，以及遙控器、滑鼠的驅動軟體只送出媒體命令而不是按鍵時，按鍵仍會調整 Windows；這種情況下「HomePod 跟隨 Windows 音量」會跟著變。

## 建置

需要 .NET 10 SDK。

```
dotnet build src -c Release          # 輸出到 app\
dotnet test tests\HomePodCast.Tests  # 通訊協定、音訊與翻譯單元測試
```

MSI 安裝程式由 `installer\build.ps1` 以 WiX Toolset v5（本機 dotnet 工具）建置，詳見 [installer/README.md](installer/README.md)。

`tools\measure_av.py` 可以從手機錄影算出影音差距；`tests\vectors\gen_vectors.py` 以 pyatv 所用的 srptools 產生配對演算法的對照資料。

介面文字以簡體中文寫在 `L.T("…")` / `L.F("…{0}", x)` 中，譯文放在 `src/i18n/<語言>.json`（每個檔案就是一種介面語言，以 `en.json` 為準），缺少任何一條或與 `en.json` 不一致時，單元測試就會失敗。瀏覽器擴充功能的文字在 `extension/_locales`。

## 分支與發佈

| 分支／標籤 | 用途 | 規則 |
| --- | --- | --- |
| `dev` | 日常開發 | 每次推送都會執行 CI 並產生預覽套件 |
| `main` | 在真正的 HomePod 上驗證過的程式碼 | 只接受來自 `dev` 的 PR，CI 必須通過 |
| `vX.Y.Z` 標籤 | 正式版本 | 標在 `main` 上，自動建置並發佈 GitHub Release |
| `release/x.y` | 為舊版本修補 | 需要時才從對應的標籤建立 |

CI 無法連到 HomePod，所以 `dev` 合併到 `main` 之前，要在實機上做一次影音同步測試。

## 程式碼簽章

目前發佈的程式還沒有數位簽章，第一次執行安裝程式或 exe 時 Windows SmartScreen 可能會攔截（按「其他資訊」→「仍要執行」）。所有發佈檔案都由 GitHub Actions 從本儲存庫的公開原始碼自動建置，請只從 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 頁面下載。

## 隱私權政策

除非使用者明確要求，HomePodCast 不會將任何資訊傳送到其他連網系統：它只和你在區域網路中選定的 AirPlay 揚聲器通訊，不收集、不上傳任何資料；本機狀態介面只監聽 127.0.0.1。以英文原文為準：[Privacy policy](README.md#privacy-policy)。

## 致謝

- [pyatv](https://github.com/postlund/pyatv)（MIT）：通訊協定流程的參考，配對演算法以其相依套件 srptools 為對照。
- [NAudio](https://github.com/naudio/NAudio)（MIT）：核對 Windows Core Audio 介面定義。
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash)（GPL-3.0）：低延遲送出封包與緩衝的思路。

## 授權條款

[GPL-3.0-or-later](LICENSE)。可以自由使用、修改和再散布；再散布修改版時，也必須以 GPL 開放原始碼。
