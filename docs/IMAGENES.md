# Imágenes de la documentación

[Volver al README](../README.md) · [Guía visual](GUIA-DE-USO.md)

Las imágenes se prepararon el 22 de octubre de 2026. Se almacenan en el repositorio para que
el README y la guía puedan verse sin depender de enlaces externos.

| Archivo | Contenido | Procedencia |
| --- | --- | --- |
| `images/vista-general.png` | Dispositivos, mapa de la placa y controles. | Interfaz WPF del proyecto renderizada con dispositivos y colores simulados. |
| `images/teclado-efectos.png` | Mapa del teclado, Arcoiris, alcance y velocidad. | Interfaz WPF renderizada con datos simulados; el patrón es ilustrativo. |
| `images/memoria.png` | Módulo de RAM con selección de un LED. | Interfaz WPF renderizada con datos simulados. |
| `images/calibracion.png` | Ganancias, gamma y acciones del diálogo. | Contenido del diálogo WPF renderizado con valores iniciales de ejemplo para la Z390. |
| `images/sin-conexion.jpg` | Ventana sin dispositivos y error del servidor SDK. | Captura real de la aplicación ejecutada sin servidor SDK accesible. |

Las cuatro vistas de demostración reutilizan el XAML, los controles, el renderizador y los
recursos gráficos de la aplicación compilada. Se prepararon fuera del flujo normal de conexión,
con un inventario simulado en memoria y sin enviar comandos RGB. Llevan una banda que identifica
su carácter ilustrativo. Esa banda no forma parte de la aplicación.

No se han modificado los archivos de la aplicación para crear estas imágenes. El renderizado
de contenido WPF omite el marco nativo del diálogo de calibración y puede diferir de una captura
de escritorio en márgenes o escala. Las fotos de los dispositivos provienen de los recursos
ya presentes en `App/Assets/`.

## Actualizar las capturas

1. Compila `App/App.csproj` en Release e inicia RGB Studio con OpenRGB disponible.
2. Desmarca Aplicar al instante antes de preparar una selección si quieres evitar envíos al pulsar el mapa.
3. Captura únicamente la ventana de la aplicación o el diálogo correspondiente, con textos legibles.
4. Sustituye la imagen de `docs/images/` conservando su nombre y revisa sus pies en el README y la guía.
5. Si pasas de una demostración a hardware real, actualiza esta tabla y registra versión de OpenRGB,
   dispositivos, acciones y observaciones en la validación. Distingue una captura de interfaz de una
   comprobación física de las luces.
6. Comprueba que los enlaces e imágenes se ven correctamente en Markdown. No añadas binarios de
   OpenRGB, logs ni archivos de `bin/` u `obj/` al commit.
