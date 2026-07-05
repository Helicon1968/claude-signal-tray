# ペットオーバーレイ 設計メモ

## 目的

オーバーレイウィンドウの表示形式を「信号機（現行の●＋テキスト）」と「ペット（アニメーション）」から選択できるようにする。Codex Pets と同様に、キャラクターの動作（寝る・走る・手を振る）で状態を直感的に伝える。

## 設計原則

- **判定ロジック（NetworkStateMonitor）には一切手を入れない。** 状態(-1/0/1/2)を受け取って描画するだけの表示層の追加に限定する。
- トレイアイコンは現行のまま（信号ドット）。変更するのはオーバーレイウィンドウのみ。
- 信号機モードが既定。ペットはオプトイン（客先・画面共有などでは信号機に即戻せる）。

## 状態→アニメーションのマッピング

| status | 意味 | アニメ状態名 | 動作 |
|---|---|---|---|
| -1 | Claude未検出 | `off` | 灰色でぺたんと寝ている |
| 0 | 待機中/完了 | `idle` | すやすや寝ている（Zzz） |
| 1 | 作業中 | `run` | 走っている |
| 2 | 確認待ちの可能性 | `wave` | 手を振って呼んでいる（！マーク） |

## ペット定義フォーマット（pet.json ＋ spritesheet.png）

Codex Pets の「マニフェスト＋スプライトシート」方式に倣った独自の最小フォーマット。

```json
{
  "name": "default-cat",
  "frameWidth": 32,
  "frameHeight": 32,
  "frameDurationMs": 180,
  "states": {
    "idle": { "row": 0, "frames": 6 },
    "run":  { "row": 1, "frames": 6 },
    "wave": { "row": 2, "frames": 6 },
    "off":  { "row": 3, "frames": 6 }
  }
}
```

- spritesheet.png は横=フレーム、縦=状態(row)のグリッド。
- 定義に無い状態が要求された場合は `idle` → 先頭状態の順でフォールバック。
- 拡大は最近傍補間（ピクセルアートがぼやけないように）。小=2倍、大=3倍。

## カスタムペットの配置

```
%LOCALAPPDATA%\ClaudeSignalTray\pets\<ペット名>\
    pet.json
    spritesheet.png
```

- 初回起動時に組み込みペット（default）を上記フォルダへ展開し、フォーマットの実例とする。
- トレイメニュー「ペットの選択」はフォルダを都度スキャンして一覧表示する。
- 壊れた pet.json / 読めない PNG は無視して組み込みペットにフォールバック（アプリを落とさない）。

## クラス構成

```
Program.cs（既存）
  MonitorConfig            … OverlayMode("Signal"/"Pet")、PetName を追加
  NetworkStateMonitor      … 変更なし
  TrayContext              … メニュー追加（表示形式・ペット選択）、状態をオーバーレイへ中継
  OverlayWindow            … 信号機ビュー(dot+label)とPetViewを持ち、モードで切替
PetSupport.cs（新規）
  PetDefinition/PetStateDef … pet.json のモデル
  Pet                      … 定義＋Bitmapの組。読み込みとフォールバック
  PetLibrary               … petsフォルダの列挙・展開・読み込み
  PetView                  … ダブルバッファ描画＋フレームタイマーのカスタムコントロール
assets/pets/default/       … 組み込みペット（EmbeddedResource）
scripts/gen_default_pet.py … スプライトシート生成スクリプト（再現用）
```

## config.json 追加項目

| 項目 | 値 | 既定 |
|---|---|---|
| `OverlayMode` | `"Signal"` / `"Pet"` | `"Signal"` |
| `PetName` | petsフォルダ内のディレクトリ名 | `"default"` |

不正値は既定値へフォールバック（既存のSanitize方針を踏襲）。

## 非採用としたこと

- タスク文字列の吹き出し表示: 現方式（I/O監視）ではタスク内容を取得できないため対象外。
- 透過ウィンドウでのデスクトップマスコット化（枠なし完全透過・自由配置）: 第一段階では現行オーバーレイの矩形ウィンドウ内に描画。反応を見て次段階で検討。
- Codex pet.json の完全互換: 公開仕様が流動的なため、最小限の互換構造（states＋spritesheet）に留める。
