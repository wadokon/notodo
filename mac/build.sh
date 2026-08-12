#!/bin/bash
# NotoDo (macOS版) のビルドスクリプト
# 必要: Xcode Command Line Tools (xcode-select --install)
set -e
cd "$(dirname "$0")"

swiftc -O -parse-as-library -o NotoDo NotoDo.swift

APP=NotoDo.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>NotoDo</string>
    <key>CFBundleIdentifier</key><string>com.wadokon.notodo</string>
    <key>CFBundleExecutable</key><string>NotoDo</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
mv NotoDo "$APP/Contents/MacOS/"
echo "ビルド完了: $APP"
echo "起動: open $APP"
