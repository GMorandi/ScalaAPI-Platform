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
