# ClaudeSignalTray.ps1
# Claude Cowork の作業状況をタスクトレイの信号アイコンで表示する、非公式の常駐スクリプト。
# Anthropic / Claude とは無関係の個人プロジェクトです。
# %LOCALAPPDATA%\ClaudeSignalTray\status.json を定期的に読み取り、内容に応じてアイコンの色を切り替える。
#
# 使い方:
#   powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ClaudeSignalTray.ps1
#
# status.json の形式:
#   { "status": 0, "task": "タスク名", "updated_at": "2026-07-02T19:45:00+09:00" }
#   status: 0 = 待機中/完了(緑)  1 = 作業中(黄)  2 = 確認待ち(赤・点滅)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$StatusPath   = "$env:LOCALAPPDATA\ClaudeSignalTray\status.json"
$StaleSeconds = 180   # この秒数以上更新が無ければ「不明(灰)」扱いにする
$PollMs       = 1000  # ファイルの再読み込み間隔
$BlinkMs      = 600   # 確認待ち時の点滅間隔

# ---- アイコン生成 (色付きの丸を動的に描画してICON化) ----
function New-CircleIcon {
    param([System.Drawing.Color]$Color, [bool]$Bright = $false)

    $bmp = New-Object System.Drawing.Bitmap 32,32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $brush = New-Object System.Drawing.SolidBrush $Color
    $g.FillEllipse($brush, 2, 2, 28, 28)

    $penColor = if ($Bright) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(60,60,60) }
    $pen = New-Object System.Drawing.Pen $penColor, 2
    $g.DrawEllipse($pen, 2, 2, 28, 28)

    $g.Dispose()
    $hIcon = $bmp.GetHicon()
    return [System.Drawing.Icon]::FromHandle($hIcon)
}

$IconGreen  = New-CircleIcon ([System.Drawing.Color]::FromArgb(46,160,67))
$IconYellow = New-CircleIcon ([System.Drawing.Color]::FromArgb(230,175,15))
$IconRedA   = New-CircleIcon ([System.Drawing.Color]::FromArgb(220,40,40))
$IconRedB   = New-CircleIcon ([System.Drawing.Color]::FromArgb(255,90,90)) -Bright $true
$IconGray   = New-CircleIcon ([System.Drawing.Color]::FromArgb(140,140,140))

# ---- トレイアイコン & メニュー ----
$notifyIcon = New-Object System.Windows.Forms.NotifyIcon
$notifyIcon.Icon = $IconGray
$notifyIcon.Text = "claude-signal-tray: 起動中..."
$notifyIcon.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip

$itemStatus = $menu.Items.Add("状態: 取得中...")
$itemStatus.Enabled = $false

$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null

$itemOpenFolder = $menu.Items.Add("状態ファイルのフォルダを開く")
$itemOpenFolder.Add_Click({ Start-Process explorer.exe (Split-Path $StatusPath -Parent) })

$itemReload = $menu.Items.Add("今すぐ再読み込み")
$itemReload.Add_Click({ Update-Status })

$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)) | Out-Null

$itemExit = $menu.Items.Add("終了")
$itemExit.Add_Click({
    $notifyIcon.Visible = $false
    [System.Windows.Forms.Application]::Exit()
})

$notifyIcon.ContextMenuStrip = $menu

# ---- 状態管理 ----
$script:CurrentStatus = -1
$script:BlinkOn = $false
$script:LastGoodRead = $null

function Update-Status {
    $now = Get-Date
    if (-not (Test-Path $StatusPath)) {
        $notifyIcon.Icon = $IconGray
        $notifyIcon.Text = "claude-signal-tray: 状態ファイルが見つかりません"
        $itemStatus.Text = "状態ファイル未作成"
        $script:CurrentStatus = -1
        return
    }

    try {
        $raw = Get-Content -Path $StatusPath -Raw -ErrorAction Stop
        $data = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        # 書き込み中の一時的な読み取り失敗は無視して次回に回す
        return
    }

    $script:LastGoodRead = $now
    $updatedAt = $null
    try { $updatedAt = [DateTime]::Parse($data.updated_at) } catch {}

    $isStale = $false
    if ($updatedAt) {
        $isStale = ($now - $updatedAt).TotalSeconds -gt $StaleSeconds
    }

    $script:CurrentStatus = [int]$data.status
    $taskName = if ($data.task) { $data.task } else { "(タスク名未設定)" }

    if ($isStale) {
        $notifyIcon.Icon = $IconGray
        $notifyIcon.Text = "claude-signal-tray: 更新が止まっています`n最終更新: $updatedAt"
        $itemStatus.Text = "不明(更新停止): $taskName"
        return
    }

    switch ($script:CurrentStatus) {
        0 {
            $notifyIcon.Icon = $IconGreen
            $notifyIcon.Text = "claude-signal-tray: 待機中/完了`n$taskName"
            $itemStatus.Text = "待機中/完了: $taskName"
        }
        1 {
            $notifyIcon.Icon = $IconYellow
            $notifyIcon.Text = "claude-signal-tray: 作業中`n$taskName"
            $itemStatus.Text = "作業中: $taskName"
        }
        2 {
            # 点滅は下の点滅タイマーが担当。ここではテキストのみ更新。
            $notifyIcon.Text = "claude-signal-tray: 確認待ち！`n$taskName"
            $itemStatus.Text = "確認待ち: $taskName"
        }
        default {
            $notifyIcon.Icon = $IconGray
            $notifyIcon.Text = "claude-signal-tray: 不明な状態 ($($script:CurrentStatus))"
            $itemStatus.Text = "不明な状態"
        }
    }
}

# ---- ファイル監視 + ポーリング(取りこぼし防止の二重化) ----
$pollTimer = New-Object System.Windows.Forms.Timer
$pollTimer.Interval = $PollMs
$pollTimer.Add_Tick({ Update-Status })
$pollTimer.Start()

$blinkTimer = New-Object System.Windows.Forms.Timer
$blinkTimer.Interval = $BlinkMs
$blinkTimer.Add_Tick({
    if ($script:CurrentStatus -eq 2) {
        $script:BlinkOn = -not $script:BlinkOn
        $notifyIcon.Icon = if ($script:BlinkOn) { $IconRedB } else { $IconRedA }
    }
})
$blinkTimer.Start()

try {
    $watcher = New-Object System.IO.FileSystemWatcher (Split-Path $StatusPath -Parent), (Split-Path $StatusPath -Leaf)
    $watcher.EnableRaisingEvents = $true
    Register-ObjectEvent -InputObject $watcher -EventName Changed -Action { Update-Status } | Out-Null
    Register-ObjectEvent -InputObject $watcher -EventName Created -Action { Update-Status } | Out-Null
} catch {
    # FileSystemWatcher が使えない環境でもポーリングだけで動作を継続する
}

Update-Status
[System.Windows.Forms.Application]::Run()
