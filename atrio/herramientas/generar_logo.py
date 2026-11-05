"""Dibuja el logo de Atrio: un arco de entrada que forma una A, sobre un cuadrado redondeado."""
import os

import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.herramientas', 'py'))
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FUENTES = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'source'))
S = 1024


def degradado(tam, a, b):
    img = Image.new('RGB', (tam, tam))
    px = img.load()
    for y in range(tam):
        for x in range(tam):
            t = (x + y) / (2 * tam - 2)
            px[x, y] = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return img


def simbolo(color_fondo_transparente=True):
    """Máscara del símbolo: arco exterior, hueco interior y travesaño."""
    m = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(m)
    izq, der, arriba, abajo = 232, 792, 196, 836
    radio = (der - izq) // 2
    d.ellipse((izq, arriba, der, arriba + 2 * radio), fill=255)
    d.rectangle((izq, arriba + radio, der, abajo), fill=255)
    grosor = 112
    i_izq, i_der = izq + grosor, der - grosor
    i_radio = (i_der - i_izq) // 2
    i_arriba = arriba + grosor
    d.ellipse((i_izq, i_arriba, i_der, i_arriba + 2 * i_radio), fill=0)
    d.rectangle((i_izq, i_arriba + i_radio, i_der, abajo), fill=0)
    # Travesaño de la A
    d.rectangle((i_izq - 4, 560, i_der + 4, 560 + 86), fill=255)
    return m


def icono(a=(99, 120, 255), b=(168, 92, 255), blanco=(255, 255, 255)):
    fondo = degradado(S, a, b).convert('RGBA')
    mascara = Image.new('L', (S * 2, S * 2), 0)
    ImageDraw.Draw(mascara).rounded_rectangle((0, 0, S * 2 - 1, S * 2 - 1), radius=460, fill=255)
    mascara = mascara.resize((S, S), Image.LANCZOS)
    fondo.putalpha(mascara)

    sim = simbolo()
    sombra = Image.new('RGBA', (S, S), (10, 8, 40, 0))
    sombra.putalpha(sim.point(lambda v: int(v * 0.35)).filter(ImageFilter.GaussianBlur(18)))
    capa = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    capa.alpha_composite(sombra, (0, 14))
    relleno = Image.new('RGBA', (S, S), blanco + (255,))
    relleno.putalpha(sim)
    capa.alpha_composite(relleno)
    fondo.alpha_composite(capa)
    return fondo


def monocromo(color):
    """Solo el símbolo, para la bandeja del sistema."""
    img = Image.new('RGBA', (S, S), color + (0,))
    img.putalpha(simbolo())
    return img


def guardar_ico(img, ruta):
    img.save(ruta, format='ICO', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


logo = icono()
gris = icono((64, 68, 80), (40, 44, 54), (205, 208, 216))

for tema in ('Playnite.DesktopApp/Themes/Desktop/Default/Images', 'Playnite.FullscreenApp/Themes/Fullscreen/Default/Images'):
    ruta = os.path.join(FUENTES, tema)
    guardar_ico(logo, os.path.join(ruta, 'applogo.ico'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(ruta, 'applogo.png'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(ruta, 'applogo_clean.png'))
    logo.resize((256, 256), Image.LANCZOS).save(os.path.join(ruta, 'applogo_full.png'))
    gris.resize((256, 256), Image.LANCZOS).save(os.path.join(ruta, 'applogo_dark.png'))
    guardar_ico(monocromo((255, 255, 255)), os.path.join(ruta, 'applogo_white.ico'))
    guardar_ico(monocromo((16, 16, 20)), os.path.join(ruta, 'applogo_black.ico'))

# Pantalla de inicio
for proyecto in ('Playnite.DesktopApp', 'Playnite.FullscreenApp'):
    ruta = os.path.join(FUENTES, proyecto, 'SplashScreen.png')
    ancho, alto = Image.open(ruta).size
    lienzo = Image.new('RGBA', (ancho, alto), (15, 17, 21, 255))
    lado = int(min(ancho, alto) * 0.42)
    lienzo.alpha_composite(logo.resize((lado, lado), Image.LANCZOS), ((ancho - lado) // 2, int(alto * 0.20)))
    d = ImageDraw.Draw(lienzo)
    fuente = ImageFont.truetype(os.path.join(FUENTES, '..', 'atrio', 'temas', 'fuentes', 'Poppins-SemiBold.ttf'), int(alto * 0.11))
    caja = d.textbbox((0, 0), 'Atrio', font=fuente)
    d.text(((ancho - (caja[2] - caja[0])) / 2, int(alto * 0.20) + lado + int(alto * 0.05)), 'Atrio', font=fuente, fill=(243, 244, 246))
    lienzo.save(ruta)
    print(ruta, ancho, alto)

