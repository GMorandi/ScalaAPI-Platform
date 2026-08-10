# ScalaAPI Development Stack

This directory is the versioned source of truth for the independent ScalaAPI
stack. It expects the `gateway` repository next to this `platform` repository and
does not read or start Sub2API.

Create an environment file from `.env.example`, replace every secret placeholder,
then run from this directory:

```sh
docker compose up --build -d
```

`SECURITY_MASTER_KEY` must be Base64 for exactly 32 bytes. The checked-in profile
uses authenticated Garnet over the private container network. Set `GARNET_TLS=true`,
set the certificate service name, and mount a trusted CA through a production
override before using an untrusted network.

The root workspace Compose file is a convenience launcher. Changes must be applied
to this versioned file first and verified against an empty volume set. The stack
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

The wrapper requires `openssl`, creates a short-lived local CA and a `garnet`
server certificate with a `DNS:garnet` SAN, exports the server certificate as a
password-protected PFX, and removes the temporary key material on exit. The TLS
Compose override enables Garnet server TLS, disables client-certificate
requirement for this password-authenticated deployment, mounts the PFX and CA
read-only with rootless-container relabeling, and passes the CA path and server
name to both production clients. The smoke verifies TLS through Platform's real
authenticated Garnet readiness path, then runs the complete source-built fault,
restart, reconciliation, and object-storage matrix. During the TLS gate, the
wrapper replaces the PFX with a second certificate signed by the same CA,
forces Platform/Gateway reconnects, rejects a wrong-SAN certificate and an
expired certificate through readiness, restores the valid certificate, and
settles one new billable request. Set
`GARNET_SERVER_CERT_PASSWORD`, `GARNET_SERVER_NAME`, or
`GARNET_CERT_REFRESH_SECONDS` to exercise deployment-specific values. The
default development stack remains plaintext; TLS is selected only by the
wrapper or by setting `GARNET_TLS=true` together with all required certificate
paths.

The gate creates a unique Compose project and new named volumes, builds the
current Platform and sibling Gateway sources, and removes only that project on
exit. It applies all migrations, runs the migrator again, configures a new user,
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

The same gate seeds independent Provider mock accounts for HTTP 429, HTTP 500,
malformed usage, upstream disconnect, and timeout scenarios. For every scenario
it requires a terminal aborted lease, a released balance hold, no usage event,
no usage log, no ledger entry, no request log, and one aborted idempotency record.
Independent accounts prevent one scenario's scheduler cooldown from masking the
next scenario.

Use `CONTAINER_CLI=podman` or `CONTAINER_CLI=docker` to select the runtime. Set
`KEEP_STACK=1` to retain a failed or successful project for inspection, and set
the `SMOKE_*_PORT` variables documented in `smoke.sh` when the default host ports
are occupied. To exercise a previously built Gateway image without rebuilding
it, set `GATEWAY_IMAGE` together with `SMOKE_SKIP_BUILD=1`; the default still
builds the sibling source. By default, the cleanup trap removes only the unique
Compose project and volumes created by that smoke run.
