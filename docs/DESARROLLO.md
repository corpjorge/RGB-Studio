# Arquitectura y desarrollo

[Volver al README](../README.md) · [Guía visual](GUIA-DE-USO.md)

## Flujo de funcionamiento

```mermaid
flowchart LR
    UI[Ventana WPF: selección y color] --> RGB[RgbService]
    UI --> FX[EffectEngine: efectos por zona]
    FX --> RGB
    APP[AppWatcher: color del icono activo] --> UI
    AUDIO[Audio de Windows] --> FX
    KEYS[Pulsaciones del teclado] --> FX
    RGB --> CAL[Calibración de la salida]
    CAL --> SDK[OpenRGB SDK: 127.0.0.1:6742]
    SDK --> HW[Placa, teclado, RAM y otros dispositivos]
```

RGB Studio no usa el SDK propietario de GIGABYTE. El servicio de OpenRGB realiza la comunicación
con el hardware. La ventana mantiene la selección y el color; el motor calcula los efectos
animados aproximadamente cada 66 ms. La calibración se aplica a la salida, sin alterar el
estado lógico mostrado en el mapa.

## Organización del repositorio

| Ruta | Responsabilidad |
| --- | --- |
| `App/MainWindow.xaml` y `.xaml.cs` | Ventana, selección, alcance de efectos, comandos y coordinación. |
| `App/Controls/ColorPicker*` | Rueda HSV, paleta y edición RGB/hexadecimal. |
| `App/CalibrationWindow*` | Diálogo y validación de ganancias y gamma. |
| `App/Rgb/RgbService.cs` | Cliente OpenRGB, estado de los LED, modos y tamaño de zonas. |
| `App/Rgb/ZoneCatalog.cs` | Nombres y posiciones de zonas conocidas de la Z390. |
| `App/Rgb/ColorCalibration.cs` | Curvas por canal y persistencia de perfiles. |
| `App/Rgb/StripSizes.cs` | Longitudes de las tiras y restauración al conectar. |
| `App/Effects/SoftwareEffect.cs` | Catálogo y muestreo de los 23 efectos animados. |
| `App/Effects/EffectEngine.cs` | Asignaciones por dispositivo/zona y envío de cuadros. |
| `App/Effects/AudioLevelMonitor.cs` | Captura del audio de salida mediante NAudio. |
| `App/Effects/KeyboardPulseTracker.cs` | Pulsaciones para el efecto reactivo del teclado. |
| `App/Rendering/DeviceRenderer.cs` | Fotografías, máscaras, teclas y zonas pulsables. |
| `App/Assets/` | Icono y fotografías utilizadas por la interfaz. |
| `App/AppWatcher.cs` | Color representativo del icono de la app en primer plano. |
| `App/TrayIcon.cs` | Bandeja, restauración y salida. |
| `App/MainWindow.Updates.cs` | Búsqueda periódica, controles de actualización y cancelación. |
| `App/Updates/` | Publicaciones oficiales, descarga, validación y manifiesto de OpenRGB. |
| `App/MainWindow.AppUpdates.cs` | Interfaz, notificaciones y aplicación de actualizaciones de RGB Studio. |
| `installer/` | Instalador Inno Setup, helper y requisitos para usuarios finales. |
| `scripts/` | Construcción, validación y publicación de releases. |
| `.github/workflows/release.yml` | Publicación automática de instaladores para versiones estables. |
| `App/App.xaml.cs` | Instancia única y ciclo de vida. |
| `test-connection/` | Utilidad de consola para inspección y pruebas manuales. |
| `tests/OpenRgbUpdateChecks/` | Verificaciones del actualizador y del selector del lanzador, sin hardware. |
| `tests/RgbStudioUpdateChecks/` | Validación del actualizador de la aplicación mediante HTTP simulado. |
| `Iniciar.ps1` y `RGB Studio.cmd` | Arranque de OpenRGB y la aplicación compilada. |
| `docs/` | Guía de uso, arquitectura e imágenes. |

## Compilar y ejecutar

Requiere Windows y .NET 10 SDK. Desde la raíz:

```powershell
dotnet restore App/App.csproj
dotnet build App/App.csproj -c Release
dotnet build test-connection/test-connection.csproj -c Release
dotnet run --project App/App.csproj
```

La aplicación WPF compila para `net10.0-windows`, con destino x64, y usa OpenRGB.NET 3.1.1 y
NAudio 2.2.1. La utilidad usa `net10.0` y OpenRGB.NET 3.1.1. Los paquetes se declaran en cada `.csproj`.
Las salidas de compilación quedan en `bin/` y `obj/`, excluidos de Git.

## Restricciones de la comunicación RGB

- **Direct antes de pintar.** El firmware puede ignorar las actualizaciones de LED en otro modo.
- **Un envío completo por dispositivo.** Las actualizaciones sucesivas por zona resultaron
  poco fiables con el IT8297; se conserva el estado de todos los LED y se envía un cuadro completo.
- **El modo determina cómo recibe color.** `ModeSpecific` lleva los colores en el paquete de modo;
  `PerLed` necesita una actualización de LED; `None` usa su propia paleta.
- **Acceso serializado.** `RgbService` protege cliente y estado con un cerrojo. El motor y las
  operaciones de aplicación de color trabajan fuera del hilo de la ventana.
- **Calibración al enviar.** Se corrige una copia para evitar acumular la curva cuadro a cuadro.
  Los perfiles de zona sustituyen al general en las zonas correspondientes.
- **Asignaciones por zona.** Un efecto animado se guarda por `(dispositivo, zona)`; `-1` representa
  el dispositivo entero. Color sólido también permite pintar un LED individual.
- **Color y velocidad compartidos al actualizar.** `UpdateColor` y `UpdateSpeed` cambian todas las
  asignaciones activas del motor.

## Datos locales

| Archivo | Contenido y clave |
| --- | --- |
| `%AppData%\RgbStudio\calibracion.json` | Ganancia y gamma por nombre de dispositivo; para zonas, `nombre::zona:índice`. |
| `%AppData%\RgbStudio\tiras.json` | Cantidad de LED por `nombre|índice de zona`. |

Los nombres duplicados de OpenRGB comparten claves. El alias visual D80/D41 no modifica el nombre
de hardware usado al guardar. Estas preferencias no son un sistema de escenas: no se guardan
en ellas las selecciones de color ni las asignaciones de efectos.

## Catálogo de la placa y compatibilidad

El catálogo actual identifica nombres que contienen `Z390` y mapea las zonas 1–5 a Logo AORUS,
Contorno RAM, Aguila AORUS, Linea inferior y Ventiladores y disipador. Este mapa corresponde al
equipo Z390 AORUS PRO WIFI usado durante el desarrollo; no debe suponerse válido para cualquier Z390.
Las cabeceras redimensionables sin entrada en el catálogo se dibujan aparte.

La compilación personalizada de OpenRGB usada por el lanzador está fuera del repositorio. La
documentación previa registra que normaliza el sufijo DMI `-CF` para encontrar el diseño correcto
de la placa. Un OpenRGB distinto puede exponer zonas diferentes.

Para incorporar una placa nueva, inspecciona primero lo que expone OpenRGB. Conserva el dibujo
genérico para equipos sin catálogo y verifica las correspondencias físicas antes de añadir nombres.

## Inspección y pruebas manuales

Cada cambio debe compilar ambos proyectos. El actualizador tiene verificaciones automatizadas
sin hardware y sin dependencias externas de pruebas:

```powershell
dotnet run --project tests/OpenRgbUpdateChecks -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tests/OpenRgbUpdateChecks/Check-Launcher.ps1
```

Se prueban selección de estable x64, rechazo de RC, límites de descarga, extracción de ZIP,
cancelación, bloqueo entre procesos, persistencia de preferencias, conservación de la instalación
previa y selección del ejecutable. Los ejecutables de las pruebas son fixtures que no se ejecutan.
Los archivos se crean únicamente bajo `bin/`, excluido de Git.

La prueba opcional con Internet descarga y valida el paquete oficial en una carpeta de pruebas;
no ejecuta OpenRGB ni cambia la instalación del usuario:

```powershell
dotnet run --project tests/OpenRgbUpdateChecks -c Release -- --live
```

```powershell
dotnet run --project test-connection -- list
```

`list` lee el inventario, zonas y modos. Los siguientes comandos **cambian la iluminación** y
requieren hardware conectado; la implementación actual busca una placa cuyo nombre contenga `Z390`:

```powershell
dotnet run --project test-connection -- map
dotnet run --project test-connection -- walk
```

`map` asigna colores distintos a las zonas con LED y espera Enter para apagarlas. `walk` las
enciende una a una y espera una observación antes de avanzar; al terminar apaga la placa.
Se recomienda evitar que otra interfaz o utilidad vuelva a imponer colores mientras se prueba.

Para una validación de RGB, registra:

| Dato | Qué anotar |
| --- | --- |
| Entorno | Fecha, Windows y versión o compilación exacta de OpenRGB. |
| Inventario | Nombre publicado, número de LED, zonas y modo del dispositivo. |
| Acción | Comando exacto o selección/efecto/alcance usado en la interfaz. |
| Resultado | Qué se observó físicamente y si coincide con lo esperado. |
| Evidencia | Captura de la interfaz y, si corresponde, fotografía del hardware. |

Para cambios de interfaz, revisa además la selección, el desplazamiento del panel derecho y
que APLICAR y Apagar sigan accesibles. Las capturas con datos simulados sirven para documentar
los controles, pero no demuestran detección ni respuesta física de los dispositivos.

## Validación de esta documentación

El 22 de octubre de 2026 se compilaron ambos proyectos en Release sin errores. NuGet emitió
advertencias `NU1900` al no poder consultar la información de vulnerabilidades en `api.nuget.org`.
La aplicación se abrió y se capturó el estado sin conexión. El comando
`dotnet run --project test-connection/test-connection.csproj -c Release --no-build -- list`
informó que no pudo conectarse a OpenRGB en `127.0.0.1:6742`. No se validaron efectos en hardware
ni una versión de servidor; las vistas de dispositivos usan datos de demostración.

## Actualizador de OpenRGB

La fuente es la [API pública de publicaciones del repositorio oficial en Codeberg](https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases?limit=50),
alojamiento enlazado desde la [página oficial de descargas](https://openrgb.org/releases.html).
Se filtra tanto `draft`/`prerelease` como el tag `release_N.N[.N]`, porque algunos RC no llevan
la marca `prerelease`. Se elige la versión estable numéricamente mayor con un ZIP Windows x64.

La comunicación usa HTTPS, acepta redirecciones únicamente dentro de Codeberg y limita metadatos,
tamaño de descarga, número de archivos y tamaño extraído. Se valida el tamaño declarado por la API,
las rutas y entradas del ZIP y la cabecera PE x64 de `OpenRGB.exe`. Los hashes SHA-256 calculados
sirven para comprobar la instalación local; no son firmas del editor ni sumas publicadas por este.

Cada descarga se extrae en una carpeta nueva bajo `%LocalAppData%\RgbStudio\OpenRGB\versions\`.
El manifiesto se reemplaza mediante un archivo temporal después de validar el paquete completo.
No se sobrescribe la carpeta personalizada ni la de un servidor activo. Si falla una actualización,
se conserva la referencia anterior. Un bloqueo de archivo evita instalaciones simultáneas entre
procesos; un semáforo evita comprobaciones simultáneas en la misma instancia.

El lanzador valida el manifiesto y el hash del ejecutable, prioriza `Current`, después `Previous`,
y por último las carpetas originales. `-UsarOpenRgbAnterior` omite las instalaciones administradas.
El servidor activo se reutiliza: la versión preparada entra en uso al próximo arranque de OpenRGB.
Este mecanismo no instala controladores ni valida automáticamente la respuesta física del hardware.

Se verificó la descarga y preparación de OpenRGB 1.1 (ZIP oficial de Windows x64) sin ejecutarlo,
junto con 36 comprobaciones locales del actualizador y 9 del lanzador. No se realizaron pruebas físicas con esta versión.

## Instalador y actualizaciones de RGB Studio

Consulta [el procedimiento completo de releases](RELEASES.md). `RgbStudioUpdater` consulta la
API pública de GitHub, valida el repositorio, tag, asset, tamaño y `digest`, y prepara un instalador
verificado. No se incluye ninguna credencial en el programa. El helper instalado espera el PID
de la aplicación antes de actualizar sus archivos y se ejecuta con los mismos permisos del usuario.

`OpenRgbHost` cubre el primer arranque de una instalación limpia: reutiliza un servidor activo o
inicia la versión administrada después de descargarla. En desarrollo conserva la búsqueda de las
carpetas hermanas. La descarga automática de OpenRGB y la de RGB Studio tienen controles separados.
