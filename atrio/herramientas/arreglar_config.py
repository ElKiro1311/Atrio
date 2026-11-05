"""Quita la redirección errónea de RuntimeInformation, igual que FixAppConfig en build.ps1.

Se hace sobre el texto: reescribirlo con un parser XML mueve los espacios de nombres
y Windows deja de aceptar el archivo ("side-by-side configuration is incorrect").
"""
import re
import sys

BLOQUE = re.compile(r'\s*<assemblyBinding\b[^>]*>(?:(?!</assemblyBinding>).)*?'
                    r'System\.Runtime\.InteropServices\.RuntimeInformation.*?</assemblyBinding>', re.S)

for ruta in sys.argv[1:]:
    with open(ruta, encoding='utf-8-sig', newline='') as f:
        texto = f.read()
    nuevo, cuantos = BLOQUE.subn('', texto)
    if cuantos:
        with open(ruta, 'w', encoding='utf-8', newline='') as f:
            f.write(nuevo)
        print('arreglado', ruta, cuantos)
