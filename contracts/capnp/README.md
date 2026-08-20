# ScalaAPI Internal RPC Contract

This directory is the canonical source for revision 1 of the ScalaAPI internal
Cap'n Proto contract. It is a new product contract and contains no Sub2API
compatibility branch.

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
