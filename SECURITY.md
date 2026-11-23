# Seguridad

## Avisar de un problema

Usa [un aviso privado de seguridad](https://github.com/ElKiro1311/Atrio/security/advisories/new) en lugar de una incidencia pública.

## Riesgos que conviene conocer

**Los complementos corren con tus permisos.** Un complemento de Atrio o de Playnite es código que se ejecuta como tú. Instala solo los que te fíes de su autor. Atrio avisa si tienes alguno de `atrio/complementos/bloqueados.txt`.

**Las sesiones de las tiendas se guardan en la carpeta de Atrio.** Están en `ExtensionsData`. Si compartes o subes tu carpeta, quítala antes.

**El programa no está firmado.** Descárgalo solo de la página de versiones de este repositorio. Las actualizaciones vienen de esa misma página, y Atrio comprueba la huella de cada paquete antes de instalarlo.

**OptiScaler Client es un programa de otros.** Solo se descarga si lo pides en la ficha de un juego, de su página de GitHub, y Atrio comprueba la huella SHA-256 que da GitHub antes de abrirlo. Un paquete que hayas descargado tú también se comprueba, y si no es uno publicado no se usa; una copia que descomprimas a mano en la carpeta de Atrio se usa tal cual. Corre con tus permisos y cambia archivos de los juegos en los que lo instalas. Atrio lo maneja con un módulo propio por una tubería que solo puede abrir tu usuario. Antes de cambiar un juego guarda lo que va a tocar en la carpeta de datos de OptiScaler Client (subcarpeta `Atrio`) y lo borra al acabar: si algo falla o se corta a medias, el juego vuelve a quedar como estaba. No lo uses en juegos en línea.

**Librerías heredadas.** Atrio mantiene dos librerías antiguas de Playnite porque los complementos y el formato de la biblioteca dependen de ellas:

| Librería | Aviso | Por qué sigue | Alcance |
|---|---|---|---|
| LiteDB 4.1.4 | Deserialización de datos no fiables (crítico, corregido en 5.0.13) | La versión 5 usa otro formato de archivo: rompería las bibliotecas de Playnite y los complementos | Solo abre la biblioteca local. Quien pueda alterarla ya tiene acceso a tu cuenta |
| AngleSharp 0.9.9 | Codificación incorrecta al generar HTML (moderado) | Las versiones nuevas cambian la API que usan los complementos | Se usa para leer páginas, no para publicar HTML |

Newtonsoft.Json sí está actualizada (13.0.3), con redirección para los complementos compilados contra la 10.
