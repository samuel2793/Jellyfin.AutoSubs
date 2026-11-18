# AutoSubs para Jellyfin 12.2 — prototipo 0.1.0

Un plugin **nuevo**, escrito en C#/.NET 10, que busca subtítulos con cualquier nombre en carpetas de películas y subcarpetas, y crea enlaces simbólicos con nombres compatibles con Jellyfin junto a los vídeos. No cambia los nombres originales, ni mueve, copia o borra películas ni subtítulos. Los enlaces son *relativos*, por lo que son válidos dentro del mismo bind mount del contenedor.

> **Estado:** código fuente preparado para compilar y probar; **no se ha compilado ni ejecutado contra Jellyfin 12.2** en este entorno (no dispone de .NET SDK ni acceso a NuGet). **No es todavía una versión de producción**. Probar primero en una biblioteca pequeña o de pruebas y disponer de copia de seguridad.

## Comportamiento

- Solo trabaja con las bibliotecas de tipo `Movies` que Jellyfin tenga configuradas, sin introducir rutas manualmente.
- Se dispara al final de los escaneos de la biblioteca (`ILibraryPostScanTask`).
- Examina subcarpetas hasta `MaxDepth` (12 por defecto), con cualquier nombre, incluyendo `Subs`, `Subtitles`, etc.
- Reconoce `.srt`, `.ass`, `.ssa`, `.vtt`, `.idx/.sub`, `.sup`, `.smi`, `.sami`, `.ttml`.
- Une **parejas VobSub** `.idx` + `.sub` con el mismo nombre base y las enlaza juntas; un `.idx` sin `.sub` se ignora.
- Con un único vídeo en una carpeta, asocia los subtítulos hallados en esa carpeta y debajo de ella. **Ignora** subcarpetas que contengan otro vídeo para no mezclar películas.
- Si hay **varios vídeos en la misma carpeta**, no adivina: ignora los subtítulos de esa carpeta y registra el motivo.
- No sigue enlaces simbólicos preexistentes durante la búsqueda. No pisa archivos; no elimina enlaces antiguos.
- Evita duplicar enlaces creados en ejecuciones anteriores.
- Arranca en **Dry run** por seguridad (solo simular y registrar); para crear enlaces hay que desactivarlo una vez en la configuración global.
- Intenta reconocer idioma por el nombre (`spanish`, `es`, `eng`, etc.); si no puede, usa `und` (indeterminado).

Ejemplo, antes:

```text
La.La.Land/
├── La.La.Land.mkv
└── Subs/
    ├── pistas.idx
    └── pistas.sub
```

Después:

```text
La.La.Land/
├── La.La.Land.mkv
├── La.La.Land.und.autosubs01.idx -> Subs/pistas.idx
├── La.La.Land.und.autosubs01.sub -> Subs/pistas.sub
└── Subs/
    ├── pistas.idx
    └── pistas.sub
```

Nota: Jellyfin puede necesitar actualizar los metadatos/reescanear para reconocer los nuevos enlaces. Los subtítulos VobSub son de imagen y pueden requerir transcodificación. Algunas combinaciones de pistas VobSub siguen teniendo problemas propios de Jellyfin 12.2.

## Compilación (desde Linux o Synology)

Requisitos: .NET SDK 10, acceso a NuGet, o Docker para usar la imagen del SDK.

Desde la carpeta que contiene este README:

```bash
# Opción A, con dotnet instalado:
dotnet run --project AutoSubs.SelfTest/AutoSubs.SelfTest.csproj -c Release
dotnet publish Jellyfin.Plugin.AutoSubs/Jellyfin.Plugin.AutoSubs.csproj -c Release -o publish

# Opción B, con Docker (dos órdenes):
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project AutoSubs.SelfTest/AutoSubs.SelfTest.csproj -c Release
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish Jellyfin.Plugin.AutoSubs/Jellyfin.Plugin.AutoSubs.csproj -c Release -o publish
```

El artefacto principal es `publish/Jellyfin.Plugin.AutoSubs.dll`. **No copies al plugin las DLL de Jellyfin incluidas en NuGet** (las aporta el servidor). Los paquetes usan `ExcludeAssets="runtime"`.

## Instalación manual en el contenedor de Jellyfin

1. **Desinstala Subtitle Sorter** (el anterior, incompatible) y reinicia Jellyfin.
2. Identifica dónde está montado `/config` en tu Synology con Container Manager, o desde el NAS:

   ```bash
   docker inspect NOMBRE_DEL_CONTENEDOR --format '{{range .Mounts}}{{println .Source "->" .Destination}}{{end}}'
   ```

3. Detén el contenedor y crea **dentro de su volumen de configuración** la carpeta `/config/plugins/AutoSubs_0.1.0.0/`.
4. Coloca dentro `publish/Jellyfin.Plugin.AutoSubs.dll` y el archivo `meta.json` incluido en este proyecto. No sobrescribas otros plugins.
5. Inicia Jellyfin. Comprueba que **AutoSubs** aparece como activo.
6. En **Complementos → AutoSubs → Configuración**, **Dry run ya viene activado por defecto**. Ejecuta un escaneo y busca `AutoSubs:` en los registros.
7. Cuando el registro sea correcto, desactiva Dry run y ejecuta un escaneo. Comprueba con `find . -maxdepth 1 -type l -ls` en la carpeta de la película.

### Permisos

La biblioteca debe estar montada en el contenedor con **lectura y escritura**. Si el contenedor tiene el volumen de medios en modo solo lectura (`:ro`), no podrá crear los enlaces. Los permisos de escritura deben corresponder al usuario efectivo del proceso Jellyfin.

### Desinstalación

Desinstala/desactiva el plugin y reinicia Jellyfin. Los originales permanecen intactos. Por seguridad, el plugin **no elimina automáticamente los enlaces** cuando lo desinstalas. Si quieres quitarlos, revisa primero los enlaces con:

```bash
find /volume2/Media/Peliculas -type l -name '*.autosubs*' -print
```

No borres enlaces automáticamente sin revisar qué archivos vas a afectar. Los enlaces llevan `.autosubsNN` en su nombre para identificarlos.

## Limitaciones conocidas

- Está diseñado para **una película por carpeta**. No asigna subtítulos ambiguos cuando hay dos o más vídeos en un mismo directorio.
- No detecta el idioma leyendo el contenido del subtítulo ni el índice VobSub; solo usa pistas en el nombre.
- Solo crea enlaces en un sistema de archivos que soporte symlinks y conceda permisos de escritura.
- Puede necesitar un segundo escaneo/refresh para que Jellyfin indexe los enlaces creados *después* del primer escaneo.
- Soporte para bibliotecas de series, subtítulos dentro de archivos comprimidos y limpieza de enlaces obsoletos quedan fuera de esta versión.
- **Compilación y pruebas de integración pendientes**, porque este entorno no tiene SDK de .NET ni acceso a NuGet. No presentar este prototipo como versión ya probada en el NAS.

## Archivos

- `Jellyfin.Plugin.AutoSubs/Plugin.cs`: identidad del plugin y pantalla de configuración.
- `Jellyfin.Plugin.AutoSubs/AutoSubsPostScanTask.cs`: integración con escaneos de Jellyfin 12.2.
- `Jellyfin.Plugin.AutoSubs/Core/SubtitleOrganizer.cs`: lógica de detección y enlaces.
- `AutoSubs.SelfTest/`: pruebas automáticas de la lógica de archivos con carpetas temporales (requiere .NET 10).
- `meta.json`: metadatos para la instalación manual.

Desarrollado específicamente para Jellyfin 12.2.0. No reutiliza el código de Subtitle Sorter.
