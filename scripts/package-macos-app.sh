#!/usr/bin/env bash
# 把桌面项目打包成可双击启动的 macOS .app（自包含，不依赖本机 .NET）
set -euo pipefail

ck3_project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ck3_project_root"

ck3_dotnet="${HOME}/.local/share/ck3-mod-manager/dotnet/dotnet"
if [[ ! -x "$ck3_dotnet" ]]; then
    ck3_dotnet="$(command -v dotnet || true)"
fi
if [[ -z "$ck3_dotnet" || ! -x "$ck3_dotnet" ]]; then
    echo "找不到 .NET 10 SDK，请先安装：https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
fi

ck3_out="artifacts/macos-app"
ck3_app="$ck3_out/Steam Mod Manager.app"
ck3_icons="artifacts/app-icon"

echo "1/4 生成图标…"
mkdir -p "$ck3_icons"
swift scripts/make-app-icon.swift "$ck3_icons/icon-1024.png"

echo "2/4 发布应用…"
rm -rf "$ck3_out/publish"
"$ck3_dotnet" publish src/Ck3ModManager.Desktop/Ck3ModManager.Desktop.csproj \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=false -p:UsedAvaloniaProducts= \
    -o "$ck3_out/publish" --nologo

echo "3/4 生成 icns…"
ck3_iconset="$ck3_icons/AppIcon.iconset"
rm -rf "$ck3_iconset"
mkdir -p "$ck3_iconset"
for entry in "16:16x16" "32:16x16@2x" "32:32x32" "64:32x32@2x" "128:128x128" \
             "256:128x128@2x" "256:256x256" "512:256x256@2x" "512:512x512" "1024:512x512@2x"; do
    ck3_size="${entry%%:*}"
    ck3_name="${entry##*:}"
    sips -z "$ck3_size" "$ck3_size" "$ck3_icons/icon-1024.png" \
        --out "$ck3_iconset/icon_${ck3_name}.png" >/dev/null
done
iconutil -c icns "$ck3_iconset" -o "$ck3_icons/AppIcon.icns"

echo "4/4 组装 .app…"
rm -rf "$ck3_app"
mkdir -p "$ck3_app/Contents/MacOS" "$ck3_app/Contents/Resources"
cp -R "$ck3_out/publish/." "$ck3_app/Contents/MacOS/"
cp "$ck3_icons/AppIcon.icns" "$ck3_app/Contents/Resources/AppIcon.icns"

cat > "$ck3_app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Steam Mod Manager</string>
    <key>CFBundleDisplayName</key><string>Steam Mod Manager</string>
    <key>CFBundleIdentifier</key><string>com.zxxwyt.gamemodmanager</string>
    <key>CFBundleVersion</key><string>1.0.0</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>Ck3ModManager.Desktop</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

chmod +x "$ck3_app/Contents/MacOS/Ck3ModManager.Desktop"

echo "完成：$ck3_app"
echo "把 Steam Mod Manager.app 拖到「应用程序」或桌面即可双击启动。"
