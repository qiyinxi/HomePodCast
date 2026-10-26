# Music separation / 音乐分轨（实验性）

[English](#english) | [简体中文](#简体中文)

## English

Mixer → **Music separation (experimental)** splits the captured music into estimated vocals and accompaniment on this PC, and mixes them separately for the HomePod and for a local output such as headphones. For singing practice, for example: HomePod vocals 0 / accompaniment 100, local vocals 100 / accompaniment 0. The microphone is mixed in after separation: it is not separated and doesn't wait for it.

It is not in the Microsoft Store version, which can't start a separately downloaded program.

### Installing

1. Download `HomePodCast-stems-<version>.zip` from the same release as your HomePodCast (or from the `stem-module` artifact of a CI build), the same version as the app.
2. Extract it to a folder of your choice, then in Mixer click **Select stem module** and choose that folder.

The app itself contains no ONNX Runtime and no model, never downloads them, and starts no module while separation is off. The module uses the .NET 10 Runtime that HomePodCast already needs. The switches (separation, local playback) are not saved: separation ends when the speaker disconnects, and a tray notice says so; turn it on again in Mixer after reconnecting.

### Local playback

Choose headphones or another output that is not being captured; local playback pauses on the captured device, since its sound would go to the HomePod again. Windows still plays the original: route the music app to the HomePod only (per-app routing), or put the music on its own capture device. Separation works on everything sent to the HomePod; to separate only one app, use per-app routing.

### Latency and what you hear

- Everything sent to the HomePod waits a fixed 8192 frames (about 186 ms) more, the model's own 128-frame hop included. Home, the browser extension and the players add it to the video delay by themselves. Calibrate with separation off: the A/V sync test refuses to run while it is on, so the wait is never counted twice.
- Local stems also wait the current AirPlay playout delay, to be heard about when the HomePod plays them. The real delays of the headphones and of the HomePod can still differ: listen and compare.
- Turning separation on fades the live sound out over 10 ms, leaves about 186 ms of silence while the wait fills, and fades the separated sound in; nothing is heard twice. Turning it off fades the separated sound out and the live sound in over 10 ms each; the 186 ms that were still waiting are skipped.
- Slider moves are smoothed (10 ms from 0 to 100 %). When the model falls behind, a mix that needs its results fades out over 5 ms before they go missing, stays silent, and fades back in when they return: it never falls back to the original with its vocals. Equal vocal and accompaniment levels need no model and give back the original. Mixer counts the frames that weren't ready in time and the blocks that were dropped (not while the model is still loading).
- If the module fails (it can't start, its model is damaged or of another version, it stops answering, it exits), separation turns off, the HomePod plays the original again, a tray notice says why, and Mixer keeps saying it; the module's own words are in the log.

### Quality

Separation is not perfect: instruments, vocals and reverb can leak into either stem, especially with music the model wasn't made for, or with dialogue and game sound. Processing is local; no audio is recorded or uploaded.

### Model and licenses

The vocal branch of [StemgenRT](https://github.com/sweetspotsoundsystem/stemgen-rt/tree/85e715f1a1982b29204e11595d6fa3f0bde9810a) 0.7.0 (MIT) estimates the vocals, with its weights unchanged; the accompaniment is the original minus that estimate. The runtime is [ONNX Runtime](https://github.com/microsoft/onnxruntime/tree/v1.26.0) 1.26.0 (MIT). The module carries both licenses, their notices and HomePodCast's own license (GPL-3.0-or-later).

Training data: StemgenRT's README and model card don't name it. The vocal model's training configurations in its training repository ([StemgenRT-5.8](https://github.com/sweetspotsoundsystem/StemgenRT-5.8), `configs/vocals-independent.json`) sample MUSDB18-HQ (academic use only) and MoisesDB (CC BY-NC-SA 4.0). Upstream states no terms for the weights beyond the MIT license; whether the datasets' non-commercial terms reach the weights is not settled.

### Building

```powershell
./tools/build-stem-module.ps1 -InstallOnnx
$env:HOMEPODCAST_STEM_MODULE = (Resolve-Path publish/stem-module).Path
dotnet test tests/HomePodCast.Tests --filter FullyQualifiedName~StemModuleTests
```

The script takes the upstream files pinned in `modules/StemWorker/upstream.json` (one commit, SHA-256s), keeps them in `.stem-cache` and checks them on every use, cuts the vocal branch out of the model (Python with onnx 1.20.1; `-InstallOnnx` installs it) and checks the result's SHA-256. Then it compares eight cases of the real ONNX output (short clips, partial last blocks, a reset, the end-of-file flush) with the publisher's independent PyTorch reference, within 1e-5, and runs a benchmark on non-silent input. The benchmark shows this PC's inference speed, not the music quality or the HomePod's end-to-end latency. CI does the same in the job `stem-module`, with the upstream files cached (`docs/RELEASING.md`).

## 简体中文

混音器 →「**音乐分轨（实验性）**」在这台电脑上把采集到的音乐分离成估计的人声和伴奏，分别给 HomePod 和耳机等本机输出调混合比例。比如练唱：HomePod 人声 0 / 伴奏 100，本机人声 100 / 伴奏 0。麦克风在分轨之后混入：不会被分离，也不用等分轨。

Microsoft Store 版没有这个功能：商店版不能启动另外下载的程序。

### 安装

1. 从和你的 HomePodCast 同一个发布（或 CI 构建的 `stem-module` 产物）下载 `HomePodCast-stems-<版本>.zip`，版本要和程序一致。
2. 解压到自己选的文件夹，在混音器里点「选择分轨模块」，选这个文件夹。

程序本身不含 ONNX Runtime 和模型，从不自动下载；分轨关闭时不启动模块。模块用 HomePodCast 已经需要的 .NET 10 Runtime。开关（开启分轨、本机播放）不保存：音箱断开时分轨随之关闭，托盘会提示；重新连接后在混音器里再次开启。

### 本机播放

选择耳机等没有被采集的输出设备；选到正被采集的设备时本机播放会暂停，不然它的声音会再送到 HomePod。Windows 仍在播放原始声音：可以把音乐程序设为只送 HomePod（按程序分流），或者把音乐放在独立的采集设备上。分轨处理所有送到 HomePod 的声音；只想处理某个程序时，用按程序分流。

### 延迟和听到的效果

- 送到 HomePod 的所有声音都多等固定的 8192 帧（约 186 ms），模型自己 128 帧的对齐也在其中。首页、浏览器插件和播放器会自动把它算进画面延迟。校准时关掉分轨：分轨开着时音画同步测试不会运行，等待时间不会被算两次。
- 本机分轨还会多等当前的 AirPlay 播放延迟，尽量和 HomePod 同时听到。耳机和 HomePod 的实际延迟仍可能不同：请试听比较。
- 开启分轨时，实时声音在 10 ms 内淡出，等待填满时有约 186 ms 静音，然后分轨的声音淡入；不会有哪一段听两遍。关闭时，分轨的声音和实时声音各用 10 ms 淡出、淡入；还在等待中的 186 ms 会被跳过。
- 拖动滑块是平滑的（从 0 到 100% 用 10 ms）。模型跟不上时，需要分离结果的混音会在结果缺失之前用 5 ms 淡出，保持静音，结果恢复后再淡入：绝不会退回带人声的原声。人声和伴奏音量相同时不需要模型，就是原声。混音器会统计没有及时完成的帧和被丢弃的块（模型还在加载时不算）。
- 模块出错时（启动不了、模型损坏或版本不对、不再响应、退出），分轨会自动关闭，HomePod 恢复播放原声，托盘提示原因，混音器里也会一直显示；模块自己的说明在日志里。

### 音质

分离并不完美：乐器、人声和混响可能互相串音，特别是模型没有覆盖的音乐、对白和游戏声音。处理都在本机进行，不录音、不上传。

### 模型和许可证

人声由 [StemgenRT](https://github.com/sweetspotsoundsystem/stemgen-rt/tree/85e715f1a1982b29204e11595d6fa3f0bde9810a) 0.7.0（MIT）的人声分支估计，权重不做任何改动；伴奏是原声减去这个估计。运行库是 [ONNX Runtime](https://github.com/microsoft/onnxruntime/tree/v1.26.0) 1.26.0（MIT）。模块里带着这两个许可证、它们的声明和 HomePodCast 自己的许可证（GPL-3.0-or-later）。

训练数据：StemgenRT 的 README 和模型说明没有写。它的训练仓库（[StemgenRT-5.8](https://github.com/sweetspotsoundsystem/StemgenRT-5.8)，`configs/vocals-independent.json`）里人声模型的训练配置使用 MUSDB18-HQ（仅限学术用途）和 MoisesDB（CC BY-NC-SA 4.0）。上游对权重没有 MIT 许可证以外的说明；数据集的非商业条款是否延伸到权重，目前没有定论。

### 构建

```powershell
./tools/build-stem-module.ps1 -InstallOnnx
$env:HOMEPODCAST_STEM_MODULE = (Resolve-Path publish/stem-module).Path
dotnet test tests/HomePodCast.Tests --filter FullyQualifiedName~StemModuleTests
```

脚本使用 `modules/StemWorker/upstream.json` 里固定的上游文件（一个提交和各文件的 SHA-256），保存在 `.stem-cache` 里，每次使用都校验；从模型里裁出人声分支（需要装有 onnx 1.20.1 的 Python，`-InstallOnnx` 会安装它），并校验结果的 SHA-256。然后把真实 ONNX 输出和发布者独立的 PyTorch 参考结果比较（8 组：短片段、不完整的末块、重置和文件结尾的冲刷，误差不超过 1e-5），再用非静音输入跑性能测试。性能测试只反映这台电脑的推理速度，不代表音乐质量或 HomePod 的端到端延迟。CI 的 `stem-module` 作业做同样的事，上游文件有缓存（`docs/RELEASING.md`）。
