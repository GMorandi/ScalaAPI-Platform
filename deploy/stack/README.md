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
to this versioned file first and verified against an empty volume set.

Run the repository-owned greenfield gate from any directory with Docker Compose
or Podman Compose available:

```sh
deploy/stack/smoke.sh
```

The gate creates a unique Compose project and new named volumes, builds the
current Platform and sibling Gateway sources, and removes only that project on
exit. It applies all migrations, runs the migrator again, configures a new user,
API key, Provider mock groups, and active price versions through product APIs,
then verifies chat settlement, idempotent replay, Garnet authentication, and an
asynchronous image stored in the S3-compatible object store. It also restarts the
Platform and Gateway separately, sends a new billable request after each restart,
and checks the resulting lease, hold, usage, ledger, and outbox invariants. Use
`CONTAINER_CLI=podman` or `CONTAINER_CLI=docker` to select the runtime. Set
`KEEP_STACK=1` to retain a failed or successful project for inspection, and set
the `SMOKE_*_PORT` variables documented in `smoke.sh` when the default host ports
are occupied.
