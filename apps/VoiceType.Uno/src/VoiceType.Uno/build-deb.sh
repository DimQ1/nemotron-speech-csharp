#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-1.0.0}"
if [[ ! "$VERSION" =~ ^[0-9][0-9A-Za-z.+:~-]*$ ]]; then
    echo "Invalid Debian package version: $VERSION" >&2
    exit 2
fi

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE="$(cd "$PROJECT_DIR/../../../.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-$WORKSPACE/build/voicetype-uno-linux-x64}"
STAGING_ROOT="$(mktemp -d)"
trap 'rm -rf "$STAGING_ROOT"' EXIT
STAGING_DIR="$STAGING_ROOT/voicetype-uno_${VERSION}_amd64"
OUTPUT_DIR="$WORKSPACE/build/linux-packages"
OUTPUT_FILE="$OUTPUT_DIR/voicetype-uno_${VERSION}_amd64.deb"
PACKAGING_DIR="$PROJECT_DIR/Packaging/Linux"
INSTALL_DIR="$STAGING_DIR/opt/voicetype-uno"

rm -rf "$OUTPUT_DIR"
mkdir -p \
    "$INSTALL_DIR" \
    "$STAGING_DIR/DEBIAN" \
    "$STAGING_DIR/usr/bin" \
    "$STAGING_DIR/usr/share/applications" \
    "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps" \
    "$OUTPUT_DIR"

if [[ ! -x "$PUBLISH_DIR/VoiceType.Uno" ]]; then
    rm -rf "$PUBLISH_DIR"
    dotnet publish "$PROJECT_DIR/VoiceType.Uno.csproj" \
        -c Release \
        -r linux-x64 \
        -f net10.0-desktop \
        -p:GpuArch=CPU \
        --self-contained true \
        -o "$PUBLISH_DIR"
fi

test -x "$PUBLISH_DIR/VoiceType.Uno"
cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"
install -m 0755 "$PROJECT_DIR/launch-linux.sh" "$INSTALL_DIR/launch-linux.sh"
install -m 0755 "$WORKSPACE/tools/scripts/x11-window-fixer.py" "$INSTALL_DIR/x11-window-fixer.py"
install -m 0755 "$PACKAGING_DIR/voicetype-uno" "$STAGING_DIR/usr/bin/voicetype-uno"
install -m 0644 "$PACKAGING_DIR/voicetype-uno.desktop" "$STAGING_DIR/usr/share/applications/voicetype-uno.desktop"
install -m 0644 "$PROJECT_DIR/Assets/Icons/icon.svg" "$STAGING_DIR/usr/share/icons/hicolor/scalable/apps/voicetype-uno.svg"
{
    sed "s/@VERSION@/$VERSION/g" "$PACKAGING_DIR/control"
    echo
} > "$STAGING_DIR/DEBIAN/control"

find "$STAGING_DIR" -type d -exec chmod 0755 {} +
find "$STAGING_DIR" -type f -exec chmod 0644 {} +
chmod 0755 \
    "$INSTALL_DIR/VoiceType.Uno" \
    "$INSTALL_DIR/launch-linux.sh" \
    "$INSTALL_DIR/x11-window-fixer.py" \
    "$STAGING_DIR/usr/bin/voicetype-uno"

dpkg-deb --build --root-owner-group "$STAGING_DIR" "$OUTPUT_FILE"
cp "$PACKAGING_DIR/README.md" "$OUTPUT_DIR/README.md"
(
    cd "$OUTPUT_DIR"
    sha256sum "$(basename "$OUTPUT_FILE")" > SHA256SUMS
)

dpkg-deb --info "$OUTPUT_FILE"
echo "Debian artifact: $OUTPUT_FILE"