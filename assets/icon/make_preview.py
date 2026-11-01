"""生成头像方案对比图：同一方案在 240 / 圆形 120 / 96 / 64 下的实际观感"""

from PIL import Image, ImageDraw, ImageFont
import os

D = "D:/code/BudPlanet/assets/icon"
BG = (247, 248, 250)
TXT = (65, 77, 90)
SUB = (138, 148, 160)

try:
    f_title = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 28)
    f_sub = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 19)
except Exception:
    f_title = ImageFont.load_default()
    f_sub = ImageFont.load_default()

items = [
    ("icon-a-capsule.png", "A  胶囊", "直白：一看就是吃药"),
    ("icon-b-calendar.png", "B  日历", "温暖：每天记着"),
    ("icon-c-clock.png", "C  钟面", "按时：到点提醒"),
]


def circle(img, size):
    im = img.resize((size, size), Image.LANCZOS).convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, size - 1, size - 1], fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    return out


COL_W = 400
W = COL_W * 3
H = 730
canvas = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(canvas)

for i, (fn, title, sub) in enumerate(items):
    src = Image.open(os.path.join(D, fn)).convert("RGB")
    cx = i * COL_W + COL_W // 2

    big = src.resize((240, 240), Image.LANCZOS)
    canvas.paste(big, (cx - 120, 28))

    c1 = circle(src, 120)
    canvas.paste(c1, (cx - 60, 288), c1)

    s96 = src.resize((96, 96), Image.LANCZOS)
    canvas.paste(s96, (cx - 92, 430))

    s64 = src.resize((64, 64), Image.LANCZOS)
    canvas.paste(s64, (cx + 28, 446))

    d.text((cx - 44, 542), "96px", font=f_sub, fill=SUB, anchor="ma")
    d.text((cx + 60, 542), "64px", font=f_sub, fill=SUB, anchor="ma")

    d.text((cx, 600), title, font=f_title, fill=TXT, anchor="ma")
    d.text((cx, 642), sub, font=f_sub, fill=SUB, anchor="ma")

d.text((W // 2, 698), "上排 240px ｜ 中排 微信圆形裁切 120px ｜ 下排 列表实际尺寸",
       font=f_sub, fill=SUB, anchor="ma")

out = os.path.join(D, "00-头像方案对比.png")
canvas.save(out, "PNG", optimize=True)
print("saved", out)
