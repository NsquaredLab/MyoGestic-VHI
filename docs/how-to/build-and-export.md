# Build and export

Running VHI from the Godot editor (or `godot --path .`) needs nothing special.
Producing a **standalone export** has two real gotchas - both covered here.

## Prerequisite: an active .NET 8 SDK

VHI targets `net8.0`. Godot's editor-integrated publish (`GodotTools`) needs a
discoverable **.NET 8 SDK** - if your `dotnet` on `PATH` is a newer major
version, the export fails with a `System.Runtime` load error before it even
starts.

Make .NET 8 the active SDK for the export - either put it first on `PATH`, or
pin it with a `global.json` in the repo:

```json
{
  "sdk": { "version": "8.0.0", "rollForward": "latestMinor" }
}
```

`dotnet --version` run in the repo should report an `8.0.x`.

## Export

`export_presets.cfg` defines presets for **macOS**, **Windows Desktop**,
**Linux** and **Android** - pass the preset name exactly:

```bash
godot --headless --export-release "macOS"           VHI.app
godot --headless --export-release "Windows Desktop" VHI.exe
godot --headless --export-release "Linux"           VHI.x86_64
```

A working export bundles the .NET runtime, all managed assemblies, **and the
ASP.NET Core shared framework** (Kestrel, the `Microsoft.Extensions.*` and
`Microsoft.AspNetCore.*` assemblies) that VHI's [gRPC server](../concepts/grpc-control.md)
needs.

## macOS: re-sign without the hardened runtime

This is the one that bites. Godot ad-hoc-signs the macOS `.app` **with the
hardened runtime enabled**. Under the hardened runtime, Godot's *embedded*
.NET host fails to bring up the runtime - silently. The window opens, the
Godot engine runs, but **no C# executes**: no gRPC server, no hand logic, no
output.

The fix is to re-sign the bundle ad-hoc **without** the hardened runtime:

```bash
codesign --force --deep --sign - VHI.app
```

After that the exported app's .NET side starts normally - the gRPC server
comes up and the hands work. Verify by checking the signature flags went from
`adhoc,runtime` to just `adhoc`:

```bash
codesign -dv VHI.app 2>&1 | grep flags
```

!!! note "For real distribution"
    Ad-hoc re-signing is fine for lab machines. To hand the build to other
    people without Gatekeeper friction, sign *all* nested code with a
    Developer ID certificate and notarize the app - the hardened-runtime
    incompatibility is specifically with *ad-hoc* signing.

## Why this happens

Godot doesn't bundle a .NET runtime - it loads one through its own managed
host, which doesn't follow the standard .NET shared-framework probing rules
([godotengine/godot#112701](https://github.com/godotengine/godot/issues/112701)).
VHI works around the *loading* with
[`SharedFrameworkAssemblyLoader`](../concepts/architecture.md#the-net-hosting-workaround);
the hardened-runtime *signing* incompatibility is a separate, macOS-specific
issue on top of that.

## Building the docs site (CI)

The docs site has one prerequisite a fresh checkout has to satisfy: the
[C# API reference](../reference/api/index.md) is generated from the compiled
assembly at build time and the `docs/reference/api/` tree is gitignored, so
`properdocs build` will fail until it exists. The full sequence:

```bash
dotnet tool restore                  # installs DefaultDocumentation.Console
./tools/gen_api_docs.sh              # builds the assembly, regenerates the API md
uv run --group docs properdocs build # the docs site itself
```

A CI runner needs the **.NET 8 SDK** (as for export), `uv`, and Python 3
(used by the API-doc post-process scripts under `tools/`). For local
authoring `uv run --group docs properdocs serve` watches `docs/` and the
generated API tree both - re-run `./tools/gen_api_docs.sh` whenever a `///`
comment in `src/` or a comment in `proto/myogestic_vhi.proto` changes.
