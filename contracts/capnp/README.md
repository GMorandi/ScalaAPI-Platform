# ScalaAPI Internal RPC Contract

This directory is the canonical source for revision 1 of the ScalaAPI internal
Cap'n Proto contract. It is a new product contract and contains no Sub2API
compatibility branch.

Each RPC frame on the dispatch socket is a 4-byte little-endian length prefix
followed by the payload: payload byte 0 is the method id and the rest is a
single capnp segment. Both peers cap the payload at 8 MiB (8 * 1024 * 1024
bytes) — `MaxFrameBytes` in `src/Platform.Host/Services/CapnpRpcHostedService.cs`
on the Platform side and `kMaxFrameBytes` in
`gateway/src/dispatch/capnp_dispatch_client.cpp` on the Gateway side — and the
two constants must stay in sync. Product-level bounds keep individual bodies at
or below 4 MiB, so the frame cap leaves headroom for capnp overhead. Neither
peer writes a frame beyond the cap; the sender answers with a
method-appropriate non-retryable error instead, and a receiver faced with an
oversize declared length discards the frame and keeps the connection alive.

> The paired contract includes `audioTts @12` and `audioStt @13`. Any change to
> these schemas must update the generated Platform output, the digest files, and
> protocol fixtures in one commit.

The gateway builds directly against this directory: `gateway/CMakeLists.txt`
compiles every schema here via `${CMAKE_SOURCE_DIR}/../contracts/capnp`. Verify
recorded digests from the repository root with:

```sh
scripts/verify-contracts.sh
```

The checked-in Platform C# output is generated with Cap'n Proto `1.0.2` and the
repository-local `capnpc-csharp` `1.3.118` tool manifest. Point the verification
script at that exact compiler build:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

The command fails if the generated output or the recorded digest differs. Contract
changes must update the canonical schemas, generated C# files, the digest files,
and protocol fixtures in the same commit.
