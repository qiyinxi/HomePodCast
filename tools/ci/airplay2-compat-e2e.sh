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

since() { awk -v start="$1" -v now="$EPOCHREALTIME" 'BEGIN { printf "%.2f", now - start }'; }
receiver_running() { [[ "$(docker inspect -f '{{.State.Running}}' "$receiver" 2>/dev/null)" == true ]]; }
published() {
  timeout 10 docker exec "$receiver" avahi-browse --resolve --parsable --terminate _airplay._tcp 2>/dev/null \
    | awk -F';' -v host="$1" '$1 == "=" && $8 == host && $9 == "7000" { found = 1 } END { exit !found }'
}

# Neither probe starts an audio session.
wait_for_receiver() {
  local host="$1" label="$2" started="$EPOCHREALTIME" deadline=$((SECONDS + 60)) port_open
  until timeout 2 bash -c "echo > /dev/tcp/$host/7000" 2>/dev/null; do
    receiver_running || { echo "$label: receiver exited before its RTSP port opened" >&2; return 1; }
    (( SECONDS < deadline )) || { echo "$label: RTSP port 7000 did not open within 60 s" >&2; return 1; }
    sleep 0.2
  done
  port_open="$(since "$started")"
  # shairport-sync opens its RTSP port before it has an Avahi entry group, and aborts on a SETUP
  # that comes before the group (airplay2-receiver.sh). Its service, as seen by the container's
  # own avahi-daemon, shows that the group exists.
  until published "$host"; do
    receiver_running || { echo "$label: receiver exited before its service was published" >&2; return 1; }
    (( SECONDS < deadline )) || { echo "$label: _airplay._tcp was not published within 60 s" >&2; return 1; }
    sleep 0.2
  done
  echo "$label: RTSP port open after ${port_open} s, _airplay._tcp published after $(since "$started") s"
}

run_sender() {
  local mode="$1" latency="$2" label="$3" host="$4"
  sudo env GITHUB_ACTIONS=true "$(command -v dotnet)" \
    "$root/tests/HomePodCast.ProtocolHarness/bin/Release/net10.0/HomePodCast.ProtocolHarness.dll" \
    --airplay2 "$host" 7000 "$latency" "$fixture" "$results/$label.pcm" "$mode" 2>&1 | tee "$results/$label-sender.log"
}

# Returns 0 when the case passed, 2 when shairport-sync exited before any audio reached its pipe
# (a receiver crash, exit status in $crash_status), and 1 for every other failure.
crash_status=""
attempt_case() {
  local mode="$1" latency="$2" label="$3" host status
  : > "$results/$label.pcm"
  receiver="$(docker run -d -e GITHUB_ACTIONS=true -v "$results:/results" homepodcast-airplay2-ci "$label")" || return 1
  host="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$receiver")" || return 1
  if wait_for_receiver "$host" "$label" && run_sender "$mode" "$latency" "$label" "$host"; then
    docker logs "$receiver" > "$results/$label.log" 2>&1
    docker rm -f "$receiver" >/dev/null
    receiver=""
    return 0
  fi
  # shairport-sync is the container's main process, so the container stops when it exits.
  status="$(timeout 5 docker wait "$receiver" 2>/dev/null || true)"
  docker logs "$receiver" > "$results/$label.log" 2>&1 || true
  if [[ -n "$status" && ! -s "$results/$label.pcm" ]]; then crash_status="$status"; return 2; fi
  return 1
}

report_failure() {
  local label="$1"
  grep -E "starting frame|Dropping|too_soon|decod|anchor|latency|timing|time difference" "$results/$label.log" | tail -n 100 || true
  tail -n 100 "$results/$label.log" || true
  tail -n 60 "$results/$label-ptp.log" 2>/dev/null || true
  {
    echo "### AirPlay 2 software receiver failure: $label"
    echo '```text'
    tail -n 80 "$results/$label-sender.log" 2>/dev/null || true
    tail -n 100 "$results/$label.log" || true
    tail -n 60 "$results/$label-ptp.log" 2>/dev/null || true
    echo '```'
  } >> "$GITHUB_STEP_SUMMARY"
}

run_case() {
  local mode="$1" latency="$2"
  local label="$mode-$latency" status=0
  attempt_case "$mode" "$latency" "$label" || status=$?
  # A receiver crash before any audio arrived gets one more try, so that an intermittent
  # shairport-sync abort cannot block a release. Wrong audio, a latency miss, gaps or a crash
  # after audio arrived still fail the case at once, and so does a second crash.
  if (( status == 2 )); then
    local first="$label-attempt1"
    mv "$results/$label.log" "$results/$first.log"
    mv "$results/$label-sender.log" "$results/$first-sender.log" 2>/dev/null || true
    cp "$results/$label-ptp.log" "$results/$first-ptp.log" 2>/dev/null || true
    docker rm -f "$receiver" >/dev/null
    receiver=""
    echo "::warning title=AirPlay 2 receiver crashed in $label::shairport-sync exited with status $crash_status before any audio reached its pipe. Retrying $label once; the first attempt's logs are $first*.log in the airplay-compat-results artifact."
    echo "================ RECEIVER CRASH in $label (exit status $crash_status): RETRYING ONCE ================"
    tail -n 40 "$results/$first.log"
    {
      echo "### AirPlay 2 receiver crashed in $label (exit status $crash_status); the case was retried once"
      echo '```text'
      tail -n 40 "$results/$first.log"
      echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
    status=0
    attempt_case "$mode" "$latency" "$label" || status=$?
    if (( status == 0 )); then echo "$label passed on its retry after the receiver crash"; fi
  fi
  if (( status != 0 )); then
    if (( status == 2 )); then echo "$label: shairport-sync crashed again (exit status $crash_status)" >&2; fi
    report_failure "$label"
    return 1
  fi
}
run_case realtime 500
run_case buffered 1000
run_case buffered 1500
run_case buffered 2000
