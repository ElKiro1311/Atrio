# Seguridad

## Avisar de un problema

Usa [un aviso privado de seguridad](https://github.com/ElKiro1311/Atrio/security/advisories/new) en lugar de una incidencia pública.

## Riesgos que conviene conocer

**Los complementos corren con tus permisos.** Un complemento de Atrio o de Playnite es código que se ejecuta como tú. Instala solo los que te fíes de su autor. Atrio avisa si tienes alguno de `atrio/complementos/bloqueados.txt`.

**Las sesiones de las tiendas se guardan en la carpeta de Atrio.** Están en `ExtensionsData`. Si compartes o subes tu carpeta, quítala antes.

**El programa no está firmado.** Descárgalo solo de la página de versiones de este repositorio. Las actualizaciones vienen de esa misma página, y Atrio comprueba la huella de cada paquete antes de instalarlo.

**Librerías heredadas.** Atrio mantiene dos librerías antiguas de Playnite porque los complementos y el formato de la biblioteca dependen de ellas:

| Librería | Aviso | Por qué sigue | Alcance |
|---|---|---|---|
| LiteDB 4.1.4 | Deserialización de datos no fiables (crítico, corregido en 5.0.13) | La versión 5 usa otro formato de archivo: rompería las bibliotecas de Playnite y los complementos | Solo abre la biblioteca local. Quien pueda alterarla ya tiene acceso a tu cuenta |
| AngleSharp 0.9.9 | Codificación incorrecta al generar HTML (moderado) | Las versiones nuevas cambian la API que usan los complementos | Se usa para leer páginas, no para publicar HTML |

Newtonsoft.Json sí está actualizada (13.0.3), con redirección para los complementos compilados contra la 10.
