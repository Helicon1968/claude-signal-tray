# MSIインストーラー(installer/)

claude-signal-tray を、exeの直接配布ではなく、Windowsの「アプリと機能」に登録される
標準的なMSIインストーラーとして配布するためのプロジェクト。[WiX Toolset](https://wixtoolset.org/)
(v6、無料・OSS)を使用している。

> **SmartScreenについての注意:** MSI化しても、Windowsの SmartScreen 警告(「WindowsによってPCが保護されました」)は
> 未署名である限り引き続き表示される。これは拡張子(exe/msi)ではなく、コード署名の有無と
> 評判(reputation)で決まるため。MSI化の目的はあくまで「アンインストール・ショートカット等、
> 配布体験の改善」であり、SmartScreen自体の解消ではない(詳細はdocs/HISTORY.md参照)。

## ビルド方法

1. 先に本体をpublishしておく(MSIは publish 済みの単一exeをそのまま取り込むだけで、
   本体のビルドはしない)。

   ```powershell
   # リポジトリルートで実行
   dotnet publish -c Release
   ```

2. このフォルダでMSIをビルドする。

   ```powershell
   cd installer
   dotnet build -c Release
   ```

   生成物: `installer/bin/x64/Release/ClaudeSignalTraySetup.msi`

## 設計メモ

- **per-user install(管理者権限不要)**: `Package/@Scope="perUser"`。本体アプリ自体が
  管理者権限なしで動くタスクトレイ常駐アプリであり、README記載の自動起動もスタートアップ
  フォルダ方式(管理者権限不要)を採用している既存方針に合わせた。
- **インストール先**: `%LOCALAPPDATA%\Programs\ClaudeSignalTray\ClaudeSignalTray.exe`。
  アプリ自身が設定・ログ・ペット定義を保存する `%LOCALAPPDATA%\ClaudeSignalTray\`
  (`MonitorConfig.GetConfigPath()`等参照)とは意図的に別フォルダにしており、
  アンインストール時にユーザーデータ(config.json・logs・pets)を誤って巻き込まない。
- **スタートメニューへのショートカットを作成**。自動起動(Windows起動時に常駐)は
  MSIでは行わない(README/`scripts/Install-AutoStart.ps1`を参照して別途設定する)。
- **UI**: `WixUI_Minimal`(ようこそ→ライセンス→進捗→完了、の最小構成)。
  ライセンスは`License.rtf`(リポジトリの`LICENSE`と同内容)。
- **バージョン更新時**: 本体`ClaudeSignalTray.csproj`の`<Version>`と、
  このプロジェクトの`<ProductVersion>`(`ClaudeSignalTrayInstaller.wixproj`)を
  両方合わせて変更すること。`UpgradeCode`(`Package.wxs`)は将来にわたって
  変更しないこと(変更すると「別製品」としてインストールされ、旧バージョンからの
  アップグレードができなくなる)。

## WiXのバージョンについて

WiX v7 は "Open Source Maintenance Fee (OSMF)" への同意が必須になっており
(詳細: https://wixtoolset.org/osmf/ )、無条件に無料で使えるv6を採用している。
v7へ上げる場合は、その利用条件を確認・許容した上で判断すること。
