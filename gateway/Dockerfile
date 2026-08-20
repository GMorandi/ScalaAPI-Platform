# Built from the repository root (context = ScalaAPI-Platform root) so the
# gateway consumes the canonical Cap'n Proto contract in contracts/capnp.
# The per-Dockerfile ignore file gateway/Dockerfile.dockerignore keeps the
# context limited to gateway/ and contracts/capnp/.
FROM ubuntu:24.04 AS build
RUN apt-get update && apt-get install -y \
    build-essential cmake git libaio-dev libssl-dev libsqlite3-dev zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY contracts/capnp/ ./contracts/capnp/
COPY gateway/ ./gateway/
RUN cmake -S gateway -B gateway/build -DCMAKE_BUILD_TYPE=Release \
        -DGATEWAY_BUILD_TESTS=OFF \
        -DGATEWAY_BUILD_BENCHMARKS=OFF && \
    cmake --build gateway/build --parallel $(nproc)

FROM ubuntu:24.04 AS runtime
RUN apt-get update && apt-get install -y libaio1t64 libssl3 libsqlite3-0 curl \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /var/run/scalaapi /var/lib/scalaapi
COPY --from=build /src/gateway/build/gateway /usr/local/bin/gateway
EXPOSE 8080
ENTRYPOINT ["gateway"]
