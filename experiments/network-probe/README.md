# NetworkProbe (検証用)

`claude` という名前の全プロセスの `IO Data Bytes/sec`（PerformanceCounter、ディスク・
ネットワーク等のI/O合算値）と `% Processor Time` を0.5秒間隔でサンプリングし、
コンソール表示とCSVログ（`probe-log-{開始時刻}.csv`）の両方に記録する、使い捨ての
診断用コンソールアプリです。**claude-signal-tray 本体には組み込まれていません。**

claude-signal-tray が現在採用しているプロセスI/O量ベースの状態推定方式（アイドル／
確認待ち／作業中の3段階判定）は、このツールを使った手動検証で得られた実測データを
根拠にしている。検証の背景・経緯は [docs/HISTORY.md](../../docs/HISTORY.md) を参照。

## 実行方法

```
dotnet run
```

Claude/Coworkを操作しながら実行し、完全アイドル・AskUserQuestion表示中（確認待ち）・
実際に作業中（検索や長文生成など）のそれぞれの区間でI/O量がどう変化するかをCSVで
記録・比較する、という使い方を想定している。
