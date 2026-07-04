// claude-signal-tray
// Claude Cowork の作業状況をタスクトレイの信号アイコンで表示する、非公式の常駐アプリ。
// Anthropic / Claude とは無関係の個人プロジェクトです。
//
// 【方式】Claudeプロセスの I/O 量(PerformanceCounterの "IO Data Bytes/sec"、
// ネットワーク+ディスク等の合算値)を継続的にサンプリングし、値の水準と安定度から
// 「アイドル / 確認待ち / 作業中」を推定する。status.json 経由でClaude自身に
// 書き込みを頼る方式(グローバル指示ベース)は、実機検証で全く実行されないことが
// 複数回確認されたため廃止し、本方式に一本化した。
//
// 【判定の根拠】3回の手動検証(experiments/network-probe)で、以下の傾向が
// 一貫して再現された。
//   - 完全アイドル: ほぼ0 (単発の突発スパイクを除く)
//   - AskUserQuestion表示中の確認待ち: 400~2000KB/s程度で分散が小さい安定した帯
//   - 実際に作業中(検索・生成中): 2500KB/sを大きく超え、かつ激しく変動する
// これは1台のマシンでの限定的な検証結果であり、環境によって閾値の調整が
// 必要になる可能性がある(README参照)。
//
// 【追記・実運用での調整】上記はいずれも数分程度の短時間テストの結果だったが、
// 実際に常駐させて長時間の「本当のアイドル」を観測したところ、100~150KB/s程度まで
// 揺れることが分かった。これは当初のアイドル/確認待ちの境界(150KB/s)のすぐ近くだったため、
// 緑/赤が数秒おきにチラつく問題が発生した。対策として、境界を実測ノイズから
// 十分離し、かつヒステリシス(状態ごとに異なる閾値)を導入した(詳細は
// NetworkStateMonitor.Classify 参照)。

using System.Diagnostics;
using System.Text.Json;

namespace ClaudeSignalTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

/// <summary>
/// NetworkStateMonitor の挙動を外部ファイルから調整するための設定値。
/// %LOCALAPPDATA%\ClaudeSignalTray\config.json に保存され、起動時と
/// 右クリックメニューの「設定を再読み込み」時に反映される。
///
/// マシンやネットワーク環境(例: 直接Anthropic APIを使う場合とAWS Bedrock経由の
/// 場合など)によって、実測されるI/O量の水準が異なりうるため、閾値等をソースを
/// 書き換えずに調整できるようにしている。なお本ツールは通信先URL/エンドポイントは
/// 一切見ておらず(TLSで暗号化されたペイロードの中身にもアクセスしない)、
/// プロセス単位のI/O量のみを見ている点に注意。接続先がAPI直結かBedrock経由かで
/// 変わるのは「その環境でのI/O量の傾向」であり、閾値の調整で吸収する想定。
/// </summary>
internal sealed class MonitorConfig
{
    public string ProcessName { get; set; } = "claude";
    public int PollIntervalMs { get; set; } = 700;
    public int RefreshInstancesEveryNTicks { get; set; } = 8; // プロセス一覧の再取得間隔(tick数)
    public int WindowSize { get; set; } = 8;                  // 中央値を取るサンプル数
    public int DebounceTicks { get; set; } = 4;                // 表示を切り替えるのに必要な連続一致回数

    // 閾値(バイト/秒単位)。詳細な経緯・調整根拠はREADME参照。
    public float IdleEnterBytesPerSec { get; set; } = 250_000f;        // 確認待ち→アイドルに戻る条件(これ以下)
    public float ConfirmWaitEnterBytesPerSec { get; set; } = 600_000f; // アイドル→確認待ちになる条件(これ以上)
    public float WorkingThresholdBytesPerSec { get; set; } = 2_500_000f; // これ以上なら「作業中」

    // 常に最前面に表示する小さなオーバーレイウィンドウの設定。
    // OverlayX/OverlayYが-1(未設定)の場合は、画面右端中央付近を既定位置として使う。
    // ドラッグで移動した位置はここに保存され、次回起動時に復元される。
    public bool OverlayEnabled { get; set; }
    public int OverlayX { get; set; } = -1;
    public int OverlayY { get; set; } = -1;

    /// <summary>"Small" または "Large"。不正な値は既定値(Small)にフォールバックする。</summary>
    public string OverlaySize { get; set; } = "Small";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetConfigPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "ClaudeSignalTray", "config.json");
    }

    /// <summary>
    /// 設定ファイルを読み込む。存在しない場合は既定値でファイルを新規作成する。
    /// 壊れている/読み込みに失敗した場合は、ファイルには触れず既定値で動作を継続する
    /// (アプリを落とさないことを優先する)。
    /// </summary>
    public static MonitorConfig LoadOrCreateDefault()
    {
        var path = GetConfigPath();

        if (!File.Exists(path))
        {
            var defaultConfig = new MonitorConfig();
            defaultConfig.TrySave();
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<MonitorConfig>(json, JsonOptions) ?? new MonitorConfig();
            loaded.Sanitize();
            return loaded;
        }
        catch
        {
            return new MonitorConfig();
        }
    }

    public bool TrySave()
    {
        try
        {
            var dir = Path.GetDirectoryName(GetConfigPath())!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 値の整合性が崩れている場合(例: 閾値の大小関係が逆転している等)に、
    /// 明らかにおかしい設定でアプリが機能しなくなるのを防ぐため既定値へ戻す。
    /// </summary>
    private void Sanitize()
    {
        var d = new MonitorConfig();

        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = d.ProcessName;
        if (PollIntervalMs <= 0) PollIntervalMs = d.PollIntervalMs;
        if (WindowSize <= 0) WindowSize = d.WindowSize;
        if (DebounceTicks <= 0) DebounceTicks = d.DebounceTicks;
        if (RefreshInstancesEveryNTicks <= 0) RefreshInstancesEveryNTicks = d.RefreshInstancesEveryNTicks;
        if (OverlaySize != "Small" && OverlaySize != "Large") OverlaySize = d.OverlaySize;

        if (IdleEnterBytesPerSec < 0
            || ConfirmWaitEnterBytesPerSec <= IdleEnterBytesPerSec
            || WorkingThresholdBytesPerSec <= ConfirmWaitEnterBytesPerSec)
        {
            IdleEnterBytesPerSec = d.IdleEnterBytesPerSec;
            ConfirmWaitEnterBytesPerSec = d.ConfirmWaitEnterBytesPerSec;
            WorkingThresholdBytesPerSec = d.WorkingThresholdBytesPerSec;
        }
    }
}

/// <summary>
/// Claudeプロセスの I/O 量をサンプリングし、アイドル(0)/作業中(1)/確認待ち(2)/
/// 不明(-1: プロセスが見つからない)を判定する。
/// </summary>
internal sealed class NetworkStateMonitor
{
    // 実機での連続稼働テストで、「完全アイドル時」の実測値が100〜150KB/s程度まで
    // 揺れることが判明した(当初の想定=ほぼ0、より高い)。単一の閾値(150KB/s)だと
    // ノイズがその値のすぐ近くで頻繁に往復し、緑/赤が数秒おきにチラつく問題が
    // 発生したため、次の2つの対策を行った。
    //   1. アイドル/確認待ちの境界を、実測アイドルノイズの上限から十分離す
    //      (アイドル側の余裕とテスト時に見えた確認待ち帯の下限との中間あたり)
    //   2. アイドル⇔確認待ちの切り替えにヒステリシス(2本の閾値)を導入し、
    //      「今アイドル表示中なら明確に超えるまではアイドルのまま」
    //      「今確認待ち表示中なら明確に下回るまでは確認待ちのまま」とすることで、
    //      境界付近を行き来するノイズを吸収する。
    // ConfirmWaitEnterBytesPerSecは当初450,000だったが、実機テストで「メッセージ
    // 入力中(未送信)」のI/Oバーストが350KB/s前後まで達し、誤って確認待ち(赤)と
    // 判定されるケースが確認された。実際のAskUserQuestion表示中は700〜800KB/sで
    // 安定していたため、両者の間(600,000)に引き上げて余裕を持たせた。
    // これらの閾値は現在 MonitorConfig 経由で外部化されている(README参照)。

    private MonitorConfig _config;

    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private readonly Queue<float> _window = new();

    private int _tick;
    private int _pendingStatus = -2;
    private int _pendingCount;

    /// <summary>-1=プロセス未検出/不明, 0=アイドル, 1=作業中, 2=確認待ち</summary>
    public int CurrentStatus { get; private set; } = -1;

    /// <summary>直近ウィンドウの中央値(バイト/秒)。診断表示用。</summary>
    public float LastMedianBytesPerSec { get; private set; }

    /// <summary>直近1tickの生値(平滑化前、バイト/秒)。診断・ログ用。</summary>
    public float LastRawBytesPerSec { get; private set; }

    /// <summary>直近tickでの分類結果(デバウンス前)。診断・ログ用。</summary>
    public int LastBucket { get; private set; } = -2;

    public int ProcessCount => _counters.Count;

    /// <summary>ログ出力中かどうか。</summary>
    public bool IsLogging => _logWriter != null;

    /// <summary>現在(または直近)のログファイルのフルパス。</summary>
    public string? LogFilePath { get; private set; }

    private StreamWriter? _logWriter;

    public NetworkStateMonitor(MonitorConfig config)
    {
        _config = config;
    }

    /// <summary>現在有効な設定(ポーリング間隔の参照等に使う)。</summary>
    public MonitorConfig Config => _config;

    /// <summary>
    /// 設定ファイルを再読み込みし、以後のサンプリングに反映する。
    /// 閾値やウィンドウ幅が変わりうるため、内部状態(サンプルウィンドウや
    /// デバウンス中のカウント)は破棄し、次のtickから新しい設定でやり直す。
    /// </summary>
    public void ReloadConfig()
    {
        _config = MonitorConfig.LoadOrCreateDefault();
        _window.Clear();
        _pendingStatus = -2;
        _pendingCount = 0;
        CurrentStatus = -1;
    }

    /// <summary>
    /// ログ出力先ディレクトリ(%LOCALAPPDATA%\ClaudeSignalTray\logs)を返す。
    /// フォルダを開くメニュー等、ログ未開始でもパスを知りたい場合に使う。
    /// </summary>
    public static string GetLogDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "ClaudeSignalTray", "logs");
    }

    /// <summary>
    /// デバッグ用ログ出力を開始する。1tickごとに生値・中央値・判定結果・確定状態を
    /// CSV形式で追記する。既に開始済みなら何もしない。
    /// </summary>
    public string StartLogging()
    {
        if (_logWriter != null)
        {
            return LogFilePath!;
        }

        var dir = GetLogDirectory();
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        writer.WriteLine("timestamp,raw_bytes_per_sec,median_bytes_per_sec,bucket,status,process_count");

        _logWriter = writer;
        LogFilePath = path;
        return path;
    }

    /// <summary>ログ出力を停止する。</summary>
    public void StopLogging()
    {
        _logWriter?.Flush();
        _logWriter?.Dispose();
        _logWriter = null;
    }

    public void Sample()
    {
        if (_tick % _config.RefreshInstancesEveryNTicks == 0)
        {
            RefreshCounters();
        }

        _tick++;

        if (_counters.Count == 0)
        {
            CurrentStatus = -1;
            _pendingStatus = -2;
            _pendingCount = 0;
            _window.Clear();
            LastRawBytesPerSec = 0;
            LastMedianBytesPerSec = 0;
            LastBucket = -1;
            WriteLogRow();
            return;
        }

        float sum = 0;
        foreach (var counter in _counters.Values)
        {
            try
            {
                sum += counter.NextValue();
            }
            catch
            {
                // プロセスが終了した等で読み取り失敗した場合はスキップする
            }
        }

        LastRawBytesPerSec = sum;

        _window.Enqueue(sum);
        while (_window.Count > _config.WindowSize)
        {
            _window.Dequeue();
        }

        var median = Median(_window);
        LastMedianBytesPerSec = median;

        var bucket = Classify(median);
        LastBucket = bucket;
        ApplyDebounce(bucket);

        WriteLogRow();
    }

    private void WriteLogRow()
    {
        if (_logWriter == null)
        {
            return;
        }

        try
        {
            _logWriter.WriteLine(string.Join(',',
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                LastRawBytesPerSec.ToString("0"),
                LastMedianBytesPerSec.ToString("0"),
                LastBucket,
                CurrentStatus,
                ProcessCount));
        }
        catch
        {
            // ログ書き込み失敗(ディスクエラー等)は監視本体に影響させない
        }
    }

    private int Classify(float median)
    {
        if (median >= _config.WorkingThresholdBytesPerSec)
        {
            return 1; // 作業中
        }

        if (CurrentStatus == 1)
        {
            // 作業中(1)から確認待ち(2)へは直接遷移させない。
            // 生成が終わってI/Oが下がっていく「余韻」が、たまたま確認待ち帯の
            // 値を一時的に通過することがあり、これをそのまま確認待ちと判定すると
            // 送信直後に一瞬赤くなる誤判定が発生する(実機テストで確認済み)。
            // いったんアイドル(0)を経由させ、そこから改めて確認待ち帯に
            // 入り直した場合のみ確認待ちとして扱う。
            return 0;
        }

        // アイドル(0)/確認待ち(2)の境界はヒステリシス付き。
        // 現在の確定状態(CurrentStatus)を基準に、切り替わり方向ごとに
        // 別々の閾値を使う。
        var wasConfirmWait = CurrentStatus == 2;

        if (wasConfirmWait)
        {
            return median <= _config.IdleEnterBytesPerSec ? 0 : 2;
        }

        return median >= _config.ConfirmWaitEnterBytesPerSec ? 2 : 0;
    }

    private void ApplyDebounce(int bucket)
    {
        if (bucket == _pendingStatus)
        {
            _pendingCount++;
        }
        else
        {
            _pendingStatus = bucket;
            _pendingCount = 1;
        }

        if (_pendingCount >= _config.DebounceTicks)
        {
            CurrentStatus = bucket;
        }
    }

    private void RefreshCounters()
    {
        List<string> instanceNames;
        try
        {
            var processName = _config.ProcessName;
            instanceNames = new PerformanceCounterCategory("Process")
                .GetInstanceNames()
                .Where(n => n.Equals(processName, StringComparison.OrdinalIgnoreCase)
                         || n.StartsWith(processName + "#", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return;
        }

        var toRemove = _counters.Keys.Where(k => !instanceNames.Contains(k)).ToList();
        foreach (var key in toRemove)
        {
            _counters[key].Dispose();
            _counters.Remove(key);
        }

        foreach (var name in instanceNames)
        {
            if (_counters.ContainsKey(name))
            {
                continue;
            }

            try
            {
                var counter = new PerformanceCounter("Process", "IO Data Bytes/sec", name, readOnly: true);
                counter.NextValue();
                _counters[name] = counter;
            }
            catch
            {
                // カウンター作成に失敗した場合はスキップする(次回リフレッシュで再試行)
            }
        }
    }

    private static float Median(Queue<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var n = sorted.Length;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2f;
    }
}

/// <summary>
/// タスクトレイアイコンの表示を担当するアプリケーションコンテキスト。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int BlinkMs = 600; // 確認待ち時の点滅間隔

    private static readonly Color ColorGreen = Color.FromArgb(46, 160, 67);
    private static readonly Color ColorYellow = Color.FromArgb(230, 175, 15);
    private static readonly Color ColorRedA = Color.FromArgb(220, 40, 40);
    private static readonly Color ColorRedB = Color.FromArgb(255, 90, 90);
    private static readonly Color ColorGray = Color.FromArgb(140, 140, 140);

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _itemStatus;
    private readonly ToolStripMenuItem _itemLogging;
    private readonly ToolStripMenuItem _itemOverlay;
    private readonly ToolStripMenuItem _itemOverlaySizeSmall;
    private readonly ToolStripMenuItem _itemOverlaySizeLarge;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _blinkTimer;
    private readonly NetworkStateMonitor _monitor = new(MonitorConfig.LoadOrCreateDefault());
    private readonly OverlayWindow _overlay;

    private readonly Icon _iconGreen;
    private readonly Icon _iconYellow;
    private readonly Icon _iconRedA;
    private readonly Icon _iconRedB;
    private readonly Icon _iconGray;

    private bool _blinkOn;

    public TrayContext()
    {
        _iconGreen = CreateCircleIcon(ColorGreen);
        _iconYellow = CreateCircleIcon(ColorYellow);
        _iconRedA = CreateCircleIcon(ColorRedA);
        _iconRedB = CreateCircleIcon(ColorRedB, bright: true);
        _iconGray = CreateCircleIcon(ColorGray);

        var menu = new ContextMenuStrip();

        _itemStatus = new ToolStripMenuItem("状態: 取得中...") { Enabled = false };
        menu.Items.Add(_itemStatus);
        menu.Items.Add(new ToolStripSeparator());

        _itemLogging = new ToolStripMenuItem("デバッグログ出力を開始");
        _itemLogging.Click += (_, _) => ToggleLogging();
        menu.Items.Add(_itemLogging);

        var itemOpenLogFolder = new ToolStripMenuItem("設定/ログフォルダを開く");
        itemOpenLogFolder.Click += (_, _) => OpenConfigFolder();
        menu.Items.Add(itemOpenLogFolder);

        var itemReloadConfig = new ToolStripMenuItem("設定を再読み込み");
        itemReloadConfig.Click += (_, _) => ReloadConfig();
        menu.Items.Add(itemReloadConfig);
        menu.Items.Add(new ToolStripSeparator());

        _itemOverlay = new ToolStripMenuItem("オーバーレイウィンドウを表示")
        {
            CheckOnClick = true,
            Checked = _monitor.Config.OverlayEnabled,
        };
        menu.Items.Add(_itemOverlay);

        var isLargeInitial = _monitor.Config.OverlaySize == "Large";
        _itemOverlaySizeSmall = new ToolStripMenuItem("小") { Checked = !isLargeInitial };
        _itemOverlaySizeLarge = new ToolStripMenuItem("大") { Checked = isLargeInitial };
        _itemOverlaySizeSmall.Click += (_, _) => SetOverlaySize(large: false);
        _itemOverlaySizeLarge.Click += (_, _) => SetOverlaySize(large: true);

        var itemOverlaySize = new ToolStripMenuItem("オーバーレイのサイズ");
        itemOverlaySize.DropDownItems.Add(_itemOverlaySizeSmall);
        itemOverlaySize.DropDownItems.Add(_itemOverlaySizeLarge);
        menu.Items.Add(itemOverlaySize);
        menu.Items.Add(new ToolStripSeparator());

        var itemExit = new ToolStripMenuItem("終了");
        itemExit.Click += (_, _) => ExitApplication();
        menu.Items.Add(itemExit);

        _trayIcon = new NotifyIcon
        {
            Icon = _iconGray,
            Text = "claude-signal-tray: 起動中...",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // オーバーレイウィンドウ(常時最前面の小さな状態表示)。
        // 右クリックメニューはトレイアイコンと共有する。
        _overlay = new OverlayWindow(menu);
        _overlay.ApplySizePreset(isLargeInitial);
        _overlay.PositionChanged += OnOverlayPositionChanged;
        PositionOverlayIfNeeded();
        if (_monitor.Config.OverlayEnabled)
        {
            _overlay.Show();
        }

        _itemOverlay.CheckedChanged += (_, _) => ToggleOverlay(_itemOverlay.Checked);

        _pollTimer = new System.Windows.Forms.Timer { Interval = _monitor.Config.PollIntervalMs };
        _pollTimer.Tick += (_, _) => UpdateStatus();
        _pollTimer.Start();

        _blinkTimer = new System.Windows.Forms.Timer { Interval = BlinkMs };
        _blinkTimer.Tick += (_, _) =>
        {
            if (_monitor.CurrentStatus != 2)
            {
                return;
            }

            _blinkOn = !_blinkOn;
            _trayIcon.Icon = _blinkOn ? _iconRedB : _iconRedA;
            _overlay.SetDotColor(_blinkOn ? ColorRedB : ColorRedA);
        };
        _blinkTimer.Start();

        UpdateStatus();
    }

    private void ToggleOverlay(bool enabled)
    {
        _monitor.Config.OverlayEnabled = enabled;
        _monitor.Config.TrySave();

        if (enabled)
        {
            PositionOverlayIfNeeded();
            _overlay.Show();
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void SetOverlaySize(bool large)
    {
        _monitor.Config.OverlaySize = large ? "Large" : "Small";
        _monitor.Config.TrySave();

        _itemOverlaySizeSmall.Checked = !large;
        _itemOverlaySizeLarge.Checked = large;

        _overlay.ApplySizePreset(large);

        // ドラッグ済みで位置が保存されている場合はその左上座標を維持したまま
        // サイズだけ変える。未設定(既定位置)の場合は、新しいサイズに合わせて
        // 右端揃えの位置を再計算する。
        PositionOverlayIfNeeded();
    }

    private void OnOverlayPositionChanged(Point newLocation)
    {
        _monitor.Config.OverlayX = newLocation.X;
        _monitor.Config.OverlayY = newLocation.Y;
        _monitor.Config.TrySave();
    }

    private void PositionOverlayIfNeeded()
    {
        var cfg = _monitor.Config;
        if (cfg.OverlayX >= 0 && cfg.OverlayY >= 0)
        {
            _overlay.Location = new Point(cfg.OverlayX, cfg.OverlayY);
            return;
        }

        // 既定位置: 画面右端・縦中央付近(ドラッグで移動すれば以後はその位置を記憶する)
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var x = area.Right - _overlay.Width - 16;
        var y = area.Top + (area.Height - _overlay.Height) / 2;
        _overlay.Location = new Point(x, y);
    }

    private void UpdateStatus()
    {
        _monitor.Sample();

        var kbPerSec = _monitor.LastMedianBytesPerSec / 1024.0;
        var kbText = kbPerSec.ToString("0");

        switch (_monitor.CurrentStatus)
        {
            case -1:
                _trayIcon.Icon = _iconGray;
                _trayIcon.Text = "claude-signal-tray: Claudeプロセスが見つかりません";
                _itemStatus.Text = "Claude未検出";
                _overlay.SetDotColor(ColorGray);
                _overlay.SetText("Claude未検出");
                break;
            case 0:
                _trayIcon.Icon = _iconGreen;
                _trayIcon.Text = $"claude-signal-tray: 待機中/完了 ({kbText} KB/s)";
                _itemStatus.Text = $"待機中/完了 ({kbText} KB/s, {_monitor.ProcessCount}プロセス)";
                _overlay.SetDotColor(ColorGreen);
                _overlay.SetText($"待機中/完了 ({kbText} KB/s)");
                break;
            case 1:
                _trayIcon.Icon = _iconYellow;
                _trayIcon.Text = $"claude-signal-tray: 作業中 ({kbText} KB/s)";
                _itemStatus.Text = $"作業中 ({kbText} KB/s, {_monitor.ProcessCount}プロセス)";
                _overlay.SetDotColor(ColorYellow);
                _overlay.SetText($"作業中 ({kbText} KB/s)");
                break;
            case 2:
                // ドットの点滅は _blinkTimer が担当するので、ここではテキストのみ更新する。
                _trayIcon.Text = $"claude-signal-tray: 確認待ちの可能性 ({kbText} KB/s)";
                _itemStatus.Text = $"確認待ちの可能性 ({kbText} KB/s, {_monitor.ProcessCount}プロセス)";
                _overlay.SetText($"確認待ちの可能性 ({kbText} KB/s)");
                break;
        }
    }

    private void ToggleLogging()
    {
        if (_monitor.IsLogging)
        {
            _monitor.StopLogging();
            _itemLogging.Text = "デバッグログ出力を開始";
            _trayIcon.ShowBalloonTip(2000, "claude-signal-tray", "ログ出力を停止しました。", ToolTipIcon.Info);
            return;
        }

        var path = _monitor.StartLogging();
        _itemLogging.Text = "デバッグログ出力を停止";
        _trayIcon.ShowBalloonTip(3000, "claude-signal-tray", $"ログ出力を開始しました:\n{path}", ToolTipIcon.Info);
    }

    private void ReloadConfig()
    {
        _monitor.ReloadConfig();
        _pollTimer.Interval = _monitor.Config.PollIntervalMs;
        _trayIcon.ShowBalloonTip(2000, "claude-signal-tray",
            $"設定を再読み込みしました:\n{MonitorConfig.GetConfigPath()}", ToolTipIcon.Info);
    }

    private static void OpenConfigFolder()
    {
        var dir = Path.GetDirectoryName(MonitorConfig.GetConfigPath())!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private static Icon CreateCircleIcon(Color color, bool bright = false)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 28, 28);

            var penColor = bright ? Color.White : Color.FromArgb(60, 60, 60);
            using var pen = new Pen(penColor, 2);
            g.DrawEllipse(pen, 2, 2, 28, 28);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private void ExitApplication()
    {
        _pollTimer.Stop();
        _blinkTimer.Stop();
        _monitor.StopLogging();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _overlay.Dispose();
        Application.Exit();
    }
}

/// <summary>
/// 常に最前面に表示する、状態確認用の小さなオーバーレイウィンドウ。
/// タイトルバー・枠なしで、ドラッグでの移動と位置の記憶に対応する。
/// フォーカスを奪わないようにしているため、フォアグラウンドで作業中の
/// 他アプリの操作を妨げない(このツールを作った動機と矛盾しないための配慮)。
/// </summary>
internal sealed class OverlayWindow : Form
{
    private readonly Panel _dot;
    private readonly Label _label;

    private bool _dragging;
    private Point _dragStartMouse;
    private Point _dragStartWindow;

    public event Action<Point>? PositionChanged;

    public OverlayWindow(ContextMenuStrip contextMenu)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.92;
        ContextMenuStrip = contextMenu;

        _dot = new Panel { BackColor = Color.Gray };
        Controls.Add(_dot);

        _label = new Label
        {
            AutoSize = false,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Text = "取得中...",
        };
        Controls.Add(_label);

        // フォーム本体・ドット・ラベルのいずれをドラッグしても移動できるようにする
        foreach (Control c in new Control[] { this, _dot, _label })
        {
            c.MouseDown += OnMouseDown;
            c.MouseMove += OnMouseMove;
            c.MouseUp += OnMouseUp;
        }

        ApplySizePreset(large: false);
    }

    public void SetDotColor(Color color) => _dot.BackColor = color;

    public void SetText(string text) => _label.Text = text;

    /// <summary>
    /// ウィンドウ全体・ドット・ラベルのサイズを、小/大の2種類のプリセットで
    /// 一括切り替えする。
    /// </summary>
    public void ApplySizePreset(bool large)
    {
        if (large)
        {
            Size = new Size(300, 60);
            _dot.Size = new Size(22, 22);
            _dot.Location = new Point(16, 19);
            _label.Location = new Point(48, 15);
            _label.Size = new Size(236, 30);
            _label.Font = new Font("Segoe UI", 12f);
        }
        else
        {
            Size = new Size(220, 44);
            _dot.Size = new Size(16, 16);
            _dot.Location = new Point(12, 14);
            _label.Location = new Point(36, 11);
            _label.Size = new Size(172, 22);
            _label.Font = new Font("Segoe UI", 9f);
        }

        RefreshDotRegion();
    }

    private void RefreshDotRegion()
    {
        _dot.Region?.Dispose();
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(0, 0, _dot.Width, _dot.Height);
        _dot.Region = new Region(path);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _dragStartMouse = Cursor.Position;
        _dragStartWindow = Location;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var dx = Cursor.Position.X - _dragStartMouse.X;
        var dy = Cursor.Position.Y - _dragStartMouse.Y;
        Location = new Point(_dragStartWindow.X + dx, _dragStartWindow.Y + dy);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        PositionChanged?.Invoke(Location);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate;
            return cp;
        }
    }
}
