#!/usr/bin/env python3
"""組み込みペット「bat」（オレンジ眼鏡のコウモリ、icon.icoと同モチーフ）を生成する。

出力: assets/pets/bat/spritesheet.png (192x128 = 32x32 x 6フレーム x 4状態)
行の割り当て: 0=idle(ぶら下がって睡眠) 1=run(飛行) 2=wave(手を振る) 3=off(未検出・灰色)

再生成:
    python3 scripts/gen_bat_pet.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

FW, FH, FRAMES, ROWS = 32, 32, 6, 4

BODY = (43, 45, 66, 255)
BODY_D = (24, 26, 42, 255)
EAR_IN = (68, 71, 102, 255)
ORANGE = (245, 132, 42, 255)
LENS = (255, 243, 224, 255)
FANG = (255, 255, 255, 255)
ZZZ = (120, 150, 220, 255)
ALERT = (220, 60, 50, 255)
GRAY = (140, 140, 140, 255)
GRAY_D = (95, 95, 95, 255)
GRAY_L = (190, 190, 190, 255)


def glasses(d, cx, cy, gray=False):
    """オレンジの丸眼鏡(3px径レンズ×2)。"""
    frame = GRAY_D if gray else ORANGE
    lens = GRAY_L if gray else LENS
    for ox in (-4, 2):
        d.ellipse([cx + ox, cy - 2, cx + ox + 4, cy + 2], fill=lens, outline=frame)
    d.point((cx, cy), fill=frame)  # ブリッジ


def draw_hang(d, f, gray=False):
    """ぶら下がって寝ている(逆さま・翼をたたむ)。idle/off共用。"""
    body, dark = (GRAY, GRAY_D) if gray else (BODY, BODY_D)
    sway = [0, 0, 1, 1, 0, -1][f]  # ゆっくり揺れる
    x = 16 + sway
    # 足→天井
    d.line([(x - 3, 6), (x - 3, 2)], fill=dark)
    d.line([(x + 3, 6), (x + 3, 2)], fill=dark)
    # たたんだ翼で包まれた体(縦長のしずく型)
    d.ellipse([x - 7, 5, x + 7, 25], fill=body, outline=dark)
    d.line([(x - 2, 7), (x - 3, 20)], fill=dark)  # 翼の折り目
    d.line([(x + 2, 7), (x + 3, 20)], fill=dark)
    # 逆さまの頭(下側)・耳は下向き
    d.polygon([(x - 5, 24), (x - 7, 30), (x - 2, 26)], fill=body, outline=dark)
    d.polygon([(x + 5, 24), (x + 7, 30), (x + 2, 26)], fill=body, outline=dark)
    # 眼鏡(寝ていてもかけている)・閉じ目は省略し眼鏡のみ
    glasses(d, x - 1, 21, gray=gray)
    if not gray:
        if f in (1, 2):
            d.text((24, 8), "z", fill=ZZZ)
        if f in (2, 3, 4):
            d.text((26, 2), "Z", fill=ZZZ)


def wing(d, x, y, up, flip=False, gray=False):
    """飛行用の翼。up: -1=振り下ろし, 0=水平, 1=振り上げ。flip=右翼。"""
    dark = GRAY_D if gray else BODY_D
    tip_y = y - 7 * up
    s = -1 if flip else 1
    pts = [(x, y), (x - s * 11, tip_y), (x - s * 9, y + 3), (x - s * 4, y + 2)]
    d.polygon(pts, fill=dark)


def draw_fly(d, f):
    """飛行(作業中)。翼を上下にはばたく。"""
    flap = [1, 0, -1, -1, 0, 1][f]
    bob = [0, 1, 2, 2, 1, 0][f]
    y = bob
    # 翼(体の後ろ)
    wing(d, 10, 15 + y, flap)
    wing(d, 22, 15 + y, flap, flip=True)
    # 体
    d.ellipse([9, 9 + y, 23, 25 + y], fill=BODY, outline=BODY_D)
    # 耳
    d.polygon([(10, 11 + y), (9, 4 + y), (14, 9 + y)], fill=BODY, outline=BODY_D)
    d.polygon([(22, 11 + y), (23, 4 + y), (18, 9 + y)], fill=BODY, outline=BODY_D)
    d.polygon([(11, 9 + y), (11, 6 + y), (13, 9 + y)], fill=EAR_IN)
    d.polygon([(21, 9 + y), (21, 6 + y), (19, 9 + y)], fill=EAR_IN)
    # 眼鏡・口
    glasses(d, 15, 16 + y)
    d.point((15, 21 + y), fill=BODY_D)
    d.point((16, 21 + y), fill=BODY_D)
    # スピード線
    if f in (0, 3):
        d.line([(2, 26), (5, 26)], fill=(180, 180, 180, 255))


def draw_wave(d, f):
    """ホバリングしながら片翼を振る(確認待ち)。"""
    bob = [0, 1, 0, 1, 0, 1][f]
    y = bob
    # 左翼は控えめに動かす
    wing(d, 10, 16 + y, [0, -1, 0, -1, 0, -1][f])
    # 体
    d.ellipse([9, 10 + y, 23, 26 + y], fill=BODY, outline=BODY_D)
    # 耳
    d.polygon([(10, 12 + y), (9, 5 + y), (14, 10 + y)], fill=BODY, outline=BODY_D)
    d.polygon([(22, 12 + y), (23, 5 + y), (18, 10 + y)], fill=BODY, outline=BODY_D)
    # 眼鏡
    glasses(d, 15, 17 + y)
    # 口(にっこり)と牙
    d.line([(14, 22 + y), (18, 22 + y)], fill=BODY_D)
    d.point((14, 23 + y), fill=FANG)
    d.point((18, 23 + y), fill=FANG)
    # 振る翼(右上に大きく)
    if f % 2 == 0:
        d.polygon([(22, 16 + y), (30, 6 + y), (28, 15 + y)], fill=BODY_D)
    else:
        d.polygon([(22, 16 + y), (31, 12 + y), (28, 18 + y)], fill=BODY_D)
    # ！マーク(点滅)
    if f in (1, 3, 5):
        d.rectangle([3, 3, 5, 8], fill=ALERT)
        d.rectangle([3, 10, 5, 12], fill=ALERT)


def main():
    sheet = Image.new("RGBA", (FW * FRAMES, FH * ROWS), (0, 0, 0, 0))
    for row in range(ROWS):
        for f in range(FRAMES):
            frame = Image.new("RGBA", (FW, FH), (0, 0, 0, 0))
            d = ImageDraw.Draw(frame)
            if row == 0:
                draw_hang(d, f)
            elif row == 1:
                draw_fly(d, f)
            elif row == 2:
                draw_wave(d, f)
            else:
                draw_hang(d, f, gray=True)
            sheet.paste(frame, (f * FW, row * FH))

    out = Path(__file__).resolve().parent.parent / "assets" / "pets" / "bat" / "spritesheet.png"
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)
    print(f"saved: {out} ({sheet.size[0]}x{sheet.size[1]})")


if __name__ == "__main__":
    main()
