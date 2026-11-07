"""Картинки мастера установки PC Toolkit (Inno Setup, тёмный стиль).

Запуск: python installer\\make-wizard-images.py installer\\wizard   (нужен Pillow).

WizardImage-<w>.png  — левая панель страниц «Добро пожаловать» и «Готово» (164:314).
WizardSmall-<s>.png  — значок в правом верхнем углу остальных страниц.
Размеры — области картинок Inno Setup 6.6+ при 100…250 % масштаба.
"""
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter


ACCENT = (0x4C, 0x8D, 0xFF)
TOP = (0x15, 0x1B, 0x28)
BOTTOM = (0x0B, 0x0D, 0x11)
GRID = (0x1F, 0x25, 0x30)

# Значок приложения — тот же рисунок, что в make_icon.py (PcToolkit.ico).
POINTS = [(3, 12), (7, 12), (10, 5), (14, 19), (17, 12), (21, 12)]

def render_icon(size):
    ss = 8
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    radius = S * 9 / 32
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=ACCENT + (255,))
    # Знак крупнее на маленьких размерах, чтобы линия не терялась.
    glyph = S * (0.64 if size <= 24 else 0.58)
    scale = glyph / 24
    off = (S - glyph) / 2
    stroke_px = max(2.2 * scale, (1.7 if size <= 24 else 1.3) * ss)
    pts = [(off + x * scale, off + y * scale) for x, y in POINTS]
    d.line(pts, fill=(255, 255, 255, 255), width=round(stroke_px), joint="curve")
    r = stroke_px / 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


LARGE = [(202, 386), (269, 515), (336, 643), (403, 772), (430, 824), (498, 953), (534, 1022)]
SMALL = [58, 77, 97, 116, 124, 143, 159]


def series(n):
    """Ровная «живая» кривая загрузки — одна и та же для всех размеров."""
    values = []
    for i in range(n):
        t = i / (n - 1)
        v = (0.52
             + 0.16 * math.sin(t * 7.1 + 0.6)
             + 0.09 * math.sin(t * 15.3 + 1.9)
             + 0.05 * math.sin(t * 29.0 + 0.3))
        # к правому краю нагрузка растёт — как запуск игры
        v += 0.18 * max(0.0, (t - 0.72) / 0.28) ** 2
        values.append(min(0.95, max(0.08, v)))
    return values


def large(w, h):
    ss = 4
    W, H = w * ss, h * ss
    img = Image.new("RGB", (W, H))
    px = ImageDraw.Draw(img)
    for y in range(H):
        k = y / (H - 1)
        c = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * k) for i in range(3))
        px.line([(0, y), (W, y)], fill=c)

    # Мягкое голубое свечение за логотипом.
    logo = round(W * 0.40)
    cx, cy = W / 2, H * 0.30
    glow = Image.new("L", (W, H), 0)
    gd = ImageDraw.Draw(glow)
    r = logo * 1.05
    gd.ellipse([cx - r, cy - r, cx + r, cy + r], fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(logo * 0.55))
    img.paste(Image.new("RGB", (W, H), ACCENT), (0, 0), glow)

    # Сетка и график, как на вкладке «Мониторинг».
    top, bottom = H * 0.58, H * 0.92
    d = ImageDraw.Draw(img, "RGBA")
    grid_w = max(1, round(ss * w / 202))
    for k in range(5):
        y = top + (bottom - top) * k / 4
        d.line([(0, y), (W, y)], fill=GRID + (255,), width=grid_w)

    values = series(160)
    pts = [(W * i / (len(values) - 1), bottom - (bottom - top) * v) for i, v in enumerate(values)]

    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).polygon(pts + [(W, bottom), (0, bottom)], fill=255)
    fade = Image.new("L", (W, H), 0)
    fd = ImageDraw.Draw(fade)
    for y in range(int(top), int(bottom) + 1):
        fd.line([(0, y), (W, y)], fill=round(80 * (1 - (y - top) / (bottom - top))))
    alpha = Image.composite(fade, Image.new("L", (W, H), 0), mask)
    img.paste(Image.new("RGB", (W, H), ACCENT), (0, 0), alpha)

    line_w = round(W * 0.011)
    d = ImageDraw.Draw(img, "RGBA")
    d.line(pts, fill=ACCENT + (255,), width=line_w, joint="curve")

    img = img.resize((w, h), Image.LANCZOS)

    # Логотип поверх свечения — рисуется сразу в итоговом размере (у значка своё сглаживание).
    icon = render_icon(round(w * 0.40))
    img.paste(icon, (round(w / 2 - icon.width / 2), round(h * 0.30 - icon.height / 2)), icon)
    return img


def main(out):
    os.makedirs(out, exist_ok=True)
    for w, h in LARGE:
        large(w, h).save(os.path.join(out, f"WizardImage-{w}.png"), optimize=True)
    for s in SMALL:
        render_icon(s).save(os.path.join(out, f"WizardSmall-{s}.png"), optimize=True)


if __name__ == "__main__":
    main(sys.argv[1])
