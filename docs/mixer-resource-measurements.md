# Mixer resource measurements

2026-10-26, P6b2. Reproduce with `dotnet run --project tests/HomePodCast.MixerBench -c Release -- 60 test-results/mixer-resources.json`.
The console host opens no GUI, audio endpoint, network connection or speaker. Eight fake process roots produce
distinct constant markers at 44.1 kHz stereo. Every destination reads and validates its selected markers. The
mixed cases also write/read a five-second timeline and run the real Windows AAC and MP3 encoders into fake consumers.
Each case warms up for 300 cycles, then measures at least 60 seconds. Effects are fixed: flat, night compression off.
PCM has one member and requested D=105 ms; mixed has two encoded consumers and D=2000 ms. This is a capture/mix/codec
capacity measurement, not an acoustic measurement or a measurement of transport threads. Controller A/B follows in P6b3.

| Destinations | Format | CPU cores | Allocated MiB/min | Peak private MiB | Private MiB after dispose | Threads before/after | Drops | p99/max scheduling lateness ms |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | --- |
| 1 | PCM | 0.0477 | 0.216 | 26.13 | 16.13 | 8/7 | 0 | 14.20/21.58 |
| 2 | PCM | 0.0477 | 0.207 | 23.61 | 21.82 | 7/7 | 0 | 14.09/15.06 |
| 4 | PCM | 0.0450 | 0.201 | 24.39 | 23.60 | 7/5 | 0 | 14.02/15.57 |
| 1 | AAC + MP3 | 0.0693 | 12.68 | 48.09 | 38.49 | 7/6 | 0 | 13.94/14.93 |
| 2 | AAC + MP3 | 0.1065 | 25.15 | 76.73 | 59.10 | 6/7 | 0 | 13.81/14.79 |
| 4 | AAC + MP3 | 0.1388 | 50.11 | 131.16 | 96.41 | 7/7 | 0 | 13.71/15.28 |

The 1→4 PCM slope is within this run's noise (about 0.003 CPU core); the codec slope is about 0.023 CPU core per
additional destination. Do not turn these into a device-independent CPU promise. The timer's approximately 14 ms p99
is console pacing noise, not measured RTP lateness. Source checks remain enabled throughout, and all input/bus leases
are zero after each disposal. Private bytes include retained CLR/codec heaps; they are not live managed buffer size.
The isolated warm-cycle regression checks that the capture/mix loop itself allocates no packet objects. Encoder
allocations are existing Media Foundation wrapper packets, separately visible above; the new hub does not remove them.

Raw CPU identity, nominal frequency, runtime, allocation, sample validation and per-case measurements accompany this
report in `docs/mixer-resource-measurements.json`. No real hardware playback has been verified.

## Single-controller A/B

P6b3: `dotnet run --project tests/HomePodCast.MixerBench -c Release -- --ab test-results/mixer-controller-ab.json`.
This runs the actual single-speaker `StreamController` loop, the existing `AirPlayClient` and `RtpSender`, an in-process
AirPlay receiver on loopback, and a fake endpoint producer. ABBA order, three-second warmup and at least 60 seconds per
pass. The receiver consumes and decrypts every packet. No GUI or Windows audio endpoint is opened. The fake receiver's
workers and packet/decryption allocations are part of the process measurements in both arms.

| Entry | CPU cores | Managed MiB | Target/cap frames at start and end | Extra capture ms | Skipped RTP packets | RTP arrival p99/max ms | Leases after stop |
| --- | ---: | ---: | --- | ---: | ---: | --- | ---: |
| Direct A1 | 0.6012 | 42.52 | 705/2557 → 705/2557 | 0 | 0 | 0.667/2.175 | 0 |
| Coordinator B1 | 0.5888 | 41.37 | 705/2557 → 705/2557 | 0 | 0 | 0.676/1.160 | 0 |
| Coordinator B2 | 0.5973 | 41.20 | 705/2557 → 705/2557 | 0 | 0 | 0.680/1.026 | 0 |
| Direct A2 | 0.5825 | 41.20 | 705/2557 → 705/2557 | 0 | 0 | 0.663/1.007 | 0 |

The mean difference is approximately 0.001 CPU core, smaller than the within-arm spread of 0.009–0.019 core. These
passes show no repeatable coordinator CPU or RTP-lateness regression, and buffers match exactly; they are not a
real-speaker latency or monitor timing measurement. All passes return to 20–21 threads before the fake receiver itself
is disposed. Private bytes settle at about 67–70 MiB with CLR/fake transport heaps retained. Raw measurements are in
`docs/mixer-controller-ab.json`. Deterministic frame/negotiation/RTP regressions separately catch an extra packet of
algorithmic buffering, including the explicit negative control.

The fake endpoint uses the same high-resolution timer and MMCSS scheduling as the existing audio threads. A coarse
Wait-based producer was rejected after it caused artificial FIFO underruns; no production FIFO or latency was raised.
The recorded run requires every decoded sample after warmup to equal the non-silent marker, and records that count.
