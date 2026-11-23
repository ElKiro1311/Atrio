"""Draws Atrio's logo: an entrance arch that makes an A, on a rounded square."""
import os

import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.tools', 'py'))
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SOURCES = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'source'))
S = 1024


def gradient(size, a, b):
    img = Image.new('RGB', (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * size - 2)
            px[x, y] = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return img


def symbol():
    """The symbol's mask: outer arch, inner hole and crossbar."""
    m = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(m)
    left, right, top, bottom = 232, 792, 196, 836
    radius = (right - left) // 2
    d.ellipse((left, top, right, top + 2 * radius), fill=255)
    d.rectangle((left, top + radius, right, bottom), fill=255)
    thickness = 112
    i_left, i_right = left + thickness, right - thickness
    i_radius = (i_right - i_left) // 2
    i_top = top + thickness
    d.ellipse((i_left, i_top, i_right, i_top + 2 * i_radius), fill=0)
    d.rectangle((i_left, i_top + i_radius, i_right, bottom), fill=0)
    # The A's crossbar
    d.rectangle((i_left - 4, 560, i_right + 4, 560 + 86), fill=255)
    return m


def icon(a=(99, 120, 255), b=(168, 92, 255), white=(255, 255, 255)):
    background = gradient(S, a, b).convert('RGBA')
    mask = Image.new('L', (S * 2, S * 2), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S * 2 - 1, S * 2 - 1), radius=460, fill=255)
    mask = mask.resize((S, S), Image.LANCZOS)
    background.putalpha(mask)

    sym = symbol()
    shadow = Image.new('RGBA', (S, S), (10, 8, 40, 0))
    shadow.putalpha(sym.point(lambda v: int(v * 0.35)).filter(ImageFilter.GaussianBlur(18)))
    layer = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    layer.alpha_composite(shadow, (0, 14))
    fill = Image.new('RGBA', (S, S), white + (255,))
    fill.putalpha(sym)
    layer.alpha_composite(fill)
    background.alpha_composite(layer)
    return background


def monochrome(color):
    """Only the symbol, for the system tray."""
    img = Image.new('RGBA', (S, S), color + (0,))
    img.putalpha(symbol())
    return img


def save_ico(img, path):
    img.save(path, format='ICO', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


logo = icon()
gray = icon((64, 68, 80), (40, 44, 54), (205, 208, 216))

for theme in ('Playnite.DesktopApp/Themes/Desktop/Default/Images', 'Playnite.FullscreenApp/Themes/Fullscreen/Default/Images'):
    path = os.path.join(SOURCES, theme)
    save_ico(logo, os.path.join(path, 'applogo.ico'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(path, 'applogo.png'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(path, 'applogo_clean.png'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(path, 'applogo_full.png'))
    gray.resize((256, 256), Image.LANCZOS).save(os.path.join(path, 'applogo_dark.png'))
    save_ico(monochrome((255, 255, 255)), os.path.join(path, 'applogo_white.ico'))
    save_ico(monochrome((16, 16, 20)), os.path.join(path, 'applogo_black.ico'))

# Cover of a game that has none
cover = gradient(620, (38, 41, 50), (20, 21, 26)).crop((82, 0, 538, 620)).convert('RGBA')
cover.alpha_composite(monochrome((96, 100, 114)).resize((110, 110), Image.LANCZOS), (173, 255))
for theme in ('Playnite.DesktopApp/Themes/Desktop/Default/Images', 'Playnite.FullscreenApp/Themes/Fullscreen/Default/Images'):
    cover.save(os.path.join(SOURCES, theme, 'custom_cover_background.png'))

# Splash screen
for project in ('Playnite.DesktopApp', 'Playnite.FullscreenApp'):
    path = os.path.join(SOURCES, project, 'SplashScreen.png')
    width, height = Image.open(path).size
    canvas = Image.new('RGBA', (width, height), (15, 17, 21, 255))
    side = int(min(width, height) * 0.42)
    canvas.alpha_composite(logo.resize((side, side), Image.LANCZOS), ((width - side) // 2, int(height * 0.20)))
    d = ImageDraw.Draw(canvas)
    font = ImageFont.truetype(os.path.join(SOURCES, '..', 'atrio', 'themes', 'fonts', 'Poppins-SemiBold.ttf'), int(height * 0.11))
    box = d.textbbox((0, 0), 'Atrio', font=font)
    d.text(((width - (box[2] - box[0])) / 2, int(height * 0.20) + side + int(height * 0.05)), 'Atrio', font=font, fill=(243, 244, 246))
    canvas.save(path)
    print(path, width, height)
