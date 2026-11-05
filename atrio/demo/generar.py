"""Crea una biblioteca inventada para las capturas del proyecto: juegos que no existen y arte generado aquí.

Sirve para enseñar Atrio sin usar portadas de juegos reales ni la biblioteca de nadie.

Uso: generar.py <carpeta de salida>
"""
import hashlib
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.herramientas', 'py'))
from PIL import Image, ImageDraw, ImageFilter, ImageFont

RAIZ = os.path.dirname(os.path.abspath(__file__))
FUENTES = os.path.join(RAIZ, '..', 'temas', 'fuentes')

TIENDAS = {
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

# Nombre, estudio, género, año, tiendas, instalado, horas, hace cuántos días se jugó (0 = nunca)
JUEGOS = [
    ('Ecos de Vidrio', 'Estudio Lucerna', 'Aventura', 2024, ['steam', 'epic', 'gog'], True, 62, 1),
    ('Marea Baja', 'Faro Norte', 'Rol', 2023, ['steam', 'gog'], True, 148, 2),
    ('Cometa Nueve', 'Órbita Baja', 'Estrategia', 2025, ['epic'], True, 31, 3),
    ('Los Relojeros de Arena', 'Tres Lunas', 'Puzle', 2022, ['gog', 'steam'], False, 12, 40),
    ('Jardín Binario', 'Ciprés Digital', 'Simulación', 2024, ['steam'], True, 87, 5),
    ('Salitre', 'Faro Norte', 'Acción', 2021, ['xbox', 'steam'], False, 25, 70),
    ('Nube Férrea', 'Hangar 12', 'Carreras', 2023, ['epic', 'steam', 'ea'], True, 44, 7),
    ('Taller de Tormentas', 'Estudio Lucerna', 'Construcción', 2025, ['steam'], True, 210, 1),
    ('Pulso Austral', 'Órbita Baja', 'Disparos', 2022, ['battlenet'], False, 0, 0),
    ('Sombra de Cobre', 'Tres Lunas', 'Sigilo', 2020, ['gog'], False, 18, 120),
    ('Atlas de Humo', 'Hangar 12', 'Aventura', 2024, ['ubisoft', 'steam'], False, 9, 200),
    ('La Hora Azul', 'Ciprés Digital', 'Narrativa', 2023, ['itch'], True, 6, 12),
    ('Bruma Dorada', 'Faro Norte', 'Rol', 2025, ['steam', 'epic'], False, 0, 0),
    ('Fragua Lunar', 'Estudio Lucerna', 'Acción', 2021, ['amazon'], False, 3, 300),
    ('Cartógrafos del Abismo', 'Órbita Baja', 'Exploración', 2024, ['steam', 'gog', 'epic'], True, 73, 9),
    ('Hilo Rojo', 'Tres Lunas', 'Puzle', 2019, ['epic'], False, 2, 400),
    ('Vértice', 'Hangar 12', 'Carreras', 2022, ['xbox'], False, 15, 90),
    ('Dunas de Neón', 'Ciprés Digital', 'Acción', 2025, ['steam'], True, 39, 14),
    ('El Último Faro', 'Faro Norte', 'Narrativa', 2020, ['gog', 'amazon'], False, 11, 150),
    ('Ceniza y Sal', 'Estudio Lucerna', 'Rol', 2023, ['steam'], False, 0, 0),
    ('Mapa de Estrellas', 'Órbita Baja', 'Estrategia', 2021, ['epic', 'gog'], False, 28, 60),
    ('Cristal Hondo', 'Tres Lunas', 'Aventura', 2024, ['ea'], False, 0, 0),
    ('Motor de Niebla', 'Hangar 12', 'Simulación', 2022, ['steam'], True, 120, 20),
    ('Tinta Viva', 'Ciprés Digital', 'Plataformas', 2023, ['itch', 'steam'], False, 8, 33),
    ('Río Subterráneo', 'Faro Norte', 'Exploración', 2020, ['ubisoft'], False, 0, 0),
    ('Corona de Musgo', 'Estudio Lucerna', 'Rol', 2025, ['gog'], False, 0, 0),
    ('Ruta Polar', 'Órbita Baja', 'Simulación', 2019, ['steam', 'xbox'], False, 54, 180),
    ('Aguja del Norte', 'Tres Lunas', 'Sigilo', 2022, ['amazon', 'epic'], False, 1, 500),
    ('Bosque Cuántico', 'Ciprés Digital', 'Puzle', 2024, ['steam'], True, 17, 25),
    ('Eclipse Tranquilo', 'Hangar 12', 'Narrativa', 2021, ['epic'], False, 5, 250),
]

PALETAS = [
    ('#0F1B3D', '#3B4BA8', '#F2A65A', '#FFE3B3'),
    ('#10231F', '#1F6F5C', '#8FD694', '#F4F1BB'),
    ('#2A0F3F', '#7B2CBF', '#F15BB5', '#FEE440'),
    ('#1B1B2F', '#E43F5A', '#F9A826', '#FFF1C1'),
    ('#07222F', '#0E7C86', '#5EEAD4', '#E0FFFA'),
    ('#2B1A12', '#A4512A', '#E6A157', '#FBE7C6'),
    ('#101820', '#2C5F8A', '#7EC8E3', '#E8F7FF'),
    ('#1F0A1E', '#6C1D45', '#D1495B', '#EDAE49'),
]


def color(texto):
    return tuple(int(texto[i:i + 2], 16) for i in (1, 3, 5))


def mezcla(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def cielo(ancho, alto, a, b, c):
    img = Image.new('RGB', (1, alto))
    for y in range(alto):
        t = y / max(1, alto - 1)
        img.putpixel((0, y), mezcla(a, b, t * 2) if t < .5 else mezcla(b, c, (t - .5) * 2))
    return img.resize((ancho, alto))


def sierra(rnd, ancho, base, amplitud):
    fases = [(rnd.uniform(.5, 1.5), rnd.uniform(0, 6.28), rnd.uniform(.4, 1)) for _ in range(5)]
    puntos = []
    for x in range(0, ancho + 8, 8):
        y = sum(math.sin(x / ancho * 6.28 * f * (i + 1) + p) * w / (i + 1) for i, (f, p, w) in enumerate(fases))
        puntos.append((x, base + y * amplitud))
    return puntos


def montanas(d, rnd, ancho, alto, tonos):
    d.ellipse((ancho * .58, alto * .14, ancho * .58 + alto * .2, alto * .34), fill=tonos[3])
    for capa in range(4):
        base = alto * (.5 + capa * .12)
        puntos = sierra(rnd, ancho, base, alto * (.05 + .02 * (3 - capa)))
        d.polygon(puntos + [(ancho, alto), (0, alto)], fill=mezcla(tonos[1], tonos[0], capa / 3))


def ondas(d, rnd, ancho, alto, tonos):
    for i in range(16):
        base = alto * (.25 + i * .05)
        puntos = sierra(rnd, ancho, base, alto * .03)
        d.line(puntos, fill=mezcla(tonos[3], tonos[1], i / 15), width=max(2, alto // 240))


def cristales(d, rnd, ancho, alto, tonos):
    for _ in range(26):
        x, y = rnd.uniform(0, ancho), rnd.uniform(alto * .1, alto)
        r = rnd.uniform(alto * .05, alto * .22)
        lados = rnd.choice((3, 4, 5, 6))
        giro = rnd.uniform(0, 6.28)
        puntos = [(x + math.cos(giro + i * 6.28 / lados) * r, y + math.sin(giro + i * 6.28 / lados) * r) for i in range(lados)]
        d.polygon(puntos, fill=mezcla(tonos[rnd.randrange(4)], tonos[0], rnd.uniform(.2, .7)))


def ciudad(d, rnd, ancho, alto, tonos):
    d.ellipse((ancho * .2, alto * .12, ancho * .2 + alto * .3, alto * .42), fill=tonos[2])
    for capa in range(3):
        x = -10
        while x < ancho:
            w = rnd.uniform(ancho * .05, ancho * .13)
            h = rnd.uniform(alto * .15, alto * .5) * (1 - capa * .2)
            tono = mezcla(tonos[1], tonos[0], .3 + capa * .33)
            d.rectangle((x, alto - h, x + w, alto), fill=tono)
            for vy in range(int(alto - h) + 12, alto, max(14, alto // 60)):
                for vx in range(int(x) + 8, int(x + w) - 6, max(14, ancho // 50)):
                    if rnd.random() < .22:
                        d.rectangle((vx, vy, vx + 4, vy + 6), fill=tonos[3])
            x += w + rnd.uniform(2, 14)


ESCENAS = [montanas, ondas, cristales, ciudad]


def escena(nombre, ancho, alto):
    semilla = int(hashlib.sha1(nombre.encode('utf-8')).hexdigest(), 16)
    rnd = random.Random(semilla)
    tonos = [color(c) for c in PALETAS[semilla % len(PALETAS)]]
    img = cielo(ancho, alto, tonos[0], tonos[1], tonos[2])
    ESCENAS[(semilla // 7) % len(ESCENAS)](ImageDraw.Draw(img), rnd, ancho, alto, tonos)
    return img, tonos


def portada(nombre):
    img, tonos = escena(nombre, 600, 800)
    velo = Image.new('L', (600, 800), 0)
    d = ImageDraw.Draw(velo)
    for y in range(440, 800):
        d.line((0, y, 600, y), fill=int((y - 440) / 360 * 225))
    img = Image.composite(Image.new('RGB', img.size, tonos[0]), img, velo)
    d = ImageDraw.Draw(img)
    familia = 'Poppins-Bold.ttf' if len(nombre) % 2 else 'Barlow-Bold.ttf'
    palabras, lineas, actual = nombre.upper().split(), [], ''
    fuente = ImageFont.truetype(os.path.join(FUENTES, familia), 56)
    for palabra in palabras:
        prueba = (actual + ' ' + palabra).strip()
        if d.textlength(prueba, font=fuente) > 500 and actual:
            lineas.append(actual)
            actual = palabra
        else:
            actual = prueba
    lineas.append(actual)
    y = 752 - len(lineas) * 70
    for linea in lineas:
        d.text((40, y), linea, font=fuente, fill=tonos[3])
        y += 70
    return img


def fondo(nombre):
    img, _ = escena(nombre, 1920, 1080)
    return img.filter(ImageFilter.GaussianBlur(3))


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    salida = sys.argv[1]
    os.makedirs(salida, exist_ok=True)
    entradas = []
    for indice, (nombre, estudio, genero, anio, tiendas, instalado, horas, dias) in enumerate(JUEGOS):
        cover, back = f'c{indice:02}.jpg', f'b{indice:02}.jpg'
        portada(nombre).save(os.path.join(salida, cover), quality=88, optimize=True)
        fondo(nombre).save(os.path.join(salida, back), quality=82, optimize=True)
        for orden, tienda in enumerate(tiendas):
            entradas.append({
                'Name': nombre, 'GameId': f'demo-{indice}-{tienda}', 'Store': TIENDAS[tienda],
                'Cover': cover, 'Background': back, 'Developer': estudio, 'Genre': genero,
                'Description': f'<p>{nombre} es un juego inventado para enseñar Atrio. No existe.</p>',
                # Solo la primera tienda tiene el juego instalado, como pasa en una biblioteca real.
                'Installed': instalado and orden == 0, 'Playtime': horas * 3600 if orden == 0 else 0,
                'Year': anio, 'DaysAgo': dias if orden == 0 else 0,
            })
    with open(os.path.join(salida, 'demo.json'), 'w', encoding='utf-8') as f:
        json.dump(entradas, f, ensure_ascii=False, indent=1)
    print(len(JUEGOS), 'juegos', len(entradas), 'entradas')


main()
