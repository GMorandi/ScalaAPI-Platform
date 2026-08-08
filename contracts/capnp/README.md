# ScalaAPI Internal RPC Contract

This directory is the canonical source for revision 1 of the ScalaAPI internal
Cap'n Proto contract. It is a new product contract and contains no Sub2API
compatibility branch.

Gateway keeps a release-vendored copy under `gateway/proto` so that its repository
can build independently. From a workspace containing both repositories, run:

```sh
scripts/verify-contracts.sh ../gateway
```

The checked-in Platform C# output is generated with Cap'n Proto `1.0.2` and the
repository-local `capnpc-csharp` `1.3.118` tool manifest. Point the verification
script at that exact compiler build:

```sh
CAPNP_COMPILER=/path/to/capnp-1.0.2 scripts/verify-generated-contracts.sh
```

The command fails if the vendor copy or the recorded digest differs. Contract
changes must update the canonical schemas, generated C# files, the Gateway vendor
copy, both digest files, and protocol fixtures in the same release.
