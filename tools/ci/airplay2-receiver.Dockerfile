# CI-only receiver namespace: nqptp owns its 319/320, the sender owns the host's 319/320.
FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential git autoconf automake libtool pkg-config libpopt-dev libconfig-dev \
    libavahi-client-dev avahi-daemon avahi-utils dbus libssl-dev libsoxr-dev libplist-dev libsodium-dev \
    libavutil-dev libavcodec-dev libavformat-dev uuid-dev libgcrypt-dev xxd ffmpeg ca-certificates
RUN git clone --depth 1 --branch 1.2.4 https://github.com/mikebrady/nqptp.git /nqptp \
    && cd /nqptp && test "$(git rev-parse HEAD)" = 591f425d9da69f1c4e09f3ad09611b758937b3e5 \
    && autoreconf -fi && ./configure && make -j2
RUN git clone --depth 1 --branch 4.3.7 https://github.com/mikebrady/shairport-sync.git /receiver \
    && cd /receiver && test "$(git rev-parse HEAD)" = 0b1c4391ffd398e7b145eb4b98416261380adeea \
    && autoreconf -fi && ./configure --with-airplay-2 --with-pipe --with-avahi --with-ssl=openssl && make -j2
COPY tools/ci/airplay2-receiver.sh /entrypoint.sh
ENTRYPOINT ["bash", "/entrypoint.sh"]
