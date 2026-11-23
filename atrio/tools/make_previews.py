"""Turns each theme's captures into the Themes menu's preview and into the README's images.

Usage: make_previews.py <folder of captures> [en|es]   (the language they are in; en by default)
"""
import os
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, '.tools', 'py'))
from PIL import Image

if len(sys.argv) < 2:
    raise SystemExit(__doc__)
CAPTURES = sys.argv[1]
LANGUAGE = sys.argv[2] if len(sys.argv) > 2 else 'en'
FOLDER = () if LANGUAGE == 'en' else (LANGUAGE,)
PREVIEWS = os.path.join(ROOT, 'atrio', 'themes', 'previews', *FOLDER)
README = os.path.join(ROOT, 'atrio', 'screenshots', *FOLDER)
os.makedirs(PREVIEWS, exist_ok=True)
os.makedirs(README, exist_ok=True)

# Each theme is presented with the screen that shows its layout best.
THEMES = {
    'AtrioNoche': 'home', 'AtrioAurora': 'home', 'AtrioOLED': 'library',
    'Cobalto': 'library', 'Oliva': 'library',
    'Grafito': 'library', 'GrafitoCompacto': 'library', 'Default': 'library',
}

for theme, screen in THEMES.items():
    img = Image.open(os.path.join(CAPTURES, f'theme-{theme}-{screen}.png')).convert('RGB')
    img.thumbnail((760, 760), Image.LANCZOS)
    img.save(os.path.join(PREVIEWS, f'{theme}.jpg'), quality=88, optimize=True)

for source, target in (('theme-AtrioNoche-home', 'home'), ('theme-AtrioNoche-library', 'library')):
    img = Image.open(os.path.join(CAPTURES, f'{source}.png')).convert('RGB')
    img.thumbnail((1280, 1280), Image.LANCZOS)
    img.save(os.path.join(README, f'{target}.jpg'), quality=86, optimize=True)

print('ok', len(THEMES))
