# カスタムペット作成ガイド

claude-signal-tray のペットモードで使えるカスタムペットの仕様書です。画像生成AI・ドット絵エディタ・スクリプトなど、作成手段を問わずこのドキュメントの仕様を満たせば動作します。AIに作成を依頼する場合は、このファイルをそのまま渡してください。

## 必要なファイル（2つ）

```
%LOCALAPPDATA%\ClaudeSignalTray\pets\<ペット名>\
    pet.json          … 定義ファイル
    spritesheet.png   … スプライトシート画像
```

配置後、トレイメニュー →「ペットの選択」に即座に表示されます（再起動不要）。定義が壊れている場合は無視され、組み込みペットにフォールバックします。

## spritesheet.png の仕様

| 項目 | 仕様 |
| --- | --- |
| 形式 | PNG（**背景は透過必須**） |
| 構造 | グリッド状。**横方向=アニメーションのフレーム、縦方向=状態（row）** |
| フレームサイズ | 自由（既定は32×32px）。全フレーム同一サイズであること |
| 画像全体のサイズ | 幅 = frameWidth × 最大フレーム数、高さ = frameHeight × 行数 |
| 描画スタイル | **ピクセルアート推奨**。表示時に2倍/3倍へ最近傍補間（ニアレストネイバー）で拡大されるため、アンチエイリアスの強い絵はジャギーが目立つ |
| フレーム数 | 行ごとに自由（既定は6）。ループ再生される |

例: 32×32px・6フレーム・4状態 → 192×128px の画像1枚。

```
[idle f0][idle f1][idle f2][idle f3][idle f4][idle f5]   ← row 0
[run  f0][run  f1][run  f2][run  f3][run  f4][run  f5]   ← row 1
[wave f0][wave f1][wave f2][wave f3][wave f4][wave f5]   ← row 2
[off  f0][off  f1][off  f2][off  f3][off  f4][off  f5]   ← row 3
```

## pet.json の仕様

```json
{
  "name": "my-pet",
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

| キー | 内容 |
| --- | --- |
| `name` | 表示名（任意の文字列） |
| `frameWidth` / `frameHeight` | 1フレームのピクセルサイズ |
| `frameDurationMs` | 1フレームの表示時間（ミリ秒）。180前後が自然。最小30 |
| `states.<状態名>.row` | その状態が使うスプライトシートの行（0始まり） |
| `states.<状態名>.frames` | その行のフレーム数 |

状態名の大文字小文字は区別されません。

## 4つの状態とデザイン意図

| 状態名 | いつ表示されるか | デザインの指針 |
| --- | --- | --- |
| `idle` | Claudeが待機中/完了 | 寝ている・くつろいでいる。**控えめで静かな動き**（呼吸、Zzz など） |
| `run` | Claudeが作業中 | 走る・作業する・タイピングする。活動感のある動き |
| `wave` | **確認待ちの可能性（最重要）** | 手を振る・跳ねる・「！」マーク等。**周辺視野でも気づける目立つ動き**にする。このツールの存在意義はこの状態に気づかせること |
| `off` | Claudeプロセス未検出 | 灰色・モノクロで気配を消す（電源オフ感） |

`idle` 以外は省略可能です。未定義の状態は `idle` にフォールバックします（最低1状態あれば動作します）。

## 画像生成AIへの依頼プロンプト例

そのまま使える英語プロンプトの例（キャラクター部分を差し替えて使用）:

```
Create a pixel art sprite sheet for a desktop pet, PNG with transparent
background, exactly 192x128 pixels: a grid of 6 frames (columns, 32x32 each)
x 4 rows. The character is a [shiba inu dog].
Row 1 (idle): sleeping curled up, gentle breathing, floating "Z" letters.
Row 2 (run): running in place with bouncing motion, legs alternating.
Row 3 (wave): standing and waving one paw energetically, a red "!" mark
appearing in alternating frames.
Row 4 (off): same sleeping pose as row 1 but entirely in gray tones, no "Z".
Clean 1-pixel dark outline, flat colors, no anti-aliasing, no background,
consistent character size and position across all frames.
```

### 生成AIを使うときの注意

- **グリッドのズレが最頻出の失敗。** 生成後、必ず32px間隔で切り出して各フレームのキャラ位置が揃っているか確認する。ズレている場合は画像編集ソフトで整列するか、フレーム単位で生成して自分で並べる方が確実
- サイズ指定を無視されたら、生成後に192×128へ**最近傍補間で**リサイズする（通常の補間だとドットが潰れる）
- 背景が透過にならない場合は、背景除去ツールで白/単色背景を透過化する
- 4行を一度に生成できないAIなら、1行（1状態）ずつ生成して縦に連結してもよい。行ごとにファイルを分けることはできないため、最終的に1枚のPNGに結合する

## 動作確認の手順

1. `pets\<名前>\` に2ファイルを配置
2. トレイアイコン右クリック →「ペットの選択」→ 作成したペット名を選択
3. 各状態の見え方を確認: Claude未起動なら `off`、Coworkにタスクを投げると `run`、確認ダイアログ表示中に `wave`、放置で `idle`
4. 表示されない場合: pet.json の構文エラー（JSONとして不正）、`frameWidth`×`frames` が画像幅を超えている、PNGが壊れている、のいずれかが典型。組み込みペットに戻っていたら定義を見直す

## 参考

- 組み込みペットの実物: 初回起動後の `pets\default\`（ねこ）と `pets\bat\`（オレンジ眼鏡のコウモリ）
- プログラム生成スクリプトの例: [scripts/gen_default_pet.py](../scripts/gen_default_pet.py) / [scripts/gen_bat_pet.py](../scripts/gen_bat_pet.py)
- 設計の背景: [pet-overlay-design.md](pet-overlay-design.md)

なお `idle` を「ぶら下がって寝る」のような上寄せの絵にする場合（コウモリ等）、全状態でキャラの位置が大きくズレないよう、フレーム内での重心をある程度揃えると切り替え時の跳びが目立ちません。
