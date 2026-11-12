#!/bin/sh
set -eu

usage() {
    printf 'Usage: sh scripts/build-app.sh [arm64|x86_64|--release]\n'
}

if [ "$#" -gt 1 ]; then
    usage >&2
    exit 64
fi
MODE="${1:-host}"
case "$MODE" in
    host|arm64|x86_64|--release) ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unsupported architecture or mode: %s\n' "$MODE" >&2; usage >&2; exit 64 ;;
esac

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P)"
if [ -L "$ROOT/dist" ]; then
    printf 'The distribution directory must not be a symbolic link.\n' >&2
    exit 1
fi
cd "$ROOT"
plutil -lint "$ROOT/Support/Info.plist"
ICON_NAME="$(plutil -extract CFBundleIconFile raw -o - "$ROOT/Support/Info.plist")"
if [ "$ICON_NAME" != "AppIcon" ] || [ ! -s "$ROOT/Support/$ICON_NAME.icns" ] || [ -L "$ROOT/Support/$ICON_NAME.icns" ]; then
    printf 'The checked-in AppIcon.icns resource is missing or invalid.\n' >&2
    exit 1
fi
VERSION="$(plutil -extract CFBundleShortVersionString raw -o - "$ROOT/Support/Info.plist")"
if ! printf '%s\n' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    printf 'The bundle release version must have the form major.minor.patch.\n' >&2
    exit 1
fi
mkdir -p "$ROOT/dist"

build_app() (
    ARCH="$1"
    APP_DIR="$2"
    case "$ARCH" in
        arm64|x86_64) ;;
        *) printf 'Unsupported host architecture: %s\n' "$ARCH" >&2; exit 1 ;;
    esac
    if [ -L "$(dirname -- "$APP_DIR")" ] || [ -L "$APP_DIR" ]; then
        printf 'The app output must not be a symbolic link.\n' >&2
        exit 1
    fi
    if [ -e "$APP_DIR" ]; then
        if [ ! -d "$APP_DIR" ]; then
            printf 'The app output is not a directory.\n' >&2
            exit 1
        fi
        LINKS="$(find "$APP_DIR" -type l -print)"
        if [ -n "$LINKS" ]; then
            printf 'The existing app output contains symbolic links.\n' >&2
            exit 1
        fi
        FILES="$(find "$APP_DIR" -type f -print)"
        while IFS= read -r FILE; do
            case "$FILE" in
                ""|"$APP_DIR/Contents/MacOS/ReviewQ"|"$APP_DIR/Contents/Info.plist"|"$APP_DIR/Contents/Resources/AppIcon.icns"|"$APP_DIR/Contents/_CodeSignature/CodeResources") ;;
                *) printf 'The existing app output contains unexpected files; refusing to package them.\n' >&2; exit 1 ;;
            esac
        done <<EOF
$FILES
EOF
    fi
    PROCESSES="$(ps -axo comm=)"
    if printf '%s\n' "$PROCESSES" | grep -Fxq "$APP_DIR/Contents/MacOS/ReviewQ"; then
        printf 'Quit ReviewQ before rebuilding this app output.\n' >&2
        exit 1
    fi
    swift build -c release --arch "$ARCH" --product ReviewQ
    BIN_DIR="$(swift build -c release --arch "$ARCH" --show-bin-path)"
    mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
    # A new inode avoids stale code-signing pages from a previously launched app.
    install -S -m 755 "$BIN_DIR/ReviewQ" "$APP_DIR/Contents/MacOS/ReviewQ"
    cp "$ROOT/Support/Info.plist" "$APP_DIR/Contents/Info.plist"
    install -S -m 644 "$ROOT/Support/$ICON_NAME.icns" "$APP_DIR/Contents/Resources/$ICON_NAME.icns"
    ACTUAL_ARCH="$(lipo -archs "$APP_DIR/Contents/MacOS/ReviewQ")"
    if [ "$ACTUAL_ARCH" != "$ARCH" ]; then
        printf 'Expected %s executable, received %s.\n' "$ARCH" "$ACTUAL_ARCH" >&2
        exit 1
    fi
    codesign --force --sign - --options runtime "$APP_DIR"
    codesign --verify --strict "$APP_DIR"
    # Existing bundles need a new timestamp to invalidate cached launcher icons.
    touch "$APP_DIR"
    printf '\nBuilt %s (%s)\n' "$APP_DIR" "$ARCH"
)

case "$MODE" in
    host)
        build_app "$(uname -m)" "$ROOT/dist/ReviewQ.app"
        printf 'Launch with: open "%s/dist/ReviewQ.app"\n' "$ROOT"
        ;;
    arm64|x86_64)
        build_app "$MODE" "$ROOT/dist/$MODE/ReviewQ.app"
        ;;
    --release)
        for ARCH in arm64 x86_64; do
            build_app "$ARCH" "$ROOT/dist/$ARCH/ReviewQ.app"
            ZIP="$ROOT/dist/ReviewQ-$VERSION-macos-$ARCH.zip"
            if [ -L "$ZIP" ]; then
                printf 'The release archive must not be a symbolic link.\n' >&2
                exit 1
            fi
            ditto -c -k --sequesterRsrc --keepParent "$ROOT/dist/$ARCH/ReviewQ.app" "$ZIP"
        done
        if [ -L "$ROOT/dist/SHA256SUMS.txt" ]; then
            printf 'The checksum output must not be a symbolic link.\n' >&2
            exit 1
        fi
        (
            cd "$ROOT/dist"
            shasum -a 256 "ReviewQ-$VERSION-macos-arm64.zip" "ReviewQ-$VERSION-macos-x86_64.zip" > SHA256SUMS.txt
        )
        printf '\nCreated both version %s ZIPs and SHA256SUMS.txt in dist/.\n' "$VERSION"
        printf 'These builds are ad-hoc signed, not Developer ID signed or Apple-notarized.\n'
        ;;
esac
