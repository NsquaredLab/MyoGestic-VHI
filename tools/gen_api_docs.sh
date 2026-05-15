#!/usr/bin/env bash
# Regenerate the C# API reference under docs/reference/api/ from VHI's
# XML doc comments. The generated tree is gitignored - rerun this script
# whenever the source changes, before building the docs site.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/VHI_godot.csproj"
CONFIGURATION="${CONFIGURATION:-Debug}"
OUT="$ROOT/docs/reference/api"

cd "$ROOT"

echo "▶ restoring local .NET tools"
dotnet tool restore >/dev/null

echo "▶ building $CONFIGURATION (with XML doc generation)"
dotnet build "$PROJECT" -c "$CONFIGURATION" -v q \
  /p:GenerateDocumentationFile=true \
  /p:NoWarn=1591

# Locate the built assembly + its XML doc file via MSBuild.
ASSEMBLY="$(dotnet msbuild "$PROJECT" -nologo -v:q \
            -getProperty:TargetPath \
            -p:Configuration="$CONFIGURATION" | tr -d '\r\n')"
[[ "$ASSEMBLY" = /* ]] || ASSEMBLY="$ROOT/$ASSEMBLY"
XML="${ASSEMBLY%.dll}.xml"

if [[ ! -f "$ASSEMBLY" ]]; then
  echo "✗ assembly not found at $ASSEMBLY" >&2
  exit 1
fi
if [[ ! -f "$XML" ]]; then
  echo "✗ XML doc file not found at $XML" >&2
  exit 1
fi

echo "▶ regenerating $OUT"
rm -rf "$OUT"
mkdir -p "$OUT"

dotnet tool run defaultdocumentation -- \
  --AssemblyFilePath "$ASSEMBLY" \
  --DocumentationFilePath "$XML" \
  --OutputDirectoryPath "$OUT" \
  --AssemblyPageName index \
  --GeneratedAccessModifiers Public \
  --GeneratedPages Assembly,Types \
  --IncludeUndocumentedItems false \
  --LogLevel Warning

# Strip Godot.NET.Sdk's auto-generated type-safe accessor nested types
# (MethodName, PropertyName, SignalName). They're real public types, but
# they're noise in narrative API docs - the parent class page already lists
# every method/property/signal that matters.
echo "▶ pruning Godot autogen nested-type pages"
rm -f "$OUT"/Vhi.*.MethodName.md \
      "$OUT"/Vhi.*.PropertyName.md \
      "$OUT"/Vhi.*.SignalName.md
# And remove their lingering markdown links from the surviving pages. We use
# perl here (rather than `sed -i ''`) so the script runs on Linux CI too -
# BSD sed wants the empty-string arg, GNU sed treats it as a filename.
find "$OUT" -name '*.md' -exec perl -i -ne \
  'print unless m{Vhi\.[^\s]+\.(MethodName|PropertyName|SignalName)\.md}' {} +

# DefaultDocumentation sends every external type to learn.microsoft.com, which
# means Godot.* types (Godot.Label, Godot.Node3D, ...) hit dead pages. Rewrite
# those to godotengine.org's own class reference. Special-case GodotObject ->
# class_object.html (Godot's native class is plain "Object"). The negative
# lookahead skips nested namespaces like Godot.Collections.Array.
echo "▶ rewriting Godot.* external links to godotengine.org"
find "$OUT" -name '*.md' -exec perl -pi -e '
  s{https://learn\.microsoft\.com/en-us/dotnet/api/godot\.godotobject\b}
   {https://docs.godotengine.org/en/stable/classes/class_object.html}gi;
  s{https://learn\.microsoft\.com/en-us/dotnet/api/godot\.([a-z0-9_]+)(?![a-z0-9_.])}
   {https://docs.godotengine.org/en/stable/classes/class_$1.html}gix;
' {} +

# Strip Godot lifecycle method sections (_Ready / _Process / _PhysicsProcess
# / _ExitTree etc.) - they're framework callbacks inherited from Node and
# not VHI's public API.
echo "▶ stripping Godot lifecycle method sections"
python3 "$ROOT/tools/strip_godot_lifecycle.py" "$OUT"/*.md

# Strip protobuf-generated boilerplate (WriteTo/MergeFrom/CalculateSize/Equals/
# GetHashCode/Clone/ToString/Parser/Descriptor/*FieldNumber/constructors) from
# the Myogestic.Vhi.V1.* pages. The user-facing message fields stay.
echo "▶ stripping protobuf boilerplate from Myogestic.Vhi.V1.* pages"
python3 "$ROOT/tools/strip_protobuf_boilerplate.py" "$OUT"/Myogestic.Vhi.V1.*.md

# The MyogesticVhiReflection helper is protobuf reflection internals, not a
# user-facing type - drop the whole page and scrub its index entry.
rm -f "$OUT/Myogestic.Vhi.V1.MyogesticVhiReflection.md"
find "$OUT" -name '*.md' -exec perl -i -ne \
  'print unless m{Myogestic\.Vhi\.V1\.MyogesticVhiReflection\.md}' {} +

# Strip "Parameters" subsections that have no <param> description prose - they
# just repeat the signature. Methods that DO have <param> tags keep their
# sections intact.
echo "▶ stripping empty Parameters sections"
python3 "$ROOT/tools/strip_empty_param_sections.py" "$OUT"/*.md

# Inject a "what's this" admonition above the namespace listing in index.md
# so the protobuf-generated Myogestic.Vhi.V1.* namespace has context.
PRELUDE="$ROOT/tools/api_index_prelude.md"
if [[ -f "$PRELUDE" ]]; then
  awk -v insert_file="$PRELUDE" '
    /^### Namespaces$/ {
      # blank line so the preceding "## VHI_godot Assembly" heading closes
      # cleanly before the admonition starts (otherwise it is parsed as the
      # heading'"'"'s trailing text and the !!! block never renders).
      print ""
      while ((getline line < insert_file) > 0) print line
      close(insert_file)
    }
    { print }
  ' "$OUT/index.md" > "$OUT/index.md.tmp" && mv "$OUT/index.md.tmp" "$OUT/index.md"
fi

echo "✓ API reference written to $OUT"
