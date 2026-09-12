# Build and export

Building VHI's C# needs a **.NET 8 SDK** (below). Producing a **standalone export**
has two further gotchas — all three are covered here.

## Prerequisite: a .NET 8 SDK

VHI targets `net8.0`, and `global.json` **is already committed** pinning that:

```json
{
  "sdk": { "version": "8.0.0", "rollForward": "latestFeature" }
}
```

`latestFeature` rolls forward within the `8.0.x` feature band only — deliberately, so
the build cannot drift onto a newer major without someone deciding to. You do not need
to create this file, and you should not edit it.

The consequence is worth being explicit about: **a machine with only a newer SDK cannot
build this project at all** — not just the export. `dotnet build` fails during SDK
resolution, before compiling anything:

```
A compatible .NET SDK was not found.
Requested SDK version: 8.0.0
global.json file: …/Virtual-Hand-Interface/global.json
```

### Installing .NET 8 side by side

Install it *alongside* whatever you already have rather than relaxing the pin. The
official installer places a self-contained SDK under `~/.dotnet` and needs no
administrator rights:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 8.0          # installs into ~/.dotnet
```

`~/.dotnet` is not added to `PATH`, so invoke that muxer explicitly — a `dotnet` from a
package manager resolves only its own SDKs and will still fail:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
"$HOME/.dotnet/dotnet" --version                        # → 8.0.x, run inside the repo
"$HOME/.dotnet/dotnet" build VHI_godot.csproj           # compile without the editor
```

Verify by running `dotnet --version` **inside the repo**: it should report an `8.0.x`,
because that is `global.json` being honoured. Outside the repo the same command may
legitimately report something newer.

!!! warning "Do not relax the pin to work around a missing SDK"
    Setting `rollForward: latestMajor` makes the build succeed on a newer SDK, and it
    will appear to work. It also changes the toolchain for everyone and for CI, where
    `.github/workflows/release.yml` provisions `8.0.x` explicitly. A newer compiler also
    reports different diagnostics — building under the pinned SDK caught a
    partially-documented parameter list that a newer one had let through silently.

    Install the SDK the project asks for instead.

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

## Godot runs the *system* `dotnet`, not the one on your `PATH`

On macOS the exporter looks for `/usr/local/share/dotnet/dotnet` before anything on
`PATH`, and that muxer only sees the SDKs installed under its own root. With a .NET 8 SDK
in `~/.dotnet` and a newer one in `/usr/local/share/dotnet`, the export reports
`dotnet publish exited with code: 155` and produces a bundle with **no**
`data_VHI_godot_macos_*` folders, so the app opens with no C# running (2026-09-12).

Two ways out. Install the .NET 8 SDK into the system root as well
(`sudo bash dotnet-install.sh --channel 8.0 --install-dir /usr/local/share/dotnet`), or
publish by hand and assemble the bundle yourself, which is exactly what the exporter does:

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
for rid in osx-arm64 osx-x64; do
  dotnet publish VHI_godot.csproj -c ExportRelease -r $rid --self-contained \
    -p:GodotTargetPlatform=macos -p:DebugType=None -p:DebugSymbols=false -o /tmp/pub-$rid
done
cp -R /tmp/pub-osx-arm64 VHI.app/Contents/Resources/data_VHI_godot_macos_arm64
cp -R /tmp/pub-osx-x64   VHI.app/Contents/Resources/data_VHI_godot_macos_x86_64
```

then re-sign as below.

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
comment in `src/` or a comment in `proto/remote_control.proto` changes.
