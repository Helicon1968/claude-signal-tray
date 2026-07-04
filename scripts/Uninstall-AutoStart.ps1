# Install-AutoStart.ps1 で作成したスタートアップ用ショートカットを削除するスクリプト。
# 使い方: powershell -ExecutionPolicy Bypass -File .\scripts\Uninstall-AutoStart.ps1

$startupDir = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startupDir "ClaudeSignalTray.lnk"

if (-not (Test-Path $shortcutPath)) {
    Write-Host "ショートカットが見つかりません（$shortcutPath）。何もしませんでした。"
    exit 0
}

Remove-Item -Path $shortcutPath -Force
Write-Host "自動起動用ショートカットを削除しました（$shortcutPath）。"
