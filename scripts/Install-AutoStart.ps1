# claude-signal-tray をWindowsログオン時に自動起動させるスクリプト。
# 管理者権限は不要です。ユーザー個別の「スタートアップ」フォルダに
# ショートカット(.lnk)を作成する方式のため、Task Scheduler の権限問題
# (環境によっては会社ポリシーやセキュリティソフトでアクセス拒否になることがある)
# を回避できる。
#
# 使い方:
#   1. 先に `dotnet publish -c Release` でexeをビルドしておく
#   2. このスクリプトをそのまま実行する（既定のビルド出力パスを自動で探します）
#      powershell -ExecutionPolicy Bypass -File .\scripts\Install-AutoStart.ps1
#   3. 別の場所に置いたexeを登録したい場合は -ExePath で指定する
#      powershell -ExecutionPolicy Bypass -File .\scripts\Install-AutoStart.ps1 -ExePath "C:\tools\ClaudeSignalTray.exe"

param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows\win-x64\publish\ClaudeSignalTray.exe")
)

$resolved = Resolve-Path -Path $ExePath -ErrorAction SilentlyContinue
if (-not $resolved) {
    Write-Error "exeが見つかりません: $ExePath`nまず 'dotnet publish -c Release' を実行するか、-ExePath で場所を指定してください。"
    exit 1
}
$ExePath = $resolved.Path

$startupDir = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startupDir "ClaudeSignalTray.lnk"

try {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $ExePath
    $shortcut.WorkingDirectory = Split-Path -Path $ExePath -Parent
    $shortcut.Description = "claude-signal-tray"
    $shortcut.Save()
}
catch {
    Write-Error "ショートカットの作成に失敗しました: $($_.Exception.Message)"
    exit 1
}

Write-Host "登録しました。"
Write-Host "  ショートカット: $shortcutPath"
Write-Host "  実行ファイル: $ExePath"
Write-Host "  トリガー: 次回以降のログオン時（今すぐ動かしたい場合は手動でexeを実行してください）"
Write-Host ""
Write-Host "確認・削除するには:"
Write-Host "  エクスプローラーで「$startupDir」を開いて ClaudeSignalTray.lnk を確認/削除"
Write-Host "  または .\scripts\Uninstall-AutoStart.ps1 を実行"
