"""Convierte las capturas de cada tema en la vista previa del menú de Temas y en las imágenes del LÉEME.

Uso: hacer_vistas.py <carpeta de capturas>
"""
import os
import sys

RAIZ = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(RAIZ, '.herramientas', 'py'))
from PIL import Image

if len(sys.argv) < 2:
    raise SystemExit(__doc__)
CAPTURAS = sys.argv[1]
VISTAS = os.path.join(RAIZ, 'atrio', 'temas', 'vistas')
LEEME = os.path.join(RAIZ, 'atrio', 'capturas')
os.makedirs(VISTAS, exist_ok=True)
os.makedirs(LEEME, exist_ok=True)

# Cada tema se presenta con la pantalla que mejor enseña su disposición.
TEMAS = {
    'AtrioNoche': 'inicio', 'AtrioAurora': 'inicio', 'AtrioOLED': 'biblioteca',
    'Cobalto': 'biblioteca', 'Oliva': 'biblioteca',
    'Grafito': 'biblioteca', 'GrafitoCompacto': 'biblioteca', 'Default': 'biblioteca',
}

for tema, pantalla in TEMAS.items():
    img = Image.open(os.path.join(CAPTURAS, f'tema-{tema}-{pantalla}.png')).convert('RGB')
    img.thumbnail((760, 760), Image.LANCZOS)
    img.save(os.path.join(VISTAS, f'{tema}.jpg'), quality=88, optimize=True)

for origen, destino in (('tema-AtrioNoche-inicio', 'inicio'), ('tema-AtrioNoche-biblioteca', 'biblioteca')):
    img = Image.open(os.path.join(CAPTURAS, f'{origen}.png')).convert('RGB')
    img.thumbnail((1280, 1280), Image.LANCZOS)
    img.save(os.path.join(LEEME, f'{destino}.jpg'), quality=86, optimize=True)

print('ok', len(TEMAS))
