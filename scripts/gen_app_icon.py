#!/usr/bin/env python3
"""実行ファイル用アイコン(icon.ico)を生成する。

モチーフ: オレンジの丸眼鏡をかけたコウモリ。
256pxで描画し、16/24/32/48/64/128/256の各サイズを内包するICOを出力する。

再生成:
    python3 scripts/gen_app_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

# パレット
BODY = (43, 45, 66, 255)        # コウモリ本体(ダークネイビー)
BODY_D = (24, 26, 42, 255)      # 輪郭・翼の影
EAR_IN = (68, 71, 102, 255)     # 耳の内側
ORANGE = (245, 132, 42, 255)    # 眼鏡フレーム
ORANGE_D = (200, 95, 20, 255)   # 眼鏡フレームの影
LENS = (255, 243, 224, 255)     # レンズ
GLINT = (255, 255, 255, 220)    # レンズのハイライト
FANG = (255, 255, 255, 255)


def draw_bat(size: int) -> Image.Image:
    """指定サイズのコウモリアイコンを描く(256基準の座標をスケール)。"""
    img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # --- 翼(左右対称) ---
    # 上縁は肩から翼端へ、下縁は2つのスカラップ(切れ込み)を持つ
    left_wing = [
        (86, 118), (30, 76), (6, 128),      # 肩→翼端上→翼端下
        (40, 132), (52, 168),               # スカラップ1
        (74, 158), (92, 186),               # スカラップ2
        (104, 158),
    ]
    right_wing = [(256 - x, y) for x, y in left_wing]
    d.polygon(left_wing, fill=BODY_D)
    d.polygon(right_wing, fill=BODY_D)

    # --- 耳 ---
    d.polygon([(84, 92), (66, 18), (126, 62)], fill=BODY, outline=BODY_D)
    d.polygon([(172, 92), (190, 18), (130, 62)], fill=BODY, outline=BODY_D)
    d.polygon([(88, 80), (76, 36), (114, 64)], fill=EAR_IN)
    d.polygon([(168, 80), (180, 36), (142, 64)], fill=EAR_IN)

    # --- 胴体(頭の下) ---
    d.ellipse([100, 150, 156, 212], fill=BODY, outline=BODY_D, width=3)
    # 足(小さな2本)
    d.line([(116, 208), (112, 224)], fill=BODY_D, width=6)
    d.line([(140, 208), (144, 224)], fill=BODY_D, width=6)

    # --- 頭 ---
    d.ellipse([64, 56, 192, 184], fill=BODY, outline=BODY_D, width=4)

    # --- 口元(小さな牙と笑い) ---
    d.arc([112, 138, 144, 162], 10, 170, fill=BODY_D, width=4)
    d.polygon([(114, 150), (120, 150), (117, 160)], fill=FANG)
    d.polygon([(136, 150), (142, 150), (139, 160)], fill=FANG)

    # --- オレンジの丸眼鏡 ---
    ring = 11
    # つる(テンプル): フレームから頭の輪郭へ
    d.line([(66, 112), (82, 116)], fill=ORANGE_D, width=8)
    d.line([(190, 112), (174, 116)], fill=ORANGE_D, width=8)
    # ブリッジ
    d.line([(118, 114), (138, 114)], fill=ORANGE, width=9)
    # レンズ(左右)
    for cx in (100, 156):
        d.ellipse([cx - 26, 90, cx + 26, 142], fill=LENS, outline=ORANGE, width=ring)
        # ハイライト
        d.ellipse([cx - 14, 100, cx - 4, 110], fill=GLINT)

    if size == 256:
        return img
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [draw_bat(s) for s in sizes]

    out = Path(__file__).resolve().parent.parent / "icon.ico"
    # 各サイズを個別に描いた画像で構成する(PILは先頭画像+append_imagesでICO化できる)
    images[-1].save(out, format="ICO", append_images=images[:-1],
                    sizes=[(s, s) for s in sizes])
    print(f"saved: {out} (sizes: {sizes})")

    # 目視確認用プレビュー(各サイズを1枚に並べる)
    preview = Image.new("RGBA", (16 + 24 + 32 + 48 + 64 + 128 + 256 + 8 * 8, 280), (245, 245, 245, 255))
    x = 8
    for s, im in zip(sizes, images):
        preview.alpha_composite(im, (x, 260 - s))
        x += s + 8
    preview_path = out.parent / "scripts" / "icon_preview.png"
    preview.convert("RGB").save(preview_path)
    print(f"preview: {preview_path}")


if __name__ == "__main__":
    main()
