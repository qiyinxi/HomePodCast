#!/usr/bin/env bash
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == "true" ]] || { echo 'CI only' >&2; exit 1; }
root="$(cd "$(dirname "$0")/../.." && pwd)"
results="$root/test-results/airplay-compat"
fixture="$root/test-results/aac-fixture"
mkdir -p "$results"
receiver=""
cleanup() { if [[ -n "$receiver" ]]; then docker rm -f "$receiver" >/dev/null 2>&1 || true; fi; }
trap cleanup EXIT
docker build -t homepodcast-airplay2-ci -f "$root/tools/ci/airplay2-receiver.Dockerfile" "$root"
# Decode the actual Windows encoder output with the receiver image's independent FFmpeg decoder.
docker run --rm --entrypoint ffmpeg -v "$fixture:/fixture" homepodcast-airplay2-ci \
  -hide_banner -loglevel error -i /fixture/signal.aac -f s16le -acodec pcm_s16le /fixture/reference.pcm
dotnet build "$root/tests/HomePodCast.ProtocolHarness" -c Release
run_case() {
  local mode="$1" latency="$2"
  local label="$mode-$latency"
  touch "$results/$label.pcm"
  receiver="$(docker run -d -e GITHUB_ACTIONS=true -v "$results:/results" homepodcast-airplay2-ci "$label")"
  local host
  host="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$receiver")"
  # Wait on the receiver's TCP listener; this does not start an audio session.
  for attempt in $(seq 1 60); do
    if (echo > "/dev/tcp/$host/7000") 2>/dev/null; then break; fi
    sleep 1
  done
  if ! sudo env GITHUB_ACTIONS=true "$(command -v dotnet)" \
    "$root/tests/HomePodCast.ProtocolHarness/bin/Release/net10.0/HomePodCast.ProtocolHarness.dll" \
    --airplay2 "$host" 7000 "$latency" "$fixture" "$results/$label.pcm" "$mode" 2>&1 | tee "$results/$label-sender.log"; then
    docker logs "$receiver" > "$results/$label.log" 2>&1
    tail -n 100 "$results/$label.log"
    tail -n 60 "$results/$label-ptp.log" || true
    {
      echo "### AirPlay 2 software receiver failure: $label"
      echo '```text'
      tail -n 80 "$results/$label-sender.log"
      tail -n 100 "$results/$label.log"
      tail -n 60 "$results/$label-ptp.log" || true
      echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
    return 1
  fi
  docker logs "$receiver" > "$results/$label.log" 2>&1
  docker rm -f "$receiver" >/dev/null
  receiver=""
}
run_case realtime 500
run_case buffered 1000
run_case buffered 1500
run_case buffered 2000
