# Mixer resource measurements

2026-10-26, P6b2 implementation with the final P6c boundary changes. Reproduce with `dotnet run --project tests/HomePodCast.MixerBench -c Release -- 60 test-results/mixer-resources.json`.
The console host opens no GUI, audio endpoint, network connection or speaker. Eight fake process roots produce
distinct constant markers at 44.1 kHz stereo. Every destination reads and validates its selected markers. The
mixed cases also write/read a five-second timeline and run the real Windows AAC and MP3 encoders into fake consumers.
Each case warms up for 300 cycles, then measures at least 60 seconds. Effects are fixed: flat, night compression off.
PCM has one member and requested D=105 ms; mixed has two encoded consumers and D=2000 ms. This is a capture/mix/codec
capacity measurement, not an acoustic measurement or a measurement of transport threads. Controller A/B follows in P6b3.

| Destinations | Format | CPU cores | Allocated MiB/min | Peak private MiB | Private MiB after dispose | Threads before/after | Drops | p99/max scheduling lateness ms |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | --- |
| 1 | PCM | 0.0641 | 0.216 | 26.54 | 12.95 | 8/7 | 0 | 0.57/23.08 |
| 2 | PCM | 0.0565 | 0.207 | 17.66 | 16.20 | 7/7 | 0 | 0.47/1.37 |
| 4 | PCM | 0.0557 | 0.202 | 19.99 | 19.16 | 8/5 | 0 | 0.66/1.06 |
| 1 | AAC + MP3 | 0.1062 | 12.685 | 44.59 | 34.11 | 7/6 | 0 | 0.90/1.54 |
| 2 | AAC + MP3 | 0.1167 | 25.153 | 70.27 | 50.48 | 6/7 | 0 | 17.60/174.88 |
| 4 | AAC + MP3 | 0.1935 | 50.107 | 111.71 | 67.35 | 7/7 | 0 | 0.96/4.80 |

The final P6c run includes per-bus clock steering. PCM differences are within run-to-run scheduling noise;
codec work and packet allocations grow with destination count. The 1-to-4 endpoint CPU slope is
-0.0028 core per extra PCM destination and +0.0291 core per extra encoded destination.
These are slopes from this one run; the PCM reduction illustrates the scheduling noise, not a speedup guarantee.
The two-destination codec pass includes a
174.88 ms host scheduling outlier, with no dropped input frames. This is console pacing, not measured RTP lateness.
Do not turn these numbers into a device-independent CPU promise. All input/bus leases return to zero after
disposal. Private bytes include retained CLR/codec heaps. The warm-cycle regression separately requires no
packet allocations from the capture/mix loop; Media Foundation wrapper allocations remain visible above.

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
