#!/usr/bin/env bash
# 打包、安装到 /Applications，并在桌面创建快捷方式
set -euo pipefail

ck3_project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ck3_project_root"

ck3_app_name="Steam Mod Manager"
ck3_built="artifacts/macos-app/$ck3_app_name.app"
ck3_target="/Applications/$ck3_app_name.app"
ck3_backups="artifacts/app-install-backups"

bash scripts/package-macos-app.sh

if [[ -e "$ck3_target" ]]; then
    mkdir -p "$ck3_backups"
    ck3_stamp="$(date +%Y%m%d-%H%M%S)"
    ck3_backup="$ck3_backups/$ck3_app_name-before-launcher-fix-$ck3_stamp.app"
    echo "备份现有应用到 $ck3_backup"
    mv "$ck3_target" "$ck3_backup"
fi

echo "安装到 $ck3_target"
cp -R "$ck3_built" "$ck3_target"

/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f "$ck3_target" >/dev/null 2>&1 || true

ck3_desktop_link="$HOME/Desktop/$ck3_app_name"
rm -f "$ck3_desktop_link"
ln -s "$ck3_target" "$ck3_desktop_link"
echo "桌面快捷方式：$ck3_desktop_link"

open "$ck3_target"
echo "已启动"
