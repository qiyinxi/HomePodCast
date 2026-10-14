#!/usr/bin/env bash
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == "true" ]] || { echo 'CI only' >&2; exit 1; }
label="${1:?case label required}"
mkdir -p /run/dbus
dbus-daemon --system --fork
avahi-daemon --daemonize --no-drop-root
/nqptp/nqptp > "/results/$label-ptp.log" 2>&1 &
mkfifo /audio.pipe
cat /audio.pipe > "/results/$label.pcm" &
cat > /receiver.conf <<EOF
general = { name = "HomePodCast CI AirPlay 2"; port = 7000; output_backend = "pipe"; mdns_backend = "avahi"; ignore_volume_control = "yes"; audio_backend_buffer_desired_length_in_seconds = 0.02; };
pipe = { name = "/audio.pipe"; };
EOF
exec /receiver/shairport-sync -c /receiver.conf -vvv
