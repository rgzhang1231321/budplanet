"""小程序头像生成 v2 —— 精确几何绘制（超采样抗锯齿，无文字无水印）

v2 修正：
- A：去掉画变形的对勾，改为胶囊接缝线（胶囊本来就有辨识度，加勾反而破坏形状）
- C：铃铛用多边形画不出钟形弧线（出来像圣诞树），改为钟面（时间=按时吃药的隐喻）
"""

from PIL import Image, ImageDraw
import os

OUT_DIR = "D:/code/BudPlanet/assets/icon"
os.makedirs(OUT_DIR, exist_ok=True)

S = 2048
F = S / 512.0
FINAL = 512

BLUE = (46, 107, 230)
ORANGE = (245, 115, 30)
GREEN = (18, 161, 80)
WHITE = (255, 255, 255)


def s(v):
    return v * F


def save(img, name):
    up = img.resize((FINAL, FINAL), Image.LANCZOS)
    up.save(os.path.join(OUT_DIR, name + ".png"), "PNG", optimize=True)
    up.resize((144, 144), Image.LANCZOS).save(
        os.path.join(OUT_DIR, name + "-144.png"), "PNG", optimize=True
    )
    print("saved", name)


def icon_a():
    """胶囊：白胶囊倾斜 45 度，中线为接缝 —— 最直白的"药"，96px 下仍是明确的胶囊轮廓"""
    img = Image.new("RGB", (S, S), BLUE)
    lay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)

    cw, ch = 290, 132
    x0, y0 = 256 - cw / 2.0, 256 - ch / 2.0
    d.rounded_rectangle(
        [s(x0), s(y0), s(x0 + cw), s(y0 + ch)],
        radius=int(s(ch / 2.0)), fill=WHITE,
    )
    # 接缝：垂直中线
    d.rectangle(
        [s(256 - 6), s(y0), s(256 + 6), s(y0 + ch)],
        fill=BLUE,
    )

    lay = lay.rotate(-45, resample=Image.BICUBIC, center=(S // 2, S // 2))
    img.paste(lay, (0, 0), lay)
    save(img, "icon-a-capsule")


def icon_b():
    """日历：白卡 + 两个装订环 + 中间圆点标记今天"""
    img = Image.new("RGB", (S, S), ORANGE)
    d = ImageDraw.Draw(img)

    for cx in (206, 306):
        d.rounded_rectangle(
            [s(cx - 20), s(104), s(cx + 20), s(192)],
            radius=int(s(20)), fill=WHITE,
        )
    d.rounded_rectangle(
        [s(98), s(150), s(414), s(418)],
        radius=int(s(52)), fill=WHITE,
    )
    r = 62
    d.ellipse([s(256 - r), s(296 - r), s(256 + r), s(296 + r)], fill=ORANGE)
    save(img, "icon-b-calendar")


def icon_c():
    """钟面：时间 = 按时吃药。形状在 96px 下不会失真（圆是唯一不会画错的形状）"""
    img = Image.new("RGB", (S, S), GREEN)
    d = ImageDraw.Draw(img)

    r = 158
    d.ellipse([s(256 - r), s(256 - r), s(256 + r), s(256 + r)], fill=WHITE)

    # 分针 -> 12 点方向
    d.rectangle([s(256 - 11), s(256 - 108), s(256 + 11), s(256)], fill=GREEN)
    d.ellipse([s(256 - 11), s(256 - 118), s(256 + 11), s(256 - 96)], fill=GREEN)
    # 时针 -> 8 点方向
    d.line([s(256), s(256), s(256 - 62), s(256 + 56)], fill=GREEN, width=int(s(24)))
    d.ellipse([s(256 - 74), s(256 + 44), s(256 - 50), s(256 + 68)], fill=GREEN)
    # 轴心
    d.ellipse([s(256 - 16), s(256 - 16), s(256 + 16), s(256 + 16)], fill=GREEN)
    save(img, "icon-c-clock")


icon_a()
icon_b()
icon_c()

# 移除 v1 的铃铛产物
old = os.path.join(OUT_DIR, "icon-c-bell.png")
if os.path.exists(old):
    os.remove(old)
    alt = os.path.join(OUT_DIR, "icon-c-bell-144.png")
    if os.path.exists(alt):
        os.remove(alt)
print("done")
