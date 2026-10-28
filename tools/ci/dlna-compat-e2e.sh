#!/usr/bin/env bash
set -euo pipefail
# Namespace creation, the packages it needs and the software renderer are deliberately CI-only.
[[ "${GITHUB_ACTIONS:-}" == "true" ]] || { echo 'Run this test only in GitHub Actions' >&2; exit 1; }
[[ "$(uname -s)" == Linux ]] || { echo 'This test requires a Linux CI runner' >&2; exit 1; }
root="$(cd "$(dirname "$0")/../.." && pwd)"
results="$root/test-results/dlna-compat"
fixtures="$root/test-results/dlna-codec-fixture"
scratch="$(mktemp -d "${RUNNER_TEMP:?}/dlna-compat.XXXXXX")"
sender_ns=dlna-send
renderer_ns=dlna-render
sender_created=false
renderer_created=false
receiver_pid=""
mkdir -p "$results"

stop_namespace_processes() {
  local namespace="$1" pids
  pids="$(sudo ip netns pids "$namespace" 2>/dev/null || true)"
  if [[ -n "$pids" ]]; then
    # The namespaces were created by this script; no host process can appear in this list.
    sudo kill $pids 2>/dev/null || true
    for _ in {1..20}; do
      [[ -z "$(sudo ip netns pids "$namespace" 2>/dev/null)" ]] && break
      sleep 0.1
    done
    pids="$(sudo ip netns pids "$namespace" 2>/dev/null || true)"
    if [[ -n "$pids" ]]; then sudo kill -KILL $pids 2>/dev/null || true; fi
  fi
}
stop_receiver() {
  if [[ "$renderer_created" == true ]]; then stop_namespace_processes "$renderer_ns"; fi
  if [[ -n "$receiver_pid" ]]; then wait "$receiver_pid" 2>/dev/null || true; receiver_pid=""; fi
}
cleanup() {
  stop_receiver
  if [[ "$sender_created" == true ]]; then
    stop_namespace_processes "$sender_ns"
    sudo ip netns del "$sender_ns" 2>/dev/null || true
  fi
  if [[ "$renderer_created" == true ]]; then sudo ip netns del "$renderer_ns" 2>/dev/null || true; fi
  # mktemp produced this directory directly under RUNNER_TEMP, before any namespace was created.
  rm -rf -- "$scratch"
}
trap cleanup EXIT

# The build packages, GStreamer and FFmpeg come from the workflow's apt cache step (.github/workflows/build.yml).
# Build the unmodified upstream renderer, pinned to v0.3.1. Nothing is installed from its build.
git clone --branch v0.3.1 --depth 1 https://github.com/hzeller/gmrender-resurrect.git "$scratch/gmrender-resurrect"
cd "$scratch/gmrender-resurrect"
[[ "$(git rev-parse HEAD)" == 3d87b3678bef4d0886cc526fe75d5e1285704310 ]]
mkdir -p "$scratch/share"
ln -s "$scratch/gmrender-resurrect/data" "$scratch/share/gmediarender"
./autogen.sh
./configure --datadir="$scratch/share"
make -j2
./src/gmediarender --version > "$results/renderer-version.txt"
for element in playbin souphttpsrc unalignedaudioparse wavparse aacparse mpegaudioparse avdec_aac avdec_mp3float audioconvert filesink fakesink; do
  gst-inspect-1.0 "$element" >> "$results/gstreamer-elements.txt"
done
# Fail before starting the renderer if its file-only output pipeline cannot be built.
gst-launch-1.0 -q audiotestsrc num-buffers=1 wave=silence \
  ! audioconvert dithering=none noise-shaping=none \
  ! audio/x-raw,format=S16LE,rate=44100,channels=2,layout=interleaved \
  ! filesink location="$scratch/pipeline-check.pcm" sync=true buffer-mode=unbuffered
cd "$root"
dotnet build tests/HomePodCast.ProtocolHarness -c Release
dotnet tests/HomePodCast.ProtocolHarness/bin/Release/net10.0/HomePodCast.ProtocolHarness.dll --self-test
dotnet_path="$(command -v dotnet)"

# These media packets came from the production Windows MFTs, including their real priming/output batches.
# FFmpeg is an independent decode reference. The renderer's lossy output is never compared sample-exactly
# to the original PCM. No encoder or upstream renderer source is substituted on Linux.
ffmpeg -version > "$results/ffmpeg-version.txt"
for format in aac mp3; do
  [[ -s "$fixtures/$format/manifest.json" && -s "$fixtures/$format/source.pcm" && -s "$fixtures/$format/signal.$format" ]]
  ffmpeg -nostdin -v error -y -f "$format" -i "$fixtures/$format/signal.$format" \
    -map 0:a:0 -acodec pcm_s16le -ar 44100 -ac 2 -f s16le "$fixtures/$format/reference.pcm"
done
cp -a "$fixtures" "$results/codec-fixtures"

# Two veth peers, no bridge, default gateway, forwarding or host-facing interface. SSDP, SOAP
# and the renderer's HTTP pull cannot reach a real LAN device even if discovery has a bug.
if sudo ip netns list | awk '{print $1}' | grep -Eq '^(dlna-send|dlna-render)$'; then
  echo 'Refusing to reuse an existing DLNA test namespace' >&2
  exit 1
fi
sudo ip netns add "$sender_ns"
sender_created=true
sudo ip netns add "$renderer_ns"
renderer_created=true
sudo ip -n "$sender_ns" link add ci-send type veth peer name ci-render
sudo ip -n "$sender_ns" link set ci-render netns "$renderer_ns"
sudo ip -n "$sender_ns" address add 10.77.0.1/24 dev ci-send
sudo ip -n "$renderer_ns" address add 10.77.0.2/24 dev ci-render
sudo ip -n "$sender_ns" link set lo up
sudo ip -n "$renderer_ns" link set lo up
sudo ip -n "$sender_ns" link set ci-send up
sudo ip -n "$renderer_ns" link set ci-render up
sudo ip -n "$sender_ns" route add 239.255.255.250/32 dev ci-send
sudo ip -n "$renderer_ns" route add 239.255.255.250/32 dev ci-render
sudo ip -n "$sender_ns" address show > "$results/sender-network.txt"
sudo ip -n "$sender_ns" route show >> "$results/sender-network.txt"
sudo ip -n "$renderer_ns" address show > "$results/renderer-network.txt"
sudo ip -n "$renderer_ns" route show >> "$results/renderer-network.txt"
for namespace in "$sender_ns" "$renderer_ns"; do
  [[ -z "$(sudo ip -n "$namespace" route show default)" ]]
  [[ "$(sudo ip -n "$namespace" -o -4 address show scope global | wc -l)" -eq 1 ]]
done

run_case() {
  local format="$1" framing="$2" label="$1-$2" pcm
  local fixture_args=()
  if [[ "$format" == aac || "$format" == mp3 ]]; then fixture_args=("$fixtures/$format"); fi
  pcm="$results/$label.pcm"
  : > "$pcm"
  sudo ip netns exec "$renderer_ns" env GST_DEBUG=2 \
    "$scratch/gmrender-resurrect/src/gmediarender" \
    --interface-name ci-render --port 49494 --friendly-name 'HomePodCast CI DLNA' \
    --uuid 83f9bc76-6452-4aa8-b236-ad9b87b9c124 --output gst --logfile stdout \
    --gstout-audiopipe "audioconvert dithering=none noise-shaping=none ! audio/x-raw,format=S16LE,rate=44100,channels=2,layout=interleaved ! filesink location=\"$pcm\" sync=true buffer-mode=unbuffered" \
    --gstout-videosink fakesink --gstout-buffer-duration 0 --gstout-initial-volume-db 0 \
    > "$results/$label-renderer.log" 2>&1 &
  receiver_pid=$!
  # Readiness comes from the renderer itself; SSDP discovery remains part of the harness.
  local ready=false
  for _ in {1..100}; do
    if grep -q 'Ready for rendering' "$results/$label-renderer.log"; then ready=true; break; fi
    if ! kill -0 "$receiver_pid" 2>/dev/null; then break; fi
    sleep 0.1
  done
  if [[ "$ready" != true ]]; then
    tail -n 100 "$results/$label-renderer.log"
    echo "$label: software renderer did not become ready" >&2
    return 1
  fi
  # The harness also checks the sole IPv4 address and absence of a default route before the
  # CI-only 100% gain needed for an exact PCM comparison. No audio-device sink is present.
  if ! sudo ip netns exec "$sender_ns" env GITHUB_ACTIONS=true HOMEPODCAST_DLNA_CI_ISOLATED=1 \
    GITHUB_STEP_SUMMARY="${GITHUB_STEP_SUMMARY:-}" \
    "$dotnet_path" "$root/tests/HomePodCast.ProtocolHarness/bin/Release/net10.0/HomePodCast.ProtocolHarness.dll" \
    --dlna 'HomePodCast CI DLNA' 10.77.0.1 10.77.0.2 "$format" "$framing" "$pcm" "${fixture_args[@]}" \
    2>&1 | tee "$results/$label-sender.log"; then
    tail -n 120 "$results/$label-renderer.log"
    return 1
  fi
  stop_receiver
}

# Exercise the renderer's normal advertised protocol list, including its upstream audio/*
# wildcard. No MIME override, decoder replacement or wavparse length workaround is used.
run_case l16 chunked
run_case wav length
run_case aac chunked
run_case aac length
run_case mp3 chunked
run_case mp3 length

python3 - "$results" <<'PY'
import glob, json, os, statistics, sys
paths = sorted(glob.glob(os.path.join(sys.argv[1], '*.result.json')))
if len(paths) != 4:
    raise SystemExit('Expected all four AAC/MP3 framing result records')
records = [json.load(open(path, encoding='utf-8')) for path in paths]
median = statistics.median(record['softwarePrebufferMedianMs'] for record in records)
text = (f'DLNA compressed software renderer: all four codec/framing cases passed; '
        f'median filesink prebuffer across cases = {median:.1f} ms. '
        'This measures the isolated software receiver and scheduling, not speaker or acoustic latency.')
print(text)
with open(os.path.join(sys.argv[1], 'compressed-summary.txt'), 'w', encoding='utf-8') as output:
    output.write(text + '\n')
if os.environ.get('GITHUB_STEP_SUMMARY'):
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
        output.write(text + '\n\n')
PY
