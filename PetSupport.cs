// ペットオーバーレイ機能のサポートクラス群。
// 設計方針・pet.jsonフォーマットの詳細は docs/pet-overlay-design.md を参照。
//
// 状態判定ロジック(NetworkStateMonitor)には依存しない純粋な表示層。
// 状態名("idle"/"run"/"wave"/"off")を受け取ってスプライトを描画するだけ。

using System.Reflection;
using System.Text.Json;

namespace ClaudeSignalTray;

/// <summary>pet.json の states 配下1エントリ。スプライトシート上の行と、その行のフレーム数。</summary>
internal sealed class PetStateDef
{
    public int Row { get; set; }
    public int Frames { get; set; } = 6;
}

/// <summary>pet.json 全体のモデル。</summary>
internal sealed class PetDefinition
{
    public string Name { get; set; } = "pet";
    public int FrameWidth { get; set; } = 32;
    public int FrameHeight { get; set; } = 32;
    public int FrameDurationMs { get; set; } = 180;
    public Dictionary<string, PetStateDef> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>明らかに不正な値を既定値へ戻す(アプリを落とさないことを優先)。</summary>
    public void Sanitize()
    {
        if (FrameWidth <= 0) FrameWidth = 32;
        if (FrameHeight <= 0) FrameHeight = 32;
        if (FrameDurationMs < 30) FrameDurationMs = 180;

        // JSONデシリアライズで生成された辞書は既定で大文字小文字を区別するため、
        // 状態名("Idle"等の表記揺れ)を許容する辞書に詰め替える。
        // 大文字小文字違いの重複キーがあった場合は先勝ち(例外にしない)。
        var merged = new Dictionary<string, PetStateDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in States)
        {
            merged.TryAdd(kv.Key, kv.Value);
        }

        States = merged;

        foreach (var s in States.Values)
        {
            if (s.Row < 0) s.Row = 0;
            if (s.Frames <= 0) s.Frames = 1;
        }
    }
}

/// <summary>ペット定義とスプライトシート画像の組。</summary>
internal sealed class Pet : IDisposable
{
    public PetDefinition Def { get; }
    public Bitmap Sheet { get; }

    private Pet(PetDefinition def, Bitmap sheet)
    {
        Def = def;
        Sheet = sheet;
    }

    /// <summary>
    /// 指定ディレクトリ(pet.json + spritesheet.png)からペットを読み込む。
    /// 壊れている・存在しない場合は null(呼び出し側で組み込みペットへフォールバック)。
    /// </summary>
    public static Pet? TryLoad(string directory)
    {
        try
        {
            var jsonPath = Path.Combine(directory, "pet.json");
            var pngPath = Path.Combine(directory, "spritesheet.png");
            if (!File.Exists(jsonPath) || !File.Exists(pngPath))
            {
                return null;
            }

            var def = JsonSerializer.Deserialize<PetDefinition>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (def == null || def.States.Count == 0)
            {
                return null;
            }

            def.Sanitize();

            // ファイルロックを避けるため、メモリへコピーしてから Bitmap 化する
            using var stream = new MemoryStream(File.ReadAllBytes(pngPath));
            var sheet = new Bitmap(stream);
            return new Pet(def, sheet);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 実行ファイルに埋め込まれた組み込みペットを名前で読み込む。
    /// リソースが存在しない場合は null。
    /// </summary>
    public static Pet? TryLoadEmbedded(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            using var jsonStream = asm.GetManifestResourceStream($"ClaudeSignalTray.Pets.{name}.pet.json");
            if (jsonStream == null)
            {
                return null;
            }

            using var reader = new StreamReader(jsonStream);
            var def = JsonSerializer.Deserialize<PetDefinition>(
                reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (def == null || def.States.Count == 0)
            {
                return null;
            }

            def.Sanitize();

            using var pngStream = asm.GetManifestResourceStream($"ClaudeSignalTray.Pets.{name}.spritesheet.png");
            if (pngStream == null)
            {
                return null;
            }

            var sheet = new Bitmap(pngStream);
            return new Pet(def, sheet);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>実行ファイルに埋め込まれた組み込みペット(default)を読み込む。</summary>
    public static Pet LoadEmbeddedDefault()
        => TryLoadEmbedded(PetLibrary.DefaultPetName)
           ?? throw new InvalidOperationException("組み込みペット(default)のリソースが見つかりません");

    /// <summary>
    /// 状態名から描画対象を解決する。未定義の状態は idle → 先頭定義 の順でフォールバック。
    /// </summary>
    public PetStateDef ResolveState(string state)
    {
        if (Def.States.TryGetValue(state, out var s))
        {
            return s;
        }

        if (Def.States.TryGetValue("idle", out var idle))
        {
            return idle;
        }

        return Def.States.Values.First();
    }

    public void Dispose() => Sheet.Dispose();
}

/// <summary>
/// %LOCALAPPDATA%\ClaudeSignalTray\pets 配下のペットの列挙・展開・読み込みを担当する。
/// </summary>
internal static class PetLibrary
{
    public const string DefaultPetName = "default";

    /// <summary>exeに埋め込まれている組み込みペットの一覧。追加時はcsprojのEmbeddedResourceも更新すること。</summary>
    public static readonly string[] BuiltinPetNames = { "default", "bat" };

    public static string GetPetsDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "ClaudeSignalTray", "pets");
    }

    /// <summary>
    /// すべての組み込みペットを pets フォルダへ展開する(各ペットとも未存在時のみ)。
    /// カスタムペット作成時のフォーマット実例を兼ねる。失敗しても致命的ではない。
    /// </summary>
    public static void EnsureBuiltinPetsExtracted()
    {
        foreach (var name in BuiltinPetNames)
        {
            ExtractBuiltinPet(name);
        }
    }

    private static void ExtractBuiltinPet(string name)
    {
        try
        {
            var dir = Path.Combine(GetPetsDirectory(), name);
            var jsonPath = Path.Combine(dir, "pet.json");
            var pngPath = Path.Combine(dir, "spritesheet.png");
            if (File.Exists(jsonPath) && File.Exists(pngPath))
            {
                return; // ユーザーが編集している可能性があるため上書きしない
            }

            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();

            using (var src = asm.GetManifestResourceStream($"ClaudeSignalTray.Pets.{name}.pet.json"))
            using (var dst = File.Create(jsonPath))
            {
                src?.CopyTo(dst);
            }

            using (var src = asm.GetManifestResourceStream($"ClaudeSignalTray.Pets.{name}.spritesheet.png"))
            using (var dst = File.Create(pngPath))
            {
                src?.CopyTo(dst);
            }
        }
        catch
        {
            // 展開失敗時も組み込みリソースから直接読めるため動作に支障はない
        }
    }

    /// <summary>pets フォルダ内の有効なペット名(pet.json と spritesheet.png が揃っているもの)を列挙する。</summary>
    public static List<string> ListPetNames()
    {
        var names = new List<string>();
        try
        {
            var root = GetPetsDirectory();
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, "pet.json"))
                        && File.Exists(Path.Combine(dir, "spritesheet.png")))
                    {
                        names.Add(Path.GetFileName(dir));
                    }
                }
            }
        }
        catch
        {
            // 列挙失敗時は空リスト(メニューには組み込みdefaultのみ表示される)
        }

        // 組み込みペットは展開に失敗していてもメニューに出す(埋め込みリソースから直接読めるため)
        foreach (var builtin in BuiltinPetNames)
        {
            if (!names.Contains(builtin, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(builtin);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// 指定名のペットを読み込む。ディスク → 同名の埋め込みリソース → 組み込みdefault の順で
    /// フォールバックする(どこかで必ず成功し、アプリは落ちない)。
    /// </summary>
    public static Pet LoadByNameOrDefault(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var pet = Pet.TryLoad(Path.Combine(GetPetsDirectory(), name))
                      ?? Pet.TryLoadEmbedded(name);
            if (pet != null)
            {
                return pet;
            }
        }

        return Pet.LoadEmbeddedDefault();
    }
}

/// <summary>
/// スプライトシートをアニメーション描画するコントロール。
/// 拡大は最近傍補間(ピクセルアートをぼやけさせない)。
/// </summary>
internal sealed class PetView : Control
{
    private Pet _pet;
    private string _state = "off";
    private int _frame;
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>表示倍率(小=2, 大=3)。Control.Scale(float)との名前衝突を避けるためPixelScaleとする。</summary>
    public int PixelScale { get; private set; } = 2;

    public PetView(Pet pet)
    {
        _pet = pet;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;

        _timer = new System.Windows.Forms.Timer { Interval = pet.Def.FrameDurationMs };
        _timer.Tick += (_, _) =>
        {
            _frame++;
            Invalidate();
        };
    }

    /// <summary>ペットを差し替える(メニューからの選択切替時)。</summary>
    public void SetPet(Pet pet)
    {
        var old = _pet;
        _pet = pet;
        _timer.Interval = pet.Def.FrameDurationMs;
        _frame = 0;
        Invalidate();
        if (!ReferenceEquals(old, pet))
        {
            old.Dispose();
        }
    }

    public void SetScale(int scale)
    {
        PixelScale = Math.Max(1, scale);
        Size = new Size(_pet.Def.FrameWidth * PixelScale, _pet.Def.FrameHeight * PixelScale);
        Invalidate();
    }

    /// <summary>アニメーション状態("idle"/"run"/"wave"/"off")を切り替える。</summary>
    public void SetAnimationState(string state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _frame = 0;
        Invalidate();
    }

    /// <summary>可視状態に合わせてタイマーを開始/停止する(非表示中のCPU消費を避ける)。</summary>
    public void SetAnimating(bool animating)
    {
        if (animating)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var def = _pet.Def;
        var st = _pet.ResolveState(_state);
        var frameIndex = _frame % st.Frames;

        var src = new Rectangle(
            frameIndex * def.FrameWidth,
            st.Row * def.FrameHeight,
            def.FrameWidth,
            def.FrameHeight);
        var dst = new Rectangle(0, 0, def.FrameWidth * PixelScale, def.FrameHeight * PixelScale);

        var g = e.Graphics;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(_pet.Sheet, dst, src, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _pet.Dispose();
        }

        base.Dispose(disposing);
    }
}
