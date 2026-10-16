#!/usr/bin/env bash
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == "true" ]] || { echo 'CI only' >&2; exit 1; }
label="${1:?case label required}"
pipe_buffer=0.02
# 2026-10-14: AAC's 1024-frame player wake can take 32 ms. A 40 ms pipe buffer
# prevents receiver-scheduler drops; the sender's requested-delay check stays at +/-50 ms.
if [[ "$label" == buffered-* || "$label" == group-buffered-* ]]; then pipe_buffer=0.04; fi
mkdir -p /run/dbus
dbus-daemon --system --fork
avahi-daemon --daemonize --no-drop-root
# avahi-daemon --daemonize returns while the server is still probing its host name (state 1,
# REGISTERING). shairport-sync creates its Avahi entry group only when the server reaches state 2
# (RUNNING); a SETUP before that makes it update the TXT record of a NULL group, and libavahi
# aborts it. 2026-10-15, v1.1.0-beta.6 tag build: "Assertion `group' failed" when the sender's
# SETUP came within a second of the container's start; in a passing run RUNNING came 0.9 s after
# shairport-sync started.
# Start shairport-sync only on a running avahi-daemon. The CI script then waits for the service
# itself to be published before the sender connects.
avahi_state=""
for _ in $(seq 1 100); do
  avahi_state="$(dbus-send --system --print-reply=literal --dest=org.freedesktop.Avahi / \
    org.freedesktop.Avahi.Server.GetState 2>/dev/null || true)"
  if [[ "$avahi_state" =~ int32\ 2$ ]]; then break; fi
  sleep 0.1
done
[[ "$avahi_state" =~ int32\ 2$ ]] || { echo "avahi-daemon did not reach RUNNING: '$avahi_state'" >&2; exit 1; }
echo "avahi-daemon is running"
/nqptp/nqptp > "/results/$label-ptp.log" 2>&1 &
mkfifo /audio.pipe
cat /audio.pipe > "/results/$label.pcm" &
cat > /receiver.conf <<EOF
general = { name = "HomePodCast CI AirPlay 2"; port = 7000; output_backend = "pipe"; mdns_backend = "avahi"; ignore_volume_control = "yes"; audio_backend_buffer_desired_length_in_seconds = $pipe_buffer; };
pipe = { name = "/audio.pipe"; };
EOF
exec /receiver/shairport-sync -c /receiver.conf -vvv
