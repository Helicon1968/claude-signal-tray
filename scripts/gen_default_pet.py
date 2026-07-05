#!/usr/bin/env python3
"""組み込みデフォルトペット（ねこ）のスプライトシートを生成する。

出力: assets/pets/default/spritesheet.png (192x128 = 32x32 x 6フレーム x 4状態)
行の割り当て: 0=idle(睡眠) 1=run(走る) 2=wave(手を振る) 3=off(未検出・灰色)

ピクセルアートのため補間なしで描画する。再生成:
    python3 scripts/gen_default_pet.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

FW, FH, FRAMES, ROWS = 32, 32, 6, 4

# パレット
BODY = (232, 163, 61, 255)      # オレンジ
BODY_D = (122, 78, 29, 255)     # 輪郭
BELLY = (247, 216, 160, 255)
EYE = (40, 30, 20, 255)
ZZZ = (120, 150, 220, 255)
ALERT = (220, 60, 50, 255)
GRAY = (150, 150, 150, 255)
GRAY_D = (100, 100, 100, 255)
GRAY_L = (200, 200, 200, 255)


def ellipse(d, x0, y0, x1, y1, fill, outline):
    d.ellipse([x0, y0, x1, y1], fill=fill, outline=outline)


def draw_sleep(d, f, gray=False):
    body, dark, belly = (GRAY, GRAY_D, GRAY_L) if gray else (BODY, BODY_D, BELLY)
    breathe = 1 if f in (2, 3) else 0
    # 丸まった体
    ellipse(d, 5, 17 - breathe, 26, 29, body, dark)
    ellipse(d, 9, 20 - breathe, 22, 28, belly, None)
    # 頭（体の左に乗せる）
    ellipse(d, 3, 13 - breathe, 14, 24, body, dark)
    # 耳
    d.polygon([(4, 15 - breathe), (6, 9 - breathe), (9, 14 - breathe)], fill=body, outline=dark)
    d.polygon([(9, 13 - breathe), (12, 8 - breathe), (14, 13 - breathe)], fill=body, outline=dark)
    # 閉じた目
    d.line([(6, 19 - breathe), (8, 19 - breathe)], fill=EYE)
    d.line([(10, 19 - breathe), (12, 19 - breathe)], fill=EYE)
    # しっぽ
    d.arc([18, 22 - breathe, 28, 30], 270, 90, fill=dark)
    if not gray:
        # Zzz（フレームで浮遊）
        if f in (1, 2):
            d.text((20, 6), "z", fill=ZZZ)
        if f in (2, 3, 4):
            d.text((24, 2), "Z", fill=ZZZ)


def draw_run(d, f):
    bounce = [0, -2, -3, -2, 0, -1][f]
    y = bounce
    # 体
    ellipse(d, 7, 14 + y, 25, 26 + y, BODY, BODY_D)
    ellipse(d, 11, 17 + y, 22, 25 + y, BELLY, None)
    # 頭
    ellipse(d, 17, 8 + y, 28, 19 + y, BODY, BODY_D)
    d.polygon([(18, 10 + y), (19, 4 + y), (22, 9 + y)], fill=BODY, outline=BODY_D)
    d.polygon([(23, 9 + y), (26, 4 + y), (27, 10 + y)], fill=BODY, outline=BODY_D)
    # 開いた目
    d.point((21, 13 + y), fill=EYE)
    d.point((25, 13 + y), fill=EYE)
    # 脚（交互）
    if f % 2 == 0:
        d.line([(10, 26 + y), (8, 30 + y)], fill=BODY_D, width=2)
        d.line([(20, 26 + y), (23, 29 + y)], fill=BODY_D, width=2)
    else:
        d.line([(11, 26 + y), (13, 30 + y)], fill=BODY_D, width=2)
        d.line([(21, 26 + y), (19, 30 + y)], fill=BODY_D, width=2)
    # しっぽ（なびく）
    tail_y = 12 + y + (1 if f % 2 else -1)
    d.line([(7, 18 + y), (2, tail_y)], fill=BODY_D, width=2)
    # スピード線
    if f in (1, 2, 4):
        d.line([(1, 20), (4, 20)], fill=(180, 180, 180, 255))
        d.line([(2, 24), (5, 24)], fill=(180, 180, 180, 255))


def draw_wave(d, f):
    # 立ち姿
    ellipse(d, 9, 16, 24, 30, BODY, BODY_D)
    ellipse(d, 12, 19, 21, 29, BELLY, None)
    # 頭
    ellipse(d, 10, 5, 23, 18, BODY, BODY_D)
    d.polygon([(11, 8), (12, 2), (15, 7)], fill=BODY, outline=BODY_D)
    d.polygon([(18, 7), (21, 2), (22, 8)], fill=BODY, outline=BODY_D)
    # 目（ぱっちり）
    d.point((14, 11), fill=EYE)
    d.point((19, 11), fill=EYE)
    d.point((14, 12), fill=EYE)
    d.point((19, 12), fill=EYE)
    # 口
    d.line([(16, 15), (17, 15)], fill=EYE)
    # 振る腕（左右に揺れる）
    if f % 2 == 0:
        d.line([(23, 18), (29, 10)], fill=BODY_D, width=2)
        d.ellipse([28, 8, 31, 11], fill=BODY, outline=BODY_D)
    else:
        d.line([(23, 18), (30, 15)], fill=BODY_D, width=2)
        d.ellipse([29, 13, 32, 16], fill=BODY, outline=BODY_D)
    # ！マーク（点滅）
    if f in (1, 3, 5):
        d.rectangle([4, 3, 6, 9], fill=ALERT)
        d.rectangle([4, 11, 6, 13], fill=ALERT)


def main():
    sheet = Image.new("RGBA", (FW * FRAMES, FH * ROWS), (0, 0, 0, 0))
    for row in range(ROWS):
        for f in range(FRAMES):
            frame = Image.new("RGBA", (FW, FH), (0, 0, 0, 0))
            d = ImageDraw.Draw(frame)
            if row == 0:
                draw_sleep(d, f)
            elif row == 1:
                draw_run(d, f)
            elif row == 2:
                draw_wave(d, f)
            else:
                draw_sleep(d, f, gray=True)
            sheet.paste(frame, (f * FW, row * FH))

    out = Path(__file__).resolve().parent.parent / "assets" / "pets" / "default" / "spritesheet.png"
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)
    print(f"saved: {out} ({sheet.size[0]}x{sheet.size[1]})")


if __name__ == "__main__":
    main()
