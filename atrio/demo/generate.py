"""Makes a made-up library for the project's screenshots: games that don't exist and art generated here.

It's for showing Atrio without using the covers of real games or anybody's library.

Usage: generate.py <output folder> [en|es]   (the language of the genres and the descriptions; en by default)
"""
import hashlib
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.tools', 'py'))
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(ROOT, '..', 'themes', 'fonts')

STORES = {
    'steam': 'cb91dfc9-b977-43bf-8e70-55f46e410fab',
    'epic': '00000002-dbd1-46c6-b5d0-b1ba559d10e4',
    'gog': 'aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e',
    'xbox': '7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287',
    'ubisoft': 'c2f038e5-8b92-4877-91f1-da9094155fc5',
    'ea': '85dd7072-2f20-4e76-a007-41035e390724',
    'battlenet': 'e3c26a3d-d695-4cb7-a769-5ff7612c7edd',
    'amazon': '402674cd-4af6-4886-b6ec-0e695bfa0688',
    'itch': '00000001-ebb2-4eec-abcb-7c89937a42bb',
}

# Name (in Spanish, so that it isn't the name of any real game), studio, genre, year, stores, installed, hours, how many
# days ago it was played (0 = never)
GAMES = [
    ('Ecos de Vidrio', 'Estudio Lucerna', 'adventure', 2024, ['steam', 'epic', 'gog'], True, 62, 1),
    ('Marea Baja', 'Faro Norte', 'rpg', 2023, ['steam', 'gog'], True, 148, 2),
    ('Cometa Nueve', 'Órbita Baja', 'strategy', 2025, ['epic'], True, 31, 3),
    ('Los Relojeros de Arena', 'Tres Lunas', 'puzzle', 2022, ['gog', 'steam'], False, 12, 40),
    ('Jardín Binario', 'Ciprés Digital', 'simulation', 2024, ['steam'], True, 87, 5),
    ('Salitre', 'Faro Norte', 'action', 2021, ['xbox', 'steam'], False, 25, 70),
    ('Nube Férrea', 'Hangar 12', 'racing', 2023, ['epic', 'steam', 'ea'], True, 44, 7),
    ('Taller de Tormentas', 'Estudio Lucerna', 'building', 2025, ['steam'], True, 210, 1),
    ('Pulso Austral', 'Órbita Baja', 'shooter', 2022, ['battlenet'], False, 0, 0),
    ('Sombra de Cobre', 'Tres Lunas', 'stealth', 2020, ['gog'], False, 18, 120),
    ('Atlas de Humo', 'Hangar 12', 'adventure', 2024, ['ubisoft', 'steam'], False, 9, 200),
    ('La Hora Azul', 'Ciprés Digital', 'narrative', 2023, ['itch'], True, 6, 12),
    ('Bruma Dorada', 'Faro Norte', 'rpg', 2025, ['steam', 'epic'], False, 0, 0),
    ('Fragua Lunar', 'Estudio Lucerna', 'action', 2021, ['amazon'], False, 3, 300),
    ('Cartógrafos del Abismo', 'Órbita Baja', 'exploration', 2024, ['steam', 'gog', 'epic'], True, 73, 9),
    ('Hilo Rojo', 'Tres Lunas', 'puzzle', 2019, ['epic'], False, 2, 400),
    ('Vértice', 'Hangar 12', 'racing', 2022, ['xbox'], False, 15, 90),
    ('Dunas de Neón', 'Ciprés Digital', 'action', 2025, ['steam'], True, 39, 14),
    ('El Último Faro', 'Faro Norte', 'narrative', 2020, ['gog', 'amazon'], False, 11, 150),
    ('Ceniza y Sal', 'Estudio Lucerna', 'rpg', 2023, ['steam'], False, 0, 0),
    ('Mapa de Estrellas', 'Órbita Baja', 'strategy', 2021, ['epic', 'gog'], False, 28, 60),
    ('Cristal Hondo', 'Tres Lunas', 'adventure', 2024, ['ea'], False, 0, 0),
    ('Motor de Niebla', 'Hangar 12', 'simulation', 2022, ['steam'], True, 120, 20),
    ('Tinta Viva', 'Ciprés Digital', 'platformer', 2023, ['itch', 'steam'], False, 8, 33),
    ('Río Subterráneo', 'Faro Norte', 'exploration', 2020, ['ubisoft'], False, 0, 0),
    ('Corona de Musgo', 'Estudio Lucerna', 'rpg', 2025, ['gog'], False, 0, 0),
    ('Ruta Polar', 'Órbita Baja', 'simulation', 2019, ['steam', 'xbox'], False, 54, 180),
    ('Aguja del Norte', 'Tres Lunas', 'stealth', 2022, ['amazon', 'epic'], False, 1, 500),
    ('Bosque Cuántico', 'Ciprés Digital', 'puzzle', 2024, ['steam'], True, 17, 25),
    ('Eclipse Tranquilo', 'Hangar 12', 'narrative', 2021, ['epic'], False, 5, 250),
]

TEXTS = {
    'en': {
        'description': '<p>{} is a made-up game to show Atrio. It doesn\'t exist.</p>',
        'adventure': 'Adventure', 'rpg': 'RPG', 'strategy': 'Strategy', 'puzzle': 'Puzzle', 'simulation': 'Simulation',
        'action': 'Action', 'racing': 'Racing', 'building': 'Building', 'shooter': 'Shooter', 'stealth': 'Stealth',
        'narrative': 'Narrative', 'exploration': 'Exploration', 'platformer': 'Platformer',
    },
    'es': {
        'description': '<p>{} es un juego inventado para enseñar Atrio. No existe.</p>',
        'adventure': 'Aventura', 'rpg': 'Rol', 'strategy': 'Estrategia', 'puzzle': 'Puzle', 'simulation': 'Simulación',
        'action': 'Acción', 'racing': 'Carreras', 'building': 'Construcción', 'shooter': 'Disparos', 'stealth': 'Sigilo',
        'narrative': 'Narrativa', 'exploration': 'Exploración', 'platformer': 'Plataformas',
    },
}

PALETTES = [
    ('#0F1B3D', '#3B4BA8', '#F2A65A', '#FFE3B3'),
    ('#10231F', '#1F6F5C', '#8FD694', '#F4F1BB'),
    ('#2A0F3F', '#7B2CBF', '#F15BB5', '#FEE440'),
    ('#1B1B2F', '#E43F5A', '#F9A826', '#FFF1C1'),
    ('#07222F', '#0E7C86', '#5EEAD4', '#E0FFFA'),
    ('#2B1A12', '#A4512A', '#E6A157', '#FBE7C6'),
    ('#101820', '#2C5F8A', '#7EC8E3', '#E8F7FF'),
    ('#1F0A1E', '#6C1D45', '#D1495B', '#EDAE49'),
]


def color(text):
    return tuple(int(text[i:i + 2], 16) for i in (1, 3, 5))


def mix(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def sky(width, height, a, b, c):
    img = Image.new('RGB', (1, height))
    for y in range(height):
        t = y / max(1, height - 1)
        img.putpixel((0, y), mix(a, b, t * 2) if t < .5 else mix(b, c, (t - .5) * 2))
    return img.resize((width, height))


def ridge(rnd, width, base, amplitude):
    phases = [(rnd.uniform(.5, 1.5), rnd.uniform(0, 6.28), rnd.uniform(.4, 1)) for _ in range(5)]
    points = []
    for x in range(0, width + 8, 8):
        y = sum(math.sin(x / width * 6.28 * f * (i + 1) + p) * w / (i + 1) for i, (f, p, w) in enumerate(phases))
        points.append((x, base + y * amplitude))
    return points


def mountains(d, rnd, width, height, tones):
    d.ellipse((width * .58, height * .14, width * .58 + height * .2, height * .34), fill=tones[3])
    for layer in range(4):
        base = height * (.5 + layer * .12)
        points = ridge(rnd, width, base, height * (.05 + .02 * (3 - layer)))
        d.polygon(points + [(width, height), (0, height)], fill=mix(tones[1], tones[0], layer / 3))


def waves(d, rnd, width, height, tones):
    for i in range(16):
        base = height * (.25 + i * .05)
        points = ridge(rnd, width, base, height * .03)
        d.line(points, fill=mix(tones[3], tones[1], i / 15), width=max(2, height // 240))


def crystals(d, rnd, width, height, tones):
    for _ in range(26):
        x, y = rnd.uniform(0, width), rnd.uniform(height * .1, height)
        r = rnd.uniform(height * .05, height * .22)
        sides = rnd.choice((3, 4, 5, 6))
        turn = rnd.uniform(0, 6.28)
        points = [(x + math.cos(turn + i * 6.28 / sides) * r, y + math.sin(turn + i * 6.28 / sides) * r) for i in range(sides)]
        d.polygon(points, fill=mix(tones[rnd.randrange(4)], tones[0], rnd.uniform(.2, .7)))


def city(d, rnd, width, height, tones):
    d.ellipse((width * .2, height * .12, width * .2 + height * .3, height * .42), fill=tones[2])
    for layer in range(3):
        x = -10
        while x < width:
            w = rnd.uniform(width * .05, width * .13)
            h = rnd.uniform(height * .15, height * .5) * (1 - layer * .2)
            tone = mix(tones[1], tones[0], .3 + layer * .33)
            d.rectangle((x, height - h, x + w, height), fill=tone)
            for vy in range(int(height - h) + 12, height, max(14, height // 60)):
                for vx in range(int(x) + 8, int(x + w) - 6, max(14, width // 50)):
                    if rnd.random() < .22:
                        d.rectangle((vx, vy, vx + 4, vy + 6), fill=tones[3])
            x += w + rnd.uniform(2, 14)


SCENES = [mountains, waves, crystals, city]


def scene(name, width, height):
    seed = int(hashlib.sha1(name.encode('utf-8')).hexdigest(), 16)
    rnd = random.Random(seed)
    tones = [color(c) for c in PALETTES[seed % len(PALETTES)]]
    img = sky(width, height, tones[0], tones[1], tones[2])
    SCENES[(seed // 7) % len(SCENES)](ImageDraw.Draw(img), rnd, width, height, tones)
    return img, tones


def cover(name):
    img, tones = scene(name, 600, 800)
    veil = Image.new('L', (600, 800), 0)
    d = ImageDraw.Draw(veil)
    for y in range(440, 800):
        d.line((0, y, 600, y), fill=int((y - 440) / 360 * 225))
    img = Image.composite(Image.new('RGB', img.size, tones[0]), img, veil)
    d = ImageDraw.Draw(img)
    family = 'Poppins-Bold.ttf' if len(name) % 2 else 'Barlow-Bold.ttf'
    words, lines, current = name.upper().split(), [], ''
    font = ImageFont.truetype(os.path.join(FONTS, family), 56)
    for word in words:
        attempt = (current + ' ' + word).strip()
        if d.textlength(attempt, font=font) > 500 and current:
            lines.append(current)
            current = word
        else:
            current = attempt
    lines.append(current)
    y = 752 - len(lines) * 70
    for line in lines:
        d.text((40, y), line, font=font, fill=tones[3])
        y += 70
    return img


def background(name):
    img, _ = scene(name, 1920, 1080)
    return img.filter(ImageFilter.GaussianBlur(3))


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    output = sys.argv[1]
    texts = TEXTS[sys.argv[2] if len(sys.argv) > 2 else 'en']
    os.makedirs(output, exist_ok=True)
    entries = []
    for index, (name, studio, genre, year, stores, installed, hours, days) in enumerate(GAMES):
        front, back = f'c{index:02}.jpg', f'b{index:02}.jpg'
        cover(name).save(os.path.join(output, front), quality=88, optimize=True)
        background(name).save(os.path.join(output, back), quality=82, optimize=True)
        for order, store in enumerate(stores):
            entries.append({
                'Name': name, 'GameId': f'demo-{index}-{store}', 'Store': STORES[store],
                'Cover': front, 'Background': back, 'Developer': studio, 'Genre': texts[genre],
                'Description': texts['description'].format(name),
                # Only the first store has the game installed, as happens in a real library.
                'Installed': installed and order == 0, 'Playtime': hours * 3600 if order == 0 else 0,
                'Year': year, 'DaysAgo': days if order == 0 else 0,
            })
    with open(os.path.join(output, 'demo.json'), 'w', encoding='utf-8') as f:
        json.dump(entries, f, ensure_ascii=False, indent=1)
    print(len(GAMES), 'games', len(entries), 'entries')


main()
