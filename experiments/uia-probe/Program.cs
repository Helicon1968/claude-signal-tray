// UiaProbe - 使い捨ての検証用コンソールアプリ。
// Claude Desktopのウィンドウを、通常時と最小化後の両方でUI Automation経由で
// (数階層潜って)読み取れるかどうかを確認するためだけのツール。
// claude-signal-tray本体には含めない。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, bool Visible, bool Minimized);

    private const int MaxDepth = 6;
    private const int MaxSiblingsPerLevel = 12;

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var selfPid = (uint)Environment.ProcessId;

        Console.WriteLine("=== 1回目: 現在の状態でウィンドウを検索します ===");
        var candidates = FindCandidateWindows(selfPid);
        PrintCandidates(candidates);

        var target = FindClaudeProcessWindow(candidates);
        if (target is null)
        {
            Console.WriteLine("プロセス名が claude のウィンドウが見つかりませんでした。Claude Desktopを起動してから再実行してください。");
            Console.WriteLine("終了するには何かキーを押してください...");
            Console.ReadKey();
            return;
        }

        ProbeWindow(target);

        Console.WriteLine();
        Console.WriteLine("=== Claude Desktopのウィンドウを最小化してから Enter キーを押してください ===");
        Console.ReadLine();

        Console.WriteLine();
        Console.WriteLine("=== 2回目: 最小化後の状態で再検索します ===");
        var candidates2 = FindCandidateWindows(selfPid);
        PrintCandidates(candidates2);

        var target2 = FindClaudeProcessWindow(candidates2);
        if (target2 is null)
        {
            Console.WriteLine("最小化後、プロセス名が claude のウィンドウが見つかりませんでした（ハンドルが変わった可能性があります）。");
        }
        else
        {
            ProbeWindow(target2);
        }

        Console.WriteLine();
        Console.WriteLine("完了しました。終了するには何かキーを押してください...");
        Console.ReadKey();
    }

    private static WindowInfo? FindClaudeProcessWindow(List<WindowInfo> list)
    {
        foreach (var w in list)
        {
            if (w.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                return w;
            }
        }

        return null;
    }

    private static List<WindowInfo> FindCandidateWindows(uint selfPid)
    {
        var results = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            var visible = IsWindowVisible(hWnd);
            var minimized = IsIconic(hWnd);

            if (!visible && !minimized)
            {
                // 完全に不可視(かつ最小化でもない)なウィンドウは対象外
                return true;
            }

            var length = GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (!title.Contains("Claude", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == selfPid)
            {
                return true;
            }

            var processName = "(不明)";
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                // プロセス情報が取れなくても続行する
            }

            results.Add(new WindowInfo(hWnd, title, processName, visible, minimized));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static void PrintCandidates(List<WindowInfo> list)
    {
        if (list.Count == 0)
        {
            Console.WriteLine("(該当ウィンドウなし)");
            return;
        }

        foreach (var w in list)
        {
            Console.WriteLine($"- \"{w.Title}\" (プロセス: {w.ProcessName}, 表示中: {w.Visible}, 最小化: {w.Minimized}, ハンドル: {w.Handle})");
        }
    }

    private static void ProbeWindow(WindowInfo w)
    {
        Console.WriteLine();
        Console.WriteLine($"--- \"{w.Title}\" (最小化: {w.Minimized}) を UI Automation で読み取ります (最大深さ{MaxDepth}) ---");

        var sw = Stopwatch.StartNew();
        var totalCount = 0;

        try
        {
            var element = AutomationElement.FromHandle(w.Handle);
            if (element is null)
            {
                Console.WriteLine("  AutomationElement.FromHandle が null を返しました。");
                return;
            }

            totalCount = DumpTree(element, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  読み取りに失敗しました: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            Console.WriteLine($"  --- 走査した要素数: {totalCount}, 所要時間: {sw.ElapsedMilliseconds}ms ---");
        }
    }

    private static int DumpTree(AutomationElement element, int depth)
    {
        var indent = new string(' ', depth * 2);
        var name = SafeGet(() => element.Current.Name);
        var type = SafeGet(() => element.Current.ControlType.ProgrammaticName);
        var offscreen = SafeGetBool(() => element.Current.IsOffscreen);
        Console.WriteLine($"{indent}- Name=\"{Truncate(name)}\" Type={type} Offscreen={offscreen}");

        var count = 1;

        if (depth >= MaxDepth)
        {
            return count;
        }

        AutomationElement? child;
        try
        {
            child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{indent}  (子要素取得エラー: {ex.GetType().Name})");
            return count;
        }

        var siblingIndex = 0;
        while (child is not null && siblingIndex < MaxSiblingsPerLevel)
        {
            count += DumpTree(child, depth + 1);

            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch
            {
                break;
            }

            siblingIndex++;
        }

        return count;
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "...";

    private static string SafeGet(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            return $"(取得失敗: {ex.GetType().Name})";
        }
    }

    private static string SafeGetBool(Func<bool> getter)
    {
        try
        {
            return getter().ToString();
        }
        catch (Exception ex)
        {
            return $"(取得失敗: {ex.GetType().Name})";
        }
    }
}
