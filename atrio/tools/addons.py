"""Prepares the add-ons that come with Atrio.

  addons.py install <folder>   Downloads the packages pinned in list.json, checks their hash, unpacks them in
                               <folder>/Extensions and writes their licenses.
  addons.py update             Looks for the latest compatible version of each one in the public add-on database and
                               updates list.json. It needs PyYAML.

Only add-ons whose license allows redistributing them are included. The others are downloaded from Atrio's Stores page,
when the user asks for them.
"""
import glob
import hashlib
import io
import json
import os
import re
import shutil
import sys
import urllib.request
import zipfile
from xml.sax.saxutils import escape

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
LIST = os.path.join(ROOT, 'atrio', 'addons', 'list.json')
LICENSES = os.path.join(ROOT, 'atrio', 'addons', 'licenses')
TRANSLATIONS = os.path.join(ROOT, 'atrio', 'addons', 'translations')
CACHE = os.path.join(ROOT, '.tools', 'addons')
ADDON_DATABASE = 'https://codeload.github.com/JosefNemec/PlayniteAddonDatabase/zip/refs/heads/master'
SDK = (6, 18, 0)


def download(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'Atrio-build'})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


def read_list():
    with open(LIST, encoding='utf-8') as f:
        return json.load(f)


def package(item):
    path = os.path.join(CACHE, f"{re.sub(r'[^A-Za-z0-9_.-]', '_', item['id'])}-{item['version']}.pext")
    if os.path.exists(path):
        with open(path, 'rb') as f:
            data = f.read()
    else:
        data = download(item['package'])
    digest = hashlib.sha256(data).hexdigest()
    if digest != item['sha256']:
        raise SystemExit(f"The hash of {item['name']} does not match: {digest}")
    os.makedirs(CACHE, exist_ok=True)
    with open(path, 'wb') as f:
        f.write(data)
    return data


def present(item, folder):
    """Atrio doesn't show another program's name and doesn't call its services.

    The licenses in addon-licenses.txt keep the original names and authors.
    """
    path = os.path.join(folder, 'extension.yaml')
    with open(path, encoding='utf-8-sig', newline='') as f:
        text = f.read()
    text = re.sub(r'^Author:[ \t]*Playnite[ \t]*(\r?)$', r'Author: Josef Nemec\1', text, flags=re.M)
    if item.get('shown_name'):
        text = re.sub(r'^Name:.*?(\r?)$', lambda m: 'Name: ' + item['shown_name'] + m.group(1), text, flags=re.M)
    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write(text)

    path = os.path.join(folder, 'plugin.cfg')
    if os.path.exists(path):
        with open(path, encoding='utf-8-sig') as f:
            settings = json.load(f)
        for key, value in settings.items():
            if isinstance(value, str) and 'playnite.link' in value:
                settings[key] = 'http://127.0.0.1:9/'
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(settings, f, indent=2)


def translate(item, folder):
    """Puts the translations of atrio/addons/translations/<id>.<language>.json on top."""
    for path in glob.glob(os.path.join(TRANSLATIONS, item['id'] + '.*.json')):
        language = os.path.basename(path).split('.')[1]
        target = os.path.join(folder, 'Localization', language + '.xaml')
        with open(path, encoding='utf-8') as f:
            texts = json.load(f)
        with open(target, encoding='utf-8-sig', newline='') as f:
            xaml = f.read()
        done = 0
        for key, text in texts.items():
            pattern = r'(x:Key="LOCPlayAch_' + re.escape(key) + r'"[^>]*>).*?(</sys:String>)'
            # Line breaks go as &#10;, as in the add-on's original texts.
            value = escape(text).replace('\r\n', '\n').replace('\n', '&#10;')
            xaml, count = re.subn(pattern, lambda m: m.group(1) + value + m.group(2), xaml, count=1, flags=re.S)
            done += count
        with open(target, 'w', encoding='utf-8', newline='') as f:
            f.write(xaml)
        print('translated', item['id'], language, done, 'of', len(texts))


def install(destination):
    extensions = os.path.join(destination, 'Extensions')
    os.makedirs(extensions, exist_ok=True)
    items = read_list()
    for item in items:
        folder = os.path.join(extensions, re.sub(r'[<>:"/\\|?*]', '_', item['id']))
        if os.path.isdir(folder):
            shutil.rmtree(folder)
        with zipfile.ZipFile(io.BytesIO(package(item))) as z:
            z.extractall(folder)
        for current, _, files in os.walk(folder):
            for file in files:
                if file.lower().endswith('.pdb'):
                    os.remove(os.path.join(current, file))
        # Files of the package whose license isn't stated: they aren't redistributed.
        for relative in item.get('remove', []):
            os.remove(os.path.join(folder, *relative.split('/')))
        translate(item, folder)
        present(item, folder)
        print('add-on', item['name'], item['version'])

    texts = {}
    with open(os.path.join(destination, 'addon-licenses.txt'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('Add-ons included with Atrio. They are the work of their authors and keep their licenses.\n\n')
        for item in items:
            f.write(f"{item['name']} {item['version']}\n  Author: {item['author']}\n  License: {item['license']}\n"
                    f"  Code: {item['code']}\n  Text: {item['license_file']}\n\n")
            texts.setdefault(item['license_file'], None)
        for name in sorted(texts):
            with open(os.path.join(LICENSES, name), encoding='utf-8') as source:
                f.write(f"{'-' * 80}\n{name}\n\n{source.read().strip()}\n\n")


def version(text):
    return tuple(int(x) for x in re.findall(r'\d+', str(text))[:3])


def update():
    sys.path.insert(0, os.path.join(ROOT, '.tools', 'py'))
    import yaml

    database = {}
    with zipfile.ZipFile(io.BytesIO(download(ADDON_DATABASE))) as z:
        for name in z.namelist():
            if '/addons/' in name and name.endswith('.yaml'):
                data = yaml.safe_load(z.read(name).decode('utf-8-sig'))
                if isinstance(data, dict) and data.get('AddonId'):
                    database[data['AddonId']] = data

    items = read_list()
    for item in items:
        data = database.get(item['database_id'])
        if not data:
            print('NOT IN THE DATABASE:', item['name'])
            continue
        manifest = download(data['InstallerManifestUrl']).decode('utf-8-sig').replace('\t', '  ')
        packages = [p for p in yaml.safe_load(manifest).get('Packages', []) if version(p.get('RequiredApiVersion', '0')) <= SDK]
        packages.sort(key=lambda p: version(p.get('Version', '0')), reverse=True)
        if not packages:
            print('NO COMPATIBLE VERSION:', item['name'])
            continue
        chosen = packages[0]
        if str(chosen['Version']) == item.get('version') and item.get('sha256'):
            continue
        content = download(chosen['PackageUrl'])
        item.update(version=str(chosen['Version']), package=chosen['PackageUrl'], sha256=hashlib.sha256(content).hexdigest())
        print('updated', item['name'], item['version'])

    with open(LIST, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(items, f, ensure_ascii=False, indent=2)
        f.write('\n')


if __name__ == '__main__':
    command = sys.argv[1] if len(sys.argv) > 1 else ''
    if command == 'install' and len(sys.argv) > 2:
        install(sys.argv[2])
    elif command == 'update':
        update()
    else:
        raise SystemExit(__doc__)
