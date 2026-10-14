#!/usr/bin/env bash
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == "true" ]] || { echo "Run this test only in GitHub Actions" >&2; exit 1; }
root="$(cd "$(dirname "$0")/../.." && pwd)"
scratch="$(mktemp -d "${RUNNER_TEMP:?}/airplay-compat.XXXXXX")"
receiver_pid=""
reader_pid=""
cleanup() {
  if [[ -n "$receiver_pid" ]]; then kill "$receiver_pid" 2>/dev/null || true; fi
  if [[ -n "$reader_pid" ]]; then kill "$reader_pid" 2>/dev/null || true; fi
}
trap cleanup EXIT
sudo apt-get update
sudo apt-get install -y --no-install-recommends build-essential autoconf automake libtool pkg-config \
  libpopt-dev libconfig-dev libavahi-client-dev avahi-daemon libssl-dev libsoxr-dev
sudo systemctl start avahi-daemon
git clone --branch 4.3.7 --depth 1 https://github.com/mikebrady/shairport-sync.git "$scratch/shairport-sync"
cd "$scratch/shairport-sync"
[[ "$(git rev-parse HEAD)" == "0b1c4391ffd398e7b145eb4b98416261380adeea" ]]
autoreconf -fi
./configure --with-pipe --with-avahi --with-ssl=openssl
make -j2
mkdir -p "$root/test-results/airplay-compat"
mkfifo "$scratch/audio.pipe"
cat "$scratch/audio.pipe" > "$root/test-results/airplay-compat/receiver.pcm" &
reader_pid=$!
cat > "$scratch/receiver.conf" <<EOF
general = { name = "HomePodCast CI RAOP"; port = 7001; output_backend = "pipe"; mdns_backend = "avahi"; };
pipe = { name = "$scratch/audio.pipe"; };
EOF
./shairport-sync -c "$scratch/receiver.conf" -v > "$root/test-results/airplay-compat/receiver.log" 2>&1 &
receiver_pid=$!
cd "$root"
dotnet run --project tests/HomePodCast.ProtocolHarness -c Release -- --self-test
dotnet run --project tests/HomePodCast.ProtocolHarness -c Release -- --discover "HomePodCast CI RAOP" 7001
