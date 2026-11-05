"""Prepara los complementos que vienen con Atrio.

  complementos.py instalar <carpeta>   Baja los paquetes fijados en lista.json, comprueba su huella,
                                       los descomprime en <carpeta>/Extensions y escribe sus licencias.
  complementos.py actualizar           Busca la última versión compatible de cada uno en la base
                                       pública de add-ons y actualiza lista.json. Necesita PyYAML.

Solo se incluyen complementos con una licencia que permite redistribuirlos. Los demás se descargan
desde la página Tiendas de Atrio, cuando el usuario lo pide.
"""
import hashlib
import io
import json
import os
import re
import shutil
import sys
import urllib.request
import zipfile

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.normpath(os.path.join(AQUI, '..', '..'))
LISTA = os.path.join(RAIZ, 'atrio', 'complementos', 'lista.json')
LICENCIAS = os.path.join(RAIZ, 'atrio', 'complementos', 'licencias')
CACHE = os.path.join(RAIZ, '.herramientas', 'complementos')
BASE_ADDONS = 'https://codeload.github.com/JosefNemec/PlayniteAddonDatabase/zip/refs/heads/master'
SDK = (6, 18, 0)


def bajar(url):
    peticion = urllib.request.Request(url, headers={'User-Agent': 'Atrio-build'})
    with urllib.request.urlopen(peticion, timeout=120) as respuesta:
        return respuesta.read()


def leer_lista():
    with open(LISTA, encoding='utf-8') as f:
        return json.load(f)


def paquete(item):
    ruta = os.path.join(CACHE, f"{re.sub(r'[^A-Za-z0-9_.-]', '_', item['id'])}-{item['version']}.pext")
    if os.path.exists(ruta):
        with open(ruta, 'rb') as f:
            datos = f.read()
    else:
        datos = bajar(item['paquete'])
    huella = hashlib.sha256(datos).hexdigest()
    if huella != item['sha256']:
        raise SystemExit(f"La huella de {item['nombre']} no coincide: {huella}")
    os.makedirs(CACHE, exist_ok=True)
    with open(ruta, 'wb') as f:
        f.write(datos)
    return datos


def instalar(destino):
    extensiones = os.path.join(destino, 'Extensions')
    os.makedirs(extensiones, exist_ok=True)
    lista = leer_lista()
    for item in lista:
        carpeta = os.path.join(extensiones, re.sub(r'[<>:"/\\|?*]', '_', item['id']))
        if os.path.isdir(carpeta):
            shutil.rmtree(carpeta)
        with zipfile.ZipFile(io.BytesIO(paquete(item))) as z:
            z.extractall(carpeta)
        for actual, _, archivos in os.walk(carpeta):
            for archivo in archivos:
                if archivo.lower().endswith('.pdb'):
                    os.remove(os.path.join(actual, archivo))
        print('complemento', item['nombre'], item['version'])

    textos = {}
    with open(os.path.join(destino, 'licencias-complementos.txt'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('Complementos incluidos con Atrio. Son obra de sus autores y conservan sus licencias.\n\n')
        for item in lista:
            f.write(f"{item['nombre']} {item['version']}\n  Autor: {item['autor']}\n  Licencia: {item['licencia']}\n"
                    f"  Código: {item['codigo']}\n  Texto: {item['archivo_licencia']}\n\n")
            textos.setdefault(item['archivo_licencia'], None)
        for nombre in sorted(textos):
            with open(os.path.join(LICENCIAS, nombre), encoding='utf-8') as origen:
                f.write(f"{'-' * 80}\n{nombre}\n\n{origen.read().strip()}\n\n")


def version(texto):
    return tuple(int(x) for x in re.findall(r'\d+', str(texto))[:3])


def actualizar():
    sys.path.insert(0, os.path.join(RAIZ, '.herramientas', 'py'))
    import yaml

    base = {}
    with zipfile.ZipFile(io.BytesIO(bajar(BASE_ADDONS))) as z:
        for nombre in z.namelist():
            if '/addons/' in nombre and nombre.endswith('.yaml'):
                datos = yaml.safe_load(z.read(nombre).decode('utf-8-sig'))
                if isinstance(datos, dict) and datos.get('AddonId'):
                    base[datos['AddonId']] = datos

    lista = leer_lista()
    for item in lista:
        datos = base.get(item['id_base'])
        if not datos:
            print('NO ESTÁ EN LA BASE:', item['nombre'])
            continue
        manifiesto = bajar(datos['InstallerManifestUrl']).decode('utf-8-sig').replace('\t', '  ')
        paquetes = [p for p in yaml.safe_load(manifiesto).get('Packages', []) if version(p.get('RequiredApiVersion', '0')) <= SDK]
        paquetes.sort(key=lambda p: version(p.get('Version', '0')), reverse=True)
        if not paquetes:
            print('SIN VERSIÓN COMPATIBLE:', item['nombre'])
            continue
        elegido = paquetes[0]
        if str(elegido['Version']) == item.get('version') and item.get('sha256'):
            continue
        contenido = bajar(elegido['PackageUrl'])
        item.update(version=str(elegido['Version']), paquete=elegido['PackageUrl'], sha256=hashlib.sha256(contenido).hexdigest())
        print('actualizado', item['nombre'], item['version'])

    with open(LISTA, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(lista, f, ensure_ascii=False, indent=2)
        f.write('\n')


if __name__ == '__main__':
    orden = sys.argv[1] if len(sys.argv) > 1 else ''
    if orden == 'instalar' and len(sys.argv) > 2:
        instalar(sys.argv[2])
    elif orden == 'actualizar':
        actualizar()
    else:
        raise SystemExit(__doc__)
