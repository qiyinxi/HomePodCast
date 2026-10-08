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

測試裝置：HomePod 第二代，HomePod 軟體 27.0（24J361）。

## 功能

- AirPlay 2 即時音訊：暫時性配對、全程加密，延遲可在 100–500 ms 間調整（以 1 ms 為單位）
- 系統匣程式：自動找到揚聲器、自動重新連線、睡眠喚醒後恢復，被其他裝置搶走時不會搶回來
- 混音器：每個程式各自的音量、靜音和音量表
- 影音同步測試：螢幕閃光加上「喀」一聲，一眼看出聲音比畫面晚多少
- 瀏覽器擴充功能（開發中）：把 YouTube、bilibili 的畫面延後，和 HomePod 的聲音對齊
- 介面支援英文、簡體中文、繁體中文和日文

## 使用方式

1. 安裝 [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0)。
2. 執行 `HomePodCast.exe`。第一次會要求新增防火牆規則（只允許區域網路、只在私人網路下生效）：HomePod 需要連回電腦進行對時和要求重傳。
3. 程式會自動找到 HomePod 並連線。把電腦的預設輸出裝置設為靜音也沒關係：擷取發生在系統音量之前，HomePod 照樣有聲音。

介面語言跟隨 Windows 顯示語言（Windows 不是中文或日文時顯示英文）。若要改用其他語言，在系統匣圖示上按右鍵 →「語言 / Language」，或把 `%APPDATA%\HomePodCast\config.json` 中的 `"Language"` 設為 `"zh-TW"`、`"zh-CN"`、`"en"`、`"ja"` 或 `"auto"`。

命令列（排查問題用）：

```
HomePodCast.exe scan                         列出區域網路中的 AirPlay 揚聲器
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     確認靜音後仍能擷取
```

## 建置

需要 .NET 10 SDK。

```
dotnet build src -c Release          # 輸出到 app\
dotnet test tests\HomePodCast.Tests  # 通訊協定、音訊與翻譯單元測試
```

`tools\measure_av.py` 可以從手機錄影算出影音差距；`tests\vectors\gen_vectors.py` 以 pyatv 所用的 srptools 產生配對演算法的對照資料。

介面文字以簡體中文寫在 `L.T("…")` / `L.F("…{0}", x)` 中，譯文放在 `src/i18n/{en,zh-TW,ja}.json`，缺少任何一條單元測試就會失敗。瀏覽器擴充功能的文字在 `extension/_locales`。

## 分支與發佈

| 分支／標籤 | 用途 | 規則 |
| --- | --- | --- |
| `dev` | 日常開發 | 每次推送都會執行 CI 並產生預覽套件 |
| `main` | 在真正的 HomePod 上驗證過的程式碼 | 只接受來自 `dev` 的 PR，CI 必須通過 |
| `vX.Y.Z` 標籤 | 正式版本 | 標在 `main` 上，自動建置並發佈 GitHub Release |
| `release/x.y` | 為舊版本修補 | 需要時才從對應的標籤建立 |

CI 無法連到 HomePod，所以 `dev` 合併到 `main` 之前，要在實機上做一次影音同步測試。

## 程式碼簽章政策

發佈的 Windows 程式預計使用 [SignPath.io](https://about.signpath.io/) 提供的免費開源程式碼簽章，憑證由 [SignPath Foundation](https://signpath.org/) 簽發。

- 只簽署由本儲存庫公開原始碼、經 GitHub Actions 自動建置出來的檔案。
- 提交者、審核者與核准者：儲存庫維護者（[@qiyinxi](https://github.com/qiyinxi)）。

以英文原文為準：[Code signing policy](README.md#code-signing-policy)。

## 隱私權政策

除非使用者明確要求，HomePodCast 不會將任何資訊傳送到其他連網系統：它只和你在區域網路中選定的 AirPlay 揚聲器通訊，不收集、不上傳任何資料；本機狀態介面只監聽 127.0.0.1。以英文原文為準：[Privacy policy](README.md#privacy-policy)。

## 致謝

- [pyatv](https://github.com/postlund/pyatv)（MIT）：通訊協定流程的參考，配對演算法以其相依套件 srptools 為對照。
- [NAudio](https://github.com/naudio/NAudio)（MIT）：核對 Windows Core Audio 介面定義。
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash)（GPL-3.0）：低延遲送出封包與緩衝的思路。

## 授權條款

[GPL-3.0-or-later](LICENSE)。可以自由使用、修改和再散布；再散布修改版時，也必須以 GPL 開放原始碼。
