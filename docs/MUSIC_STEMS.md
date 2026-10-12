# Music separation / 音乐分轨（实验性）

## English

Mixer → Music separation separates the captured music into estimated vocals and accompaniment locally. Mix them independently for HomePod and a local headphone output. For singing practice, set HomePod vocals 0 / accompaniment 100 and local vocals 100 / accompaniment 0. The microphone is mixed after separation and does not inherit the separation wait.

Extract the optional `HomePodCast-stems-<version>.zip` CI/release attachment and select its folder. The core app carries no ONNX dependency or model, never downloads it automatically, and starts no worker while separation is off. The module uses the same .NET 10 Runtime. Enable switches are session-only; disconnecting ends separation.

Choose an output that is not captured. Monitoring pauses if it would feed back into capture. Windows still plays the original: use existing per-app HomePod-only routing or a dedicated capture device. Separation applies to the captured mix, so use per-app routing to isolate a music app if required.

The MIT-licensed StemgenRT 0.7.0 vocal branch estimates vocals; accompaniment is the original minus that estimate. Processing is local without audio recording/upload. Model and runtime notices ship in the optional module. Stems may leak instruments, vocals or reverberation and are not promised to work for arbitrary dialogue/game audio.

The fixed 8192-frame wait adds about 185.8 ms to normal HomePod latency, including the model's previous-hop alignment. Video compensation includes it; calibrate with separation off. Local stems additionally wait the negotiated AirPlay delay to approximate speaker alignment; actual headphone and speaker delays still need listening tests. Late model output mutes unequal-gain stem mixes instead of sending unseparated audio. Counters show missed frames and dropped work. Equal stem gains reconstruct the delayed original directly.

Build with the commands above. Downloads are pinned and hash-checked; pruning preserves the independent vocal weights. CI compares eight real ONNX cases against the publisher's independent native PyTorch reference within 1e-5, then benchmarks non-silent inference. The benchmark is not an end-to-end latency or separation quality measurement.

Model source and license: [StemgenRT](https://github.com/sweetspotsoundsystem/stemgen-rt/tree/85e715f1a1982b29204e11595d6fa3f0bde9810a). Runtime: [ONNX Runtime](https://github.com/microsoft/onnxruntime/tree/v1.26.0).

## 简体中文

混音器 → 音乐分轨：在电脑上实时分离已采集音乐的人声和伴奏，分别调整 HomePod 与耳机的混合比例。例如，HomePod 人声 0 / 伴奏 100，本机人声 100 / 伴奏 0，用耳机听原唱练唱。麦克风在分轨之后混入，不会被分离，也不会增加分轨的 186 ms 等待。

安装 CI 或发布附件中的 `HomePodCast-stems-<version>.zip`，解压到自己选择的文件夹，然后点击「选择分轨模块」。主程序不包含 ONNX Runtime 或模型；关闭分轨时不会启动模块，不会自动下载。模块需要 .NET 10 Runtime（HomePodCast 已需要它）。开启分轨及本机播放的开关不保存；断开音箱后分轨会关闭，重连后手动开启。

选择未被采集的耳机作为监听设备。监听设备与采集设备相同会暂停监听，避免循环采集。Windows 原始播放仍然存在，可以将音乐应用设为只送 HomePod，或把音乐放在独立采集设备上。分轨针对所有送入 HomePod 的系统音频；需要只处理某个应用时，使用现有按程序分流。

模型为 StemgenRT 0.7.0（MIT），只保留独立的人声分支，伴奏为原音减去人声估计。模型、运行库和许可证在独立模块内。音频在本机处理，不保存、不上传。安装构建才访问 GitHub 下载固定版本的模型和许可证。

分轨不是完美消除人声：器乐、人声、混响可能互相串音，特别是模型未覆盖的音乐或对白。所有声音都延迟 8192 帧（约 185.8 ms）再播放，模型自身的 128 帧对齐包含在这个固定等待中；额外延迟与 HomePod 原有延迟相加。视频 API、浏览器和播放器补偿已包含此等待。校准应在分轨关闭时进行，避免把模块等待重复保存进校准值。

本机分轨额外等待当前 AirPlay 播放延迟后送往耳机，尽量与音箱同步；真实耳机驱动和 HomePod DSP 延迟仍可能使它们不同步，需实机试听。慢电脑的推理超过播放期限时，界面显示未完成帧和丢弃块；需要分离的输出静音，不会悄悄变回带原唱的完整音频。人声和伴奏相同增益时可直接重构原音。

开发构建：

```powershell
python -m pip install onnx==1.20.1
./tools/build-stem-module.ps1
$env:HOMEPODCAST_STEM_MODULE = (Resolve-Path publish/stem-module).Path
dotnet test tests/HomePodCast.Tests --no-restore --filter FullyQualifiedName~StemModuleTests
```

脚本校验完整模型 SHA-256，裁出人声图，再校验裁剪后 SHA-256；真实 ONNX 输出与发布者的独立 PyTorch 参考比较（8 组，含短片段、部分尾块、重置与 EOF flush，误差 ≤ 1e-5），随后运行非静音性能测试。基准反映此电脑的推理速度，不代表音乐质量或 HomePod 的端到端延迟。

