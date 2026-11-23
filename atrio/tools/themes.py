"""Generates Atrio's theme folders from atrio/themes/base and atrio/themes/variants.py.

Each variant inherits all the files of the base. If atrio/themes/<folder>/ exists, its files replace the base's (for
example, a top bar instead of the side one).

Usage: themes.py <the application's Themes/Desktop folder>
"""
import glob
import importlib.util
import os
import re
import shutil
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'themes'))
if len(sys.argv) < 2:
    raise SystemExit(__doc__)
OUTPUT = sys.argv[1]

spec = importlib.util.spec_from_file_location('variants', os.path.join(ROOT, 'variants.py'))
variants = importlib.util.module_from_spec(spec)
spec.loader.exec_module(variants)


def read(path):
    with open(path, encoding='utf-8-sig') as f:
        return f.read()


def expand(text, origins, values):
    def include(m):
        for origin in origins:
            path = os.path.join(origin, m.group(1))
            if os.path.exists(path):
                return read(path)
        raise SystemExit(f'The fragment {m.group(1)} does not exist')

    text = re.sub(r'@@include:([^@]+)@@', include, text)

    def value(m):
        if m.group(1) not in values:
            raise SystemExit(f'The value {m.group(1)} is missing')
        return str(values[m.group(1)])

    return re.sub(r'@@([A-Za-z0-9]+)@@', value, text)


def copy_previews(folder):
    """The preview in English is preview.jpg; the ones in other languages, preview.<language>.jpg."""
    for source, target in ((f'{folder}.jpg', 'preview.jpg'), (os.path.join('es', f'{folder}.jpg'), 'preview.es.jpg')):
        source = os.path.join(ROOT, 'previews', source)
        if os.path.exists(source):
            shutil.copyfile(source, os.path.join(OUTPUT, folder, target))


def compose(v):
    values = dict(variants.BASE)
    values.update(v['values'])
    folder = v['folder']
    destination = os.path.join(OUTPUT, folder)
    if os.path.isdir(destination):
        shutil.rmtree(destination)
    os.makedirs(destination, exist_ok=True)

    origins = [os.path.join(ROOT, folder)]
    if v.get('inherits'):
        origins.append(os.path.join(ROOT, v['inherits']))
    origins.append(os.path.join(ROOT, 'base'))
    relatives = set()
    for origin in origins:
        if not os.path.isdir(origin):
            continue
        for current, _, files in os.walk(origin):
            for file in files:
                relatives.add(os.path.relpath(os.path.join(current, file), origin))

    for relative in sorted(relatives):
        if os.path.basename(relative).startswith('_'):
            continue
        source = next(os.path.join(o, relative) for o in origins if os.path.exists(os.path.join(o, relative)))
        target = os.path.join(destination, relative)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        if relative.endswith('.xaml'):
            with open(target, 'w', encoding='utf-8', newline='\n') as f:
                f.write(expand(read(source), origins, values))
        else:
            shutil.copyfile(source, target)

    with open(os.path.join(destination, 'theme.yaml'), 'w', encoding='utf-8') as f:
        f.write(f"Id: {v['id']}\nName: {v['name']}\nAuthor: Atrio\nVersion: 1.0\nMode: Desktop\nThemeApiVersion: 2.10.0\n")

    with open(os.path.join(destination, 'atrio-theme.yaml'), 'w', encoding='utf-8') as f:
        f.write(f"Description: {v['description']}\nFamily: {v['family']}\nOrder: {v['order']}\n")
        for key in ('View', 'Sidebar', 'FilterPanel', 'CoverWidth', 'TitleFont', 'TextFont'):
            if key in v:
                f.write(f'{key}: {v[key]}\n')

    for family in v.get('fonts', []):
        font_folder = os.path.join(destination, 'Fonts')
        os.makedirs(font_folder, exist_ok=True)
        files = glob.glob(os.path.join(ROOT, 'fonts', f'{family}-*.ttf')) + glob.glob(os.path.join(ROOT, 'fonts', f'OFL-{family}.txt'))
        for font in files:
            shutil.copy(font, font_folder)

    copy_previews(folder)


for variant in variants.VARIANTS:
    compose(variant)
    print('theme', variant['folder'])

if os.path.isdir(os.path.join(OUTPUT, 'Default')):
    copy_previews('Default')
