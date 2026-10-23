#!/usr/bin/env bash
# Builds VoiceType.app for Apple silicon from the Skia desktop head and packs it as .dmg and .zip (runs on a Mac).
#
#   build-macos-app.sh [version]        version: CFBundleShortVersionString, e.g. 1.9.2 (default 1.0.0)
#
# PUBLISH_DIR: an existing `dotnet publish -f net10.0-desktop -r osx-arm64 --self-contained` output (published here
# when it has no VoiceType.Uno). Output: build/macos-packages/{VoiceType.Uno-macos-arm64.dmg,.zip,README.md,SHA256SUMS}.
#
# The bundle: the published files in Contents/MacOS (the .NET host looks for them next to the executable), its folders
# in Contents/Resources linked back into MacOS, the voicetype-audio capture helper compiled from
# Packaging/macOS/voicetype-audio.swift, Info.plist and the icon. There is no Apple Developer ID yet, so everything is
# signed ad hoc (Gatekeeper asks the user once, README.md).
set -euo pipefail

VERSION="${1:-1.0.0}"
if [[ ! "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]]; then
    echo "Invalid app version: $VERSION (expected 1.2 or 1.2.3)" >&2
    exit 2
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$(cd "$PROJECT_DIR/../../../.." && pwd)"
PACKAGING_DIR="$PROJECT_DIR/Packaging/macOS"
PUBLISH_DIR="${PUBLISH_DIR:-$WORKSPACE/build/voicetype-uno-osx-arm64}"
OUTPUT_DIR="$WORKSPACE/build/macos-packages"
NAME="VoiceType.Uno-macos-arm64"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
APP="$STAGING/VoiceType.app"

if [[ ! -x "$PUBLISH_DIR/VoiceType.Uno" ]]; then
    rm -rf "$PUBLISH_DIR"
    dotnet publish "$PROJECT_DIR/VoiceType.Uno.csproj" \
        -c Release \
        -f net10.0-desktop \
        -r osx-arm64 \
        -p:ApplicationDisplayVersion="$VERSION" \
        --self-contained true \
        -o "$PUBLISH_DIR"
fi
test -x "$PUBLISH_DIR/VoiceType.Uno"

rm -rf "$OUTPUT_DIR"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$OUTPUT_DIR"
cp -a "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
# Debug symbols are not shipped (and would only add signatures).
find "$APP/Contents/MacOS" -name '*.pdb' -delete

echo "Compiling the audio helper"
swiftc -O -swift-version 5 -target arm64-apple-macos14.0 \
    -o "$APP/Contents/MacOS/voicetype-audio" "$PACKAGING_DIR/voicetype-audio.swift"
"$APP/Contents/MacOS/voicetype-audio" check

sed "s/@VERSION@/$VERSION/g" "$PACKAGING_DIR/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "Building the icon"
ICONSET="$STAGING/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$PACKAGING_DIR/AppIcon.png" --out "$ICONSET/icon_${size}x${size}.png" > /dev/null
    sips -z $((size * 2)) $((size * 2)) "$PACKAGING_DIR/AppIcon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# codesign treats every folder in Contents/MacOS as nested code (Uno.Fonts.OpenSans would have to be a bundle), so the
# folders (Assets, fonts) move to Contents/Resources and are linked back. The files stay: the .NET host resolves the
# app assembly to its real path and looks for libhostpolicy.dylib next to it, so a linked VoiceType.Uno.dll fails.
echo "Moving the folders to Resources"
for item in "$APP/Contents/MacOS"/*; do
    if [[ -d "$item" ]]; then
        name="$(basename "$item")"
        mv "$item" "$APP/Contents/Resources/$name"
        ln -s "../Resources/$name" "$APP/Contents/MacOS/$name"
    fi
done
# Inside a bundle the Uno macOS host reads ms-appx:/// content (the icon font Uno.Fonts.Fluent, Open Sans, Assets) from
# "<folder of the app assembly>/Resources" = Contents/MacOS/Resources (MacOSHost: StorageFile.ResourcePathBase); without
# this link every icon is drawn as a "?".
ln -s ../Resources "$APP/Contents/MacOS/Resources"

# Ad-hoc signature, inside out: every file in Contents/MacOS (a non-Mach-O file keeps its signature in extended
# attributes, which Finder, ditto and disk images preserve), then the bundle, which seals Info.plist and Resources.
echo "Signing (ad hoc)"
find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/VoiceType.Uno" -print0 |
    xargs -0 -n 50 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --strict --verbose=2 "$APP"

echo "Packing"
ditto -c -k --keepParent "$APP" "$OUTPUT_DIR/$NAME.zip"
# A disk image with an Applications link for drag-and-drop; hdiutil on hosted runners fails now and then ("Resource busy").
DMG_ROOT="$STAGING/dmg"
mkdir -p "$DMG_ROOT"
ditto "$APP" "$DMG_ROOT/VoiceType.app"
ln -s /Applications "$DMG_ROOT/Applications"
for attempt in 1 2 3; do
    if hdiutil create -volname VoiceType -srcfolder "$DMG_ROOT" -fs HFS+ -format UDZO -ov "$OUTPUT_DIR/$NAME.dmg"; then
        break
    fi
    [[ $attempt == 3 ]] && { echo "hdiutil failed three times" >&2; exit 1; }
    sleep 5
done

cp "$PACKAGING_DIR/README.md" "$OUTPUT_DIR/README.md"
(
    cd "$OUTPUT_DIR"
    shasum -a 256 "$NAME.dmg" "$NAME.zip" > SHA256SUMS
)
du -sh "$APP" "$OUTPUT_DIR"/*
echo "macOS packages: $OUTPUT_DIR"
