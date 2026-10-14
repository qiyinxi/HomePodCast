# HomePodCast

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [日本語](README.ja.md)

把 Windows 電腦的聲音即時串流到 HomePod（AirPlay 2），專為打電動和看影片打造的低延遲方案。

TuneBlade 在 HomePod 軟體 27 上已經發不出聲音；HomePodCast 是從零開始實作的原生 C# 版本，單一 exe 約 350 KB，不需要虛擬音效卡。

## 支援的接收端——實驗性

第三方接收端主要用於聽音樂。AirPlay 1 相容模式與第三方 AirPlay 2 均為實驗性；第三方 AirPlay 2 **尚未在實機測試**。HomePod 原有的暫時配對、NTP、即時 PCM 與情境設定維持原有行為。

| 接收端類型 | 連線方式 | 驗證情況 |
| --- | --- | --- |
| HomePod | 原有 AirPlay 2 低延遲 | 一台 HomePod 第二代實機 |
| Apple TV HD / 4K | PIN 配對後的 AirPlay 2 低延遲 | 模擬接收端；未實機驗證 |
| 較舊的 Apple AirPlay 接收端 | AirPlay 1 相容 | CI 的 shairport-sync；未實機驗證 |
| 第三方 AirPlay 2 | 低延遲 → 已廣播的 AirPlay 1 → 緩衝；記住成功方式 | CI 的 shairport-sync AirPlay 2 + nqptp；未實機驗證 |
| 第三方 AirPlay 1 | AirPlay 1 相容 | CI 的經典 shairport-sync |
| 未知接收端 | 低延遲 → 緩衝 → AirPlay 1，記錄每次拒絕 | 僅模擬與軟體接收端 |

| 模式 | 建議最低延遲 | 預設 | 可選範圍 |
| --- | --- | --- | --- |
| HomePod 低延遲 | 一般處理時間 85 ms 時為 105 ms | 120 ms；遊戲 105 ms | 原有處理時間 + 15 … 500 ms |
| Apple TV 低延遲 | 接收端處理時間 + 30 ms | 200 ms | 處理時間 + 15 … 500 ms |
| 第三方低延遲 | max(150 ms，處理時間 + 50 ms) | 250 ms | max(120 ms，處理時間 + 30 ms) … 500 ms |
| 第三方緩衝 | 1.0 秒 | 2.0 秒 | 1.0 / 1.5 / 2.0 秒 |
| AirPlay 1 相容 | 1.0 秒；AirPort Express 為 2.0 秒 | 2.0 秒 | 1.0 / 1.5 / 2.0 秒 |

第三方裝置未回報處理時間時以 150 ms 估算，介面最低提供 200 ms。低於建議值會提示可能斷音。接收端可能忽略要求的延遲；預設值僅為經軟體測試的起點。

設定 → 音箱資訊可按裝置指定連線方式。相容模式使用自己的延遲選擇器取代 HomePod 情境按鈕，不覆寫 HomePod 情境；設定中會顯示實際協定與回報的處理時間。首次相容連線會提示約兩秒延遲。

相容模式適合音樂，不適合遊戲。瀏覽器擴充功能的畫面延遲上限仍為 **2000 ms**，聲音延遲更大時無法完全對齊；影音同步測試仍可校準實際偏移。第三方接收端無法加入多音箱群組。

不支援 FairPlay 或僅接受 MFi 認證傳送端的裝置。極米 Z6X 僅經握手檢查，音訊會話要求 FairPlay，因此**不支援**，並非已驗證可播放的裝置；不可依模擬的 Apple TV 型號判斷相容性。其他裝置回報請附上 `HomePodCast.exe scan --txt`、選擇的模式、實際連線延遲與紀錄中被拒絕的請求。

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
- **無線影院**（每個聲道一台 HomePod）：只用模擬揚聲器和產生的檔案測試過
- 實體音效卡和耳機、其他 Windows 版本，以及電腦走有線或無線網路的差別

如果你在其他裝置上試用，歡迎開 issue 回報：裝置型號、軟體版本、能穩定不斷音的最低延遲設定，以及記錄檔（`homepodcast.log`：「設定 → 工具 → 記錄檔 → 開啟記錄檔資料夾」就能看到；它在 `%APPDATA%\HomePodCast`，Microsoft Store 版在 `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`）。

## 功能

- AirPlay 2 即時音訊：暫時性配對、全程加密，延遲可在 100–500 ms 間調整（以 1 ms 為單位）
- 單一視窗介面：首頁、混音器、麥克風與音效、設定；跟隨 Windows 淺色／深色模式
- 場景模式：推薦（120 ms，預設）、遊戲（105 ms）、音樂（500 ms，最穩）、影視（200 ms，搭配瀏覽器擴充功能和播放器的音訊延遲設定）、自訂
- 系統匣程式：自動找到揚聲器、自動重新連線、睡眠喚醒後恢復，被其他裝置搶走時不會搶回來
- 不會悄悄擠掉其他裝置：iPhone 或 Mac 正在串流到 HomePod 時，按「連線」會先詢問，自動連線會等揚聲器閒置後再連（套用切換到這台揚聲器的使用設定檔時也一樣）
- 夜間自動中斷連線（實驗性）：關螢幕不關機時，在設定的時段裡，鍵盤滑鼠和聲音都閒置一段時間（5 到 120 分鐘）後中斷 HomePod 的連線，讓其他裝置可以使用；時段結束時像啟動時自動連線一樣連回去，不會打斷正在播放的裝置
- Apple TV 家庭劇院：HomePod 設為 Apple TV 的揚聲器時（Apple TV 閒置時也一直連著它），HomePodCast 連線期間會暫時接管它，不需要配對碼；中斷連線後 Apple TV 下次播放時會重新連上它。也可以在「設定」裡改成先詢問（實驗性，尚未在實際的 Apple TV 上驗證）
- 在系統匣圖示上按右鍵開啟快速面板：音量和靜音、場景、夜間模式、麥克風、連線或中斷連線
- 混音器：每個程式各自的音量、靜音和音量表，可選擇依程式分流（HomePod／本機／兩者）或依音效卡分流（選擇擷取哪個輸出裝置），見下文
- 音量上限、夜間模式（壓縮動態範圍＋減弱低音）、全域快速鍵；鍵盤音量鍵也能調整 HomePod（跟隨 Windows 音量，或串流時只調整 HomePod），見下文
- 麥克風送到 HomePod，附殘響和等化器，可用耳機低延遲監聽；串流到揚聲器的所有聲音可選等化器預設集
- 立體聲組合和多房間同步（實驗性，尚未在實際裝置上驗證）
- 無線影院（實驗性，尚未在實際裝置上驗證）：本機 5.1 或 7.1 檔案的音軌，每個聲道送到一台 HomePod，最多七台再加一台播放低音聲道；沒有分配揚聲器的聲道混入相鄰的揚聲器，還可以用麥克風測量各揚聲器的延遲（測量時音量不超過 25%）。見[無線影院（英文／簡體中文）](docs/WIRELESS_CINEMA.md)
- Apple TV：第一次按「連線」時在電視上配對一次（輸入電視顯示的 4 位數字）；它的家庭劇院揚聲器是 HomePod 時由 HomePod 出聲（實驗性，尚未在實際的 Apple TV 上驗證）
- 影音同步測試：螢幕閃光加上「喀」一聲，一眼看出聲音比畫面晚多少
- 設定中的揚聲器資訊：型號、系統和 AirPlay 版本、IP 位址、Wi‑Fi MAC 位址（在路由器的裝置清單裡依它找到 HomePod，看它連在哪個頻段或替它指定固定 IP）、藍牙位址、裝置 ID
- 瀏覽器擴充功能（測試版）：把 YouTube、bilibili 的畫面延後，和 HomePod 的聲音對齊。「音樂」場景的 500 ms 是給聽音樂用的，看影片用「影視」或「推薦」最流暢（畫面延遲超過 300 ms 時擴充功能的彈出視窗會提示）
- 同步觀影視窗（實驗性）：給沒有音訊延遲設定的播放器使用。獨立模組把你選的視窗（或整個螢幕）依 HomePod 的聲音延遲延後顯示，可以全螢幕、可以微調，畫面全程在顯示卡上處理。它是選用的 `HomePodCast-video-window-<版本>.zip`，Microsoft Store 版沒有。見[同步觀影視窗（英文／簡體中文）](docs/VIDEO_WINDOW.md)
- 檢查更新（預設不自動檢查，見[更新](#更新)）；MSI 安裝的版本驗證 SHA-256 後一鍵更新
- 介面支援英文、簡體中文、繁體中文和日文

整套使用設定檔、依音箱與音源儲存的實測校準、程式的 HomePod 獨立音量、去識別化診斷報告，以及瀏覽器「同步全螢幕」和 ±5／±20 ms 微調，詳見[使用設定檔、校準與診斷（英文／簡體中文）](docs/PLAYBACK_FEATURES.md)。瀏覽器擴充功能的網站偏移是加在校準之上的：如果之前已經依網站補償過，校準後請把這些網站偏移重設。

## 使用方式

1. 安裝 [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）。
2. 從 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 下載 HomePodCast，有兩種套件，另有一個選用模組：
   - **安裝程式（建議）`HomePodCast-<版本>-x64.msi`**：可以選擇安裝位置（預設 `C:\Program Files\HomePodCast`），會自動新增開始功能表捷徑和所需的防火牆規則。升級時直接執行新版 MSI；解除安裝請到「設定 → 應用程式」，如果開啟過「開機時自動啟動」，請先在程式裡關閉。
   - **可攜版 `HomePodCast-<版本>-win-x64.zip`**：解壓縮到任意位置，執行 `HomePodCast.exe`，第一次會要求新增防火牆規則。
   - **選用模組 `HomePodCast-video-window-<版本>.zip`**：同步觀影視窗（見「功能」）。解壓縮到任意位置，在「設定 → 工具」裡選擇這個資料夾。更新程式時它不會跟著更新，請重新下載相同版本的模組。

   防火牆規則只允許區域網路、只在私人網路下生效：HomePod 需要連回電腦進行對時和要求重傳。如果 Windows 把你的網路設成公用網路（例如從 Wi-Fi 換成網路線之後），規則就不會生效：HomePodCast 會提示，並可以開啟 Windows 設定，在那裡把「網路設定檔類型」改成「私人網路」（只對自己信任的家用網路這樣做）。兩種套件的設定和記錄檔都儲存在 `%APPDATA%\HomePodCast`。Microsoft Store 版寫入的檔案放在它自己的儲存空間：`%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`，解除安裝時由 Windows 刪除；不論哪種版本，「設定 → 工具 → 記錄檔 → 開啟記錄檔資料夾」開啟的都是正確的資料夾。
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

## 更新

HomePodCast 可以到本儲存庫的 GitHub Releases 尋找新版本：「設定 → 軟體更新 → 檢查更新」。這是它唯一一個發往區域網路以外的請求，而且只在你要求時才會送出：**自動檢查預設關閉**。開啟「自動檢查更新」後，會在啟動約 30 秒後及之後每 24 小時檢查一次（開發版本從不自動檢查）。請求只是一次 HTTPS GET（`api.github.com/repos/qiyinxi/HomePodCast/releases`），User-Agent 為 `HomePodCast/<版本>`：不需要帳號，不傳送任何電腦或揚聲器的資訊。每次請求都會寫進記錄檔。

- **更新通道**：正式版只提示正式版本；測試版也會提示測試版本，哪個新提示哪個。正在使用的測試版比最新正式版還新時，會等到下一個正式版（不會降級）。
- 自動檢查發現新版本時，系統匣會跳出一次通知，首頁會出現帶有「更新」「查看說明」「略過此版本」的提示列；手動檢查的結果顯示在設定裡。
- **不按就不會安裝。** 用 MSI 安裝的，按「更新」會先確認，然後下載新的 MSI，用 GitHub 為這個檔案提供的 SHA-256 驗證（不一致或 GitHub 沒有提供 SHA-256 時，檔案會被刪除，什麼都不裝）。接著 HomePodCast 結束（串流中斷約一分鐘）並執行安裝程式，Windows 會要求系統管理員權限。裝好後 HomePodCast 自動重新啟動並提示已更新到哪個版本；如果拒絕了授權，就重新啟動舊版本。
- 可攜版（zip）不會自行更新：「更新」會開啟發布頁。
- Microsoft Store 版由 Store 負責更新，本身從不連線到 GitHub。

## 常見問題

### 偶爾卡一下怎麼辦

**為什麼會卡**：聲音要經過兩段無線，電腦 → 路由器 → HomePod。HomePod（第二代）只有 Wi‑Fi 4（802.11n），有人走過、抬一下手、牆和櫃門都會擋住訊號，偶爾就有封包晚到或遺失。延遲設 120 ms（「推薦」）時，HomePod 自己要用約 85 ms 處理，留給網路抖動和封包重送的只有約 35 ms，一次超過這個餘裕的抖動就是一下卡頓。

**依這個順序試**：

1. 電腦能接網路線就接網路線：兩段無線變成一段。
2. Windows 的無線網路卡省電：控制台 → 電源選項 → 變更計劃設定 → 變更進階電源設定 →「無線介面卡設定 → 省電模式」，使用電池和一般電源都設為「最高效能」。
3. 裝置管理員 → 網路介面卡 → 無線網路卡 → 內容 →「進階」：「漫遊積極性」（Roaming Aggressiveness）設為最低；如果有，再把「MIMO 省電模式」（MIMO Power Save Mode）設為「無 SMPS」（No SMPS），「封包聯合」（Packet Coalescing）設為停用。名稱依網路卡而不同。
4. HomePod 的頻段：HomePod（第二代）能用 2.4G 和 5G，路由器把兩個頻段合成一個名稱（雙頻合一、Smart Connect）時，它可能被分到 2.4G，而附近 Wi‑Fi 多的地方 2.4G 很擁擠。給 5G 另取一個名稱（關掉雙頻合一），讓 iPhone 連上它，再在「家庭」App 裡把 HomePod 移到這個網路。2026-10-10 在一棟牆薄、鄰居 Wi‑Fi 很多的大樓裡實測，HomePod 離路由器 50 cm：2.4G 上 ping 中位數 21 ms、p99 約 110 ms、遺失 4%，延遲設到 500 ms 也會卡；換到 5G 後 2 ms、最大 12 ms、完全不遺失。這一點比擺放位置更有效。
5. HomePod 的位置：放在「看得見」路由器的地方，不要放進櫃子裡。
6. 把延遲加 10–15 ms：在「場景」中選「自訂」調整，或在首頁出現提示時直接按「套用」。首頁「串流狀態」下方會顯示最近 10 分鐘的網路抖動次數；抖動真的吃掉餘裕時，提示會說明是哪一段 Wi‑Fi 的問題（電腦接網路線時一定是 HomePod 那一段），並給出建議的延遲。程式不會自己改延遲。「斷音」一格算的是這次連線中的斷音：電腦這邊實測的，加上吃掉餘裕的網路抖動（HomePod 很可能因此斷了一下）。HomePod 從不回報晚到的封包，所以後者是估計，數字前會加「≈」；滑鼠停在這一格上可以看到兩部分各是多少。

**如何看記錄檔**（`%APPDATA%\HomePodCast\homepodcast.log`，Microsoft Store 版是 `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast\homepodcast.log`；「設定 → 工具 → 記錄檔 → 開啟記錄檔資料夾」可以直接開啟）：串流時每 100 ms ping 一次 HomePod 和路由器，往返超過 30 ms 或沒有回應時記一行 `network: ping to the speaker took 48 ms`（路由器是 `the router`，沒有回應是 `lost`）。每分鐘的 `stats:` 行裡有 `rtx=已重送/要求`、`rtxMiss=`（來不及重送的封包）和 `ping=中位數/p99/最大 lost=遺失/總數 router=…`。

- 只有揚聲器的 ping 突增、路由器正常：問題在路由器 → HomePod 這一段，見第 4、5 點。
- 揚聲器和路由器同時突增：問題在電腦這一段，見第 1–3 點。電腦接網路線時問題仍在 HomePod 那一段（只是路由器回自己的 ping 慢了），提示也會這樣說。
- 只有路由器慢、揚聲器正常：路由器常把送給自己的 ping 放到最後處理，不影響聲音。

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
| `main` | 在真正的 HomePod 上驗證過的程式碼 | 只接受來自 `dev` 或 `release/X.Y.Z` 分支的 PR，CI 必須通過 |
| `vX.Y.Z` 標籤 | 正式版本 | 標在 `main` 上，自動建置並發佈 GitHub Release |
| `release/x.y` | 為舊版本修補 | 需要時才從對應的標籤建立 |

CI 無法連到 HomePod，所以 `dev` 合併到 `main` 之前，要在實機上做一次影音同步測試。

發佈流程（測試版、測試過的測試版轉正、CI 發佈前檢查什麼）見 [docs/RELEASING.md](docs/RELEASING.md)（英文與簡體中文）。

## 程式碼簽章

目前發佈的程式還沒有數位簽章，第一次執行安裝程式或 exe 時 Windows SmartScreen 可能會攔截（按「其他資訊」→「仍要執行」）。所有發佈檔案都由 GitHub Actions 從本儲存庫的公開原始碼自動建置，請只從 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 頁面下載。

## 隱私權政策

除非使用者明確要求，HomePodCast 不會將任何資訊傳送到其他連網系統：它只和你在區域網路中選定的 AirPlay 揚聲器通訊，不收集、不上傳任何資料；本機狀態介面只監聽 127.0.0.1。唯一發往區域網路以外的請求是檢查更新（見[更新](#更新)），只在你按「檢查更新」或開啟自動檢查後才會送出。以英文原文為準：[Privacy policy](README.md#privacy-policy)。

## 致謝

- [pyatv](https://github.com/postlund/pyatv)（MIT）：通訊協定流程的參考，配對演算法以其相依套件 srptools 為對照。
- [NAudio](https://github.com/naudio/NAudio)（MIT）：核對 Windows Core Audio 介面定義。
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash)（GPL-3.0）：低延遲送出封包與緩衝的思路。
- [TweetNaCl](https://tweetnacl.cr.yp.to/)（公有領域）：Ed25519 和 X25519 程式碼，移植到 C# 用於 Apple TV 配對。

## 授權條款

[GPL-3.0-or-later](LICENSE)。可以自由使用、修改和再散布；再散布修改版時，也必須以 GPL 開放原始碼。
