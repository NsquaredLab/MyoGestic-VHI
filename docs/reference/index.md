# Reference

Exhaustive, lookup-oriented detail. For the *why*, see [Concepts](../concepts/index.md);
for *recipes*, see [How-to](../how-to/index.md).

- **[Every signal in and out](signals.md)** - the whole boundary on one page: twenty LSL
  streams, eight RPCs, and which protocol carries what. Start here to find the right page
  below.
- **[gRPC API](grpc-api.md)** - every RPC and message of the `VhiControl`
  service, plus the full `.proto` contract.
- **[LSL streams](lsl-reference.md)** - every inlet and outlet, with stream
  type, channel count, rate, and the nine DOFs in both directions.
- **[Configuration](configuration.md)** - every `[Export]` field, by node,
  with defaults.
- **[C# API](api/index.md)** - the auto-generated class/method reference for
  the `Vhi.*` and `Myogestic.Renderer.*` (gRPC) namespaces, built from the
  `///` XML doc comments. Regenerate locally with `tools/gen_api_docs.sh`;
  the generated tree is gitignored.
