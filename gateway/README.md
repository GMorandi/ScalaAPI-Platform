# ScalaAPI Gateway

**English** | [简体中文](README.zh-CN.md)

High-performance LLM API edge gateway for the ScalaAPI platform. Built in C++23
on the [Photon](https://github.com/alibaba/PhotonLibOS) coroutine runtime, it
terminates client protocols, enforces gateway-side policy, and forwards every
billable request to the ScalaAPI platform over a Cap'n Proto IPC channel.

> Status: active development, not yet release-certified. Developed and released
> inside the ScalaAPI-Platform repository (this directory is an in-tree subtree
> with preserved history); the standalone ScalaAPI-GateWay repository is
> archived.

## What it does

- **Protocol surface**: OpenAI Chat Completions / Responses, Anthropic Messages,
  Gemini generateContent (JSON and SSE), embeddings, model catalogue, token
  counting — with strict terminal/usage validation on every stream.
- **Media**: image generation (sync/async/batch), video, and large multipart
  bodies. Bodies over 512 KiB are uploaded to the platform through a chunked
  blob RPC and replaced by a digest reference, keeping the dispatch frame small.
- **Realtime**: WebSocket sessions (OpenAI Realtime shape) with bidirectional
  frame piping, session-level caps (bytes / frames / duration), and a `safe`
  abort disposition that settles evidence without charging on strategic aborts.
- **Dispatch**: every request acquires a lease + balance hold from the platform
  via Cap'n Proto RPC over a Unix domain socket, with idempotency keys,
  retry-aware failover, and usage settlement reporting (tokens, cache, duration,
  disconnect reasons).
- **Scheduling**: account/model routing, sticky sessions, RPM limits, 429
  cooldown, and upstream failover driven by platform dispatch decisions.

## Layout

```
src/
  server/      HTTP server, gateway handler, routing, capability matching
  protocol/    Per-protocol parsing, SSE, conversions, validation
  dispatch/    Cap'n Proto dispatch client (lease/usage/abort/blob RPC)
  forwarder/   Upstream forwarding, streaming, retries
  auth/        API key authentication and scopes
  cache/       Garnet-backed catalogue/config cache
  platform/    Platform channel management
  usage/       Usage event collection and reporting
               Cap'n Proto contract: canonical copy at ../contracts/capnp,
               consumed directly at build time (no vendored copy)
test/          Unit tests (CTest) and benchmarks
```

## Build

Requires a C++23 compiler, CMake 3.20+, and network access on first configure
(Cap'n Proto v1.0.2 and Photon are fetched by `FetchContent`). The gateway must
be built from inside a ScalaAPI-Platform checkout because CMake consumes the
canonical contract at `../contracts/capnp`. From the repository root:

```sh
cmake -S gateway -B gateway/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build gateway/build -j"$(nproc)"
```

On CMake 4.x the `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` flag is required by the
bundled Cap'n Proto build. Tests build by default (`GATEWAY_BUILD_TESTS=ON`);
benchmarks are optional (`-DGATEWAY_BUILD_BENCHMARKS=ON`):

```sh
cmake -S gateway -B gateway/build -DGATEWAY_BUILD_TESTS=ON -DGATEWAY_BUILD_BENCHMARKS=ON
cmake --build gateway/build -j"$(nproc)"
ctest --test-dir gateway/build --output-on-failure
```

## Running

The gateway is designed to run as a container beside the platform silos, with
the dispatch socket shared through a volume. See
[`deploy/stack/`](https://github.com/GMorandi/ScalaAPI-Platform/tree/master/deploy/stack)
in ScalaAPI-Platform for the reference Compose topology (two gateways, two
platform silos, the Admin API, admin/user web, PostgreSQL, Garnet, MinIO,
provider mock). Key environment variables:

| Variable | Purpose |
| --- | --- |
| `CAPNP_UDS_PATH` | Platform dispatch Unix socket path (default `/var/run/scalaapi/dispatch.sock`) |
| `GATEWAY_LISTEN_PORT` / `GATEWAY_CORES` | Listen port (default 8080) and Photon vCPU count |
| `SCALAPI_MAX_SESSION_BYTES` / `_FRAMES` / `_DURATION_SEC` | Realtime session caps |

The inline body threshold before blob offload is a fixed 512 KiB constant
(`kMaxInlineRequestBodyBytes`), not an environment setting.

## Contract discipline

The Cap'n Proto schema has a single canonical copy at `../contracts/capnp`,
which this build consumes directly. Any schema change must update the schemas,
`SHA256SUMS`, the generated Platform C# output, and the gateway protocol
fixtures in the same commit. At the repository root,
`scripts/verify-contracts.sh` checks the recorded digests, and
`scripts/verify-generated-contracts.sh` regenerates the C# output with the
pinned compiler and compares it byte-for-byte.
