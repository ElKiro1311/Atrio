"""Genera las carpetas de los temas de Atrio a partir de atrio/temas/base y atrio/temas/variantes.py.

Cada variante hereda todos los archivos de la base. Si existe atrio/temas/<carpeta>/, sus archivos
sustituyen a los de la base (por ejemplo una barra superior en lugar de la lateral).

Uso: temas.py <carpeta Themes/Desktop de la aplicación>
"""
import glob
import importlib.util
import os
import re
import shutil
import sys

RAIZ = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'temas'))
if len(sys.argv) < 2:
    raise SystemExit(__doc__)
SALIDA = sys.argv[1]

spec = importlib.util.spec_from_file_location('variantes', os.path.join(RAIZ, 'variantes.py'))
variantes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(variantes)


def leer(ruta):
    with open(ruta, encoding='utf-8-sig') as f:
        return f.read()


def expandir(texto, origenes, valores):
    def incluir(m):
        for origen in origenes:
            ruta = os.path.join(origen, m.group(1))
            if os.path.exists(ruta):
                return leer(ruta)
        raise SystemExit(f'No existe el fragmento {m.group(1)}')

    texto = re.sub(r'@@incluir:([^@]+)@@', incluir, texto)

    def valor(m):
        if m.group(1) not in valores:
            raise SystemExit(f'Falta el valor {m.group(1)}')
        return str(valores[m.group(1)])

    return re.sub(r'@@([A-Za-z0-9]+)@@', valor, texto)


def componer(v):
    valores = dict(variantes.BASE)
    valores.update(v['valores'])
    carpeta = v['carpeta']
    destino = os.path.join(SALIDA, carpeta)
    if os.path.isdir(destino):
        shutil.rmtree(destino)
    os.makedirs(destino, exist_ok=True)

    origenes = [os.path.join(RAIZ, carpeta)]
    if v.get('hereda'):
        origenes.append(os.path.join(RAIZ, v['hereda']))
    origenes.append(os.path.join(RAIZ, 'base'))
    relativos = set()
    for origen in origenes:
        if not os.path.isdir(origen):
            continue
        for carpeta_actual, _, archivos in os.walk(origen):
            for archivo in archivos:
                relativos.add(os.path.relpath(os.path.join(carpeta_actual, archivo), origen))

    for relativo in sorted(relativos):
        if os.path.basename(relativo).startswith('_'):
            continue
        fuente = next(os.path.join(o, relativo) for o in origenes if os.path.exists(os.path.join(o, relativo)))
        salida = os.path.join(destino, relativo)
        os.makedirs(os.path.dirname(salida), exist_ok=True)
        if relativo.endswith('.xaml'):
            with open(salida, 'w', encoding='utf-8', newline='\n') as f:
                f.write(expandir(leer(fuente), origenes, valores))
        else:
            shutil.copyfile(fuente, salida)

    with open(os.path.join(destino, 'theme.yaml'), 'w', encoding='utf-8') as f:
        f.write(f"Id: {v['id']}\nName: {v['nombre']}\nAuthor: Atrio\nVersion: 1.0\nMode: Desktop\nThemeApiVersion: 2.10.0\n")

    with open(os.path.join(destino, 'atrio-tema.yaml'), 'w', encoding='utf-8') as f:
        f.write(f"Descripcion: {v['descripcion']}\nFamilia: {v['familia']}\nOrden: {v['orden']}\n")
        for clave in ('Vista', 'BarraLateral', 'PanelFiltros', 'AnchoPortada', 'FuenteTitulos', 'FuenteTexto'):
            if clave in v:
                f.write(f'{clave}: {v[clave]}\n')

    for familia in v.get('fuentes', []):
        carpeta_fuentes = os.path.join(destino, 'Fonts')
        os.makedirs(carpeta_fuentes, exist_ok=True)
        archivos = glob.glob(os.path.join(RAIZ, 'fuentes', f'{familia}-*.ttf')) + glob.glob(os.path.join(RAIZ, 'fuentes', f'OFL-{familia}.txt'))
        for fuente in archivos:
            shutil.copy(fuente, carpeta_fuentes)

    vista = os.path.join(RAIZ, 'vistas', f'{carpeta}.jpg')
    if os.path.exists(vista):
        shutil.copyfile(vista, os.path.join(destino, 'preview.jpg'))


for variante in variantes.VARIANTES:
    componer(variante)
    print('tema', variante['carpeta'])

vista_clasica = os.path.join(RAIZ, 'vistas', 'Default.jpg')
if os.path.exists(vista_clasica) and os.path.isdir(os.path.join(SALIDA, 'Default')):
    shutil.copyfile(vista_clasica, os.path.join(SALIDA, 'Default', 'preview.jpg'))
