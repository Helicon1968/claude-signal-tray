// NetworkProbe - 使い捨ての検証用コンソールアプリ。
// Claudeプロセス(複数あり得る)のI/O量とCPU使用率を継続的にサンプリングし、
// 「作業中」と「アイドル」を通信量(に近い指標)だけで区別できそうかを検証する。
// claude-signal-tray本体には含めない。
//
// 注意: PerformanceCounterの "IO Data Bytes/sec" はネットワークI/Oだけでなく
// ディスク/デバイスI/Oも含む合算値。TLSを解かずに取れる範囲の「簡易プローブ」
// として、まずこれで傾向が見えるかを確認する。

using System.Diagnostics;
using System.Globalization;
using System.Threading;

internal static class Program
{
    private const string ProcessBaseName = "claude";
    private const int SampleIntervalMs = 500;
    private const int RefreshInstancesEveryNTicks = 10; // 5秒ごとにインスタンス一覧を再取得

    private static volatile bool _stop;

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stop = true;
        };

        var logPath = Path.Combine(
            AppContext.BaseDirectory,
            $"probe-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        Console.WriteLine("claude プロセスのI/O量・CPU使用率を継続サンプリングします。");
        Console.WriteLine("Claude Desktopを操作しながら、状態が変わったタイミング(作業開始・");
        Console.WriteLine("アイドルに戻ったなど)を別途メモしておいてください。");
        Console.WriteLine($"ログファイル: {logPath}");
        Console.WriteLine("Ctrl+C で終了します。");
        Console.WriteLine();
        Console.WriteLine($"{"時刻",-12} {"IO KB/s",10} {"CPU(合算,%)",12} {"対象プロセス数",10}");

        using var writer = new StreamWriter(logPath, append: false);
        writer.WriteLine("timestamp,io_kb_per_sec,cpu_percent_sum,process_count");
        writer.Flush();

        var counters = new Dictionary<string, (PerformanceCounter Io, PerformanceCounter Cpu)>();
        var tick = 0;

        while (!_stop)
        {
            if (tick % RefreshInstancesEveryNTicks == 0)
            {
                RefreshCounters(counters);
            }

            var (ioBytesPerSec, cpuPercentSum, count) = SampleAll(counters);
            var ioKb = ioBytesPerSec / 1024.0;
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            Console.WriteLine(
                $"{timestamp,-12} {ioKb,10:0.0} {cpuPercentSum,12:0.0} {count,10}");

            writer.WriteLine(
                $"{DateTime.Now:O},{ioKb.ToString("0.00", CultureInfo.InvariantCulture)},{cpuPercentSum.ToString("0.00", CultureInfo.InvariantCulture)},{count}");
            writer.Flush();

            tick++;
            Thread.Sleep(SampleIntervalMs);
        }

        Console.WriteLine();
        Console.WriteLine("終了しました。ログファイルを確認してください:");
        Console.WriteLine(logPath);
    }

    private static void RefreshCounters(Dictionary<string, (PerformanceCounter Io, PerformanceCounter Cpu)> counters)
    {
        List<string> instanceNames;
        try
        {
            instanceNames = new PerformanceCounterCategory("Process")
                .GetInstanceNames()
                .Where(n => n.Equals(ProcessBaseName, StringComparison.OrdinalIgnoreCase)
                         || n.StartsWith(ProcessBaseName + "#", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(インスタンス一覧の取得に失敗: {ex.GetType().Name}: {ex.Message})");
            return;
        }

        // 消えたインスタンスのカウンターを破棄する
        var toRemove = counters.Keys.Where(k => !instanceNames.Contains(k)).ToList();
        foreach (var key in toRemove)
        {
            counters[key].Io.Dispose();
            counters[key].Cpu.Dispose();
            counters.Remove(key);
        }

        // 新しいインスタンスのカウンターを追加する
        foreach (var name in instanceNames)
        {
            if (counters.ContainsKey(name))
            {
                continue;
            }

            try
            {
                var io = new PerformanceCounter("Process", "IO Data Bytes/sec", name, readOnly: true);
                var cpu = new PerformanceCounter("Process", "% Processor Time", name, readOnly: true);
                io.NextValue();
                cpu.NextValue();
                counters[name] = (io, cpu);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(インスタンス \"{name}\" のカウンター作成に失敗: {ex.GetType().Name})");
            }
        }
    }

    private static (float IoBytesPerSec, float CpuPercentSum, int Count) SampleAll(
        Dictionary<string, (PerformanceCounter Io, PerformanceCounter Cpu)> counters)
    {
        float ioSum = 0;
        float cpuSum = 0;
        var count = 0;

        foreach (var (io, cpu) in counters.Values)
        {
            try
            {
                ioSum += io.NextValue();
                cpuSum += cpu.NextValue();
                count++;
            }
            catch
            {
                // プロセスが終了した等で読み取り失敗した場合はスキップする
            }
        }

        return (ioSum, cpuSum, count);
    }
}
