# ScalaAPI Development Stack

This directory is the versioned source of truth for the independent ScalaAPI
stack. It builds the in-tree `gateway/` from the repository-root build context
and does not read or start Sub2API.

Create an environment file from `.env.example`, replace every secret placeholder,
then run from this directory:

```sh
./start.sh
```

`start.sh` detects the container runtime automatically: it uses Docker Compose
when available and falls back to Podman Compose. Set `CONTAINER_CLI=docker` or
`CONTAINER_CLI=podman` to override the detection, and pass `--env-file FILE` to
use an environment file other than the default `dev.env`. If required
variables are missing, empty, or still set to `.env.example` placeholder
values, the script lists every problem and stops before invoking Compose. For
a demo run, `./start.sh --demo` instead generates every such value with
openssl, prints them to the terminal, and saves them into the environment
file (created with mode 600 when missing) so later runs reuse them.

PostgreSQL applies `POSTGRES_PASSWORD` only while initializing an empty data
volume. Rotating any secret on an existing deployment breaks access to the
stored data; reset the stack with `docker compose down -v` (or the Podman
equivalent — this deletes all data) or migrate the data first.

Unset image variables build every component from source. To deploy a release
instead, pull the published images (anonymous access, no registry login
needed):

```sh
./start.sh --release          # newest stable tag, looked up from ghcr.io
./start.sh --release v0.1.1   # explicit tag
```

This exports the five `*_IMAGE` variables below; setting them by hand in the
environment file and running `./start.sh --no-build` is equivalent:

```sh
GATEWAY_IMAGE=ghcr.io/gmorandi/scalaapi-platform/gateway:v0.1.1
PLATFORM_SILO_IMAGE=ghcr.io/gmorandi/scalaapi-platform/platform-silo:v0.1.1
ADMIN_API_IMAGE=ghcr.io/gmorandi/scalaapi-platform/admin-api:v0.1.1
MIGRATOR_IMAGE=ghcr.io/gmorandi/scalaapi-platform/migrator:v0.1.1
PROVIDER_MOCK_IMAGE=ghcr.io/gmorandi/scalaapi-platform/provider-mock:v0.1.1
./start.sh --no-build
```

The admin-web and user-web frontends always build from source; the release
publishes only the five service images above.

`SECURITY_MASTER_KEY` must be Base64 for exactly 32 bytes. The checked-in profile
uses authenticated Garnet over the private container network. Set `GARNET_TLS=true`,
set the certificate service name, and mount a trusted CA through a production
override before using an untrusted network.

This versioned Compose file is the stack definition; changes must be applied
here first and verified against an empty volume set. The stack
serves the Admin Web on port 3000 and the authenticated User Web on port 3001;
both are independent clients of the new Admin API contracts.

Run the repository-owned greenfield gate from any directory with Docker Compose
or Podman Compose available:

```sh
deploy/stack/smoke.sh
```

The default gate uses authenticated plaintext Garnet on the private Compose
network. To run the TLS deployment gate, use the checked-in wrapper:

```sh
deploy/stack/garnet_tls_smoke.sh
```

The wrapper is designed to require `openssl`, create a short-lived local CA and a
`garnet` server certificate with a `DNS:garnet` SAN, export the server certificate
as a password-protected PFX, and remove the temporary key material on exit. The TLS
Compose override enables Garnet server TLS, disables client-certificate
requirement for this password-authenticated deployment, mounts the PFX and CA
read-only with rootless-container relabeling, and passes the CA path and server
name to both production clients. Its acceptance contract requires TLS through
Platform's real authenticated Garnet readiness path, then the source-built fault,
restart, reconciliation, and object-storage matrix. During the TLS gate, the
PFX is replaced with a second certificate signed by the same CA,
forces Platform/Gateway reconnects, rejects a wrong-SAN certificate and an
expired certificate through readiness, restores the valid certificate, and
settles one new billable request. Set
`GARNET_SERVER_CERT_PASSWORD`, `GARNET_SERVER_NAME`, or
`GARNET_CERT_REFRESH_SECONDS` to exercise deployment-specific values. The
default development stack remains plaintext; TLS is selected only by the
wrapper or by setting `GARNET_TLS=true` together with all required certificate
paths.

The gate is designed to create a unique Compose project and new named volumes, build
the current Platform and in-tree Gateway sources, and remove only that project on
exit. Its acceptance contract applies all migrations, runs the migrator again,
configures a new user,
API key, Provider mock groups, and active price versions through product APIs,
then verifies chat settlement, idempotent replay, Garnet authentication, and an
asynchronous image stored in the S3-compatible object store. It also restarts the
Platform and Gateway separately, sends a new billable request after each restart,
and checks the resulting lease, hold, usage, ledger, and outbox invariants. The
same gate requires `python3` and probes the realtime WebSocket path with the
source-owned `realtime_smoke.py` client, including the Provider session/usage
frames and exactly-once lease, usage, hold, and ledger settlement assertions.
It also runs four concurrent realtime sessions (the seeded user concurrency
limit), keeps each upgraded connection
open for a bounded three-second hold, and verifies one completed lease, usage
event/log, committed hold, and NUMERIC debit per session.

`smoke.sh` automatically adds `docker-compose.faults.yml`, which places the
source-owned object-storage TCP fault proxy on the private network. The gate uses
its private control port to truncate one signed PUT after request bytes begin and
to discard one successful response after MinIO has committed the object. Both
paths must retry to deterministic item/archive keys without duplicate settlement.
The normal `docker-compose.yml` does not include this proxy and Platform connects
directly to the S3-compatible service, so the test control API is absent from the
ordinary development and production topology.

The same gate seeds independent Provider mock accounts for ten chat fault
scenarios: HTTP 429, HTTP 500, malformed usage, upstream timeout and disconnect,
stream disconnects (mid-stream, before output, and after usage), client
disconnect, and invalid content type. Explicit 429/500 rejections require a
terminal aborted lease, a released balance hold, no usage event, no usage log,
no ledger entry, and one aborted idempotency record. Malformed usage, timeout,
and disconnect outcomes leave the upstream charge state unknown, so the gate
requires one `reconciliation_needed` lease with the active hold retained for
operator reconciliation. Independent accounts prevent one scenario's scheduler
cooldown from masking the next scenario.

The source-owned Provider matrix also seeds nine Anthropic Messages and nine
Gemini generation groups, plus one revoked-OAuth credential profile per
provider. The full smoke sends provider-native JSON/SSE requests for 429, 500,
malformed payload, timeout, disconnect, client disconnect, disconnect after
usage, invalid content type, and auth rejection profiles. Explicit
429/500 responses must release the hold without usage or debit; malformed,
timeout, and disconnect outcomes must retain one `reconciliation_needed` lease
and active hold. The Provider.Mock contract suite additionally covers the
usage-before-EOF truncation profile. These are new ScalaAPI contracts and do not
import or emulate Sub2API keys, data, or internal compatibility behavior.

The media section also starts a second Platform Silo and injects two isolated
partitions with runtime-neutral `network disconnect/connect` operations. The
first removes only the secondary Silo's object-storage path and requires one
fenced object-reconciliation failure followed by recovery. The second removes
only its PostgreSQL path, requires due media work to remain visible, and after
rejoin verifies `stored|completed|committed`, one usage event, and one usage
debit. This uses a temporary private bridge with explicit dependency aliases,
so it works with rootless Podman and Docker without host firewall privileges.
The cleanup trap detaches every temporary endpoint before removing that bridge.

For the longer worker-contention gate, keep the secondary Silo active after the
partition checks and repeatedly force the same completed batch due. The gate
expects one fenced parent/item claim per cycle, stable deterministic object keys,
one usage event/debit, and no early metadata deletion:

```sh
MEDIA_CONTENTION_SOAK_SECONDS=3600 \
MEDIA_CONTENTION_SOAK_RESTART_EVERY=30 \
MEDIA_CONTENTION_SOAK_INTERVAL_SECONDS=1 \
deploy/stack/smoke.sh
```

`MEDIA_CONTENTION_SOAK_SECONDS` defaults to `0` for the normal gate. When the
restart interval is positive, the secondary Silo is restarted at that cycle
boundary and must become ready and rejoin Orleans before the next cycle.

Use `CONTAINER_CLI=podman` or `CONTAINER_CLI=docker` to select the runtime. Set
`KEEP_STACK=1` to retain a failed or successful project for inspection, and set
the `SMOKE_*_PORT` variables shown in `smoke.sh` when the default host ports
are occupied. To exercise a previously built Gateway image without rebuilding
it, set `GATEWAY_IMAGE` together with `SMOKE_SKIP_BUILD=1`; the default still
builds the sibling source. By default, the cleanup trap removes only the unique
Compose project, volumes, and any temporary partition network created by that
smoke run.
