# O2 Cloud Drive

Aplicacion Windows en C#/.NET 8 que monta O2 Cloud como una unidad virtual con WinFsp. El modo normal inicia sesion en O2 Cloud, lista carpetas y archivos reales y permite abrir archivos mediante descarga bajo demanda. El backend simulado queda disponible con `--skip-auth` o `--simulated`.

## Objetivo de esta fase

- Montar una unidad virtual `O:`.
- Integrarse con el Explorador de Windows mediante WinFsp.
- Abrir una aplicacion Windows con login, selector de letra, nombre de unidad, montaje, desmontaje y logout.
- Mantenerse activa en la bandeja del sistema al minimizar.
- Abrir login oficial visible de O2 Cloud antes del montaje.
- Guardar la sesion en Windows Credential Manager.
- Mostrar carpetas y archivos reales de O2 Cloud.
- Permitir abrir archivos reales mediante descargas con `Range`.
- Crear carpetas, subir archivos nuevos, renombrar/mover y enviar elementos a la papelera en O2 Cloud.
- Leer la cuota real con `get-storage-space` para que Windows no muestre el volumen fijo de 10 GB.
- Mantener el backend simulado para pruebas locales sin tocar la cuenta.
- Mantener una arquitectura modular: `O2CloudApiClient`, `AuthService`, `VirtualFileSystem`, `LocalCacheService`, `UploadDownloadService`, `ConfigService`.

## Requisitos

- Windows 10 o Windows 11 x64.
- .NET 8 SDK.
- WinFsp instalado.
- Microsoft Edge WebView2 Runtime instalado.

Para usuario final se recomienda usar el instalador:

```text
dist\O2CloudDrive-0.7-beta-Setup.exe
```

Ese instalador no necesita que el equipo tenga .NET 8 instalado. Incluye la app autocontenida, WinFsp y Microsoft Edge WebView2 Runtime x64. Al ejecutarlo pedira permisos de administrador porque WinFsp instala un driver de sistema. Si WinFsp o WebView2 ya existen en el equipo, el instalador los omite.

## Actualizaciones

La aplicacion puede comprobar automaticamente las releases publicadas en GitHub al iniciar. Si hay una version nueva, muestra una notificacion y una ventana con enlace de descarga del instalador.

Tambien se puede comprobar manualmente desde el icono de la bandeja:

```text
Buscar actualizaciones
```

## Instalar WinFsp

1. Descarga WinFsp desde <https://winfsp.dev/rel/>.
2. Instala el MSI.
3. Reinicia Windows si el instalador lo solicita.
4. Verifica que exista `C:\Program Files (x86)\WinFsp\bin`.

El proyecto usa el paquete NuGet `winfsp.net`, pero el driver de WinFsp debe estar instalado en el sistema para montar unidades.

## Compilar

```powershell
dotnet restore
dotnet build -c Release
```

Si la maquina solo tiene un SDK anterior, instala .NET 8 o usa el SDK local:

```powershell
.\.dotnet\dotnet.exe build -c Release
```

## Crear instalador completo

El instalador completo se genera con:

```powershell
.\installer\build-installer.ps1
```

Salida esperada:

```text
dist\O2CloudDrive-0.7-beta-Setup.exe
dist\O2CloudDrive-0.7-beta-Setup.sha256.txt
```

El script descarga los prerrequisitos oficiales si no estan ya en `installer\prereqs`, publica la app como autocontenida, empaqueta el payload y publica un setup unico para Windows x64.

## Firma digital

El script de instalador tiene soporte opcional para firmar los ejecutables con `signtool.exe`. Si no se indica certificado, la firma se omite y el instalador se genera igual.

Firmar con un certificado instalado en el almacen de Windows:

```powershell
.\installer\build-installer.ps1 -CertificateThumbprint "THUMBPRINT_DEL_CERTIFICADO"
```

Firmar con un certificado `.pfx`:

```powershell
$env:O2CLOUDDRIVE_PFX_PASSWORD = "password-del-pfx"
.\installer\build-installer.ps1 -CertificatePath ".\certificados\codigo.pfx"
```

Opcionalmente se puede indicar la ruta de `signtool.exe`:

```powershell
.\installer\build-installer.ps1 -CertificateThumbprint "THUMBPRINT_DEL_CERTIFICADO" -SignToolPath "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"
```

## Ejecutar la aplicacion Windows

Despues de compilar, abre el ejecutable publicado por .NET:

```powershell
.\src\O2CloudDrive\bin\Release\net8.0-windows\win-x64\O2CloudDrive.exe
```

La ventana permite:

- Hacer `Login nuevo`, que elimina primero la sesion almacenada y abre el flujo oficial visible de O2 Cloud.
- Elegir la letra de unidad disponible.
- Cambiar el nombre/etiqueta del volumen.
- Marcar `Pedir login nuevo antes de montar` para no reutilizar una sesion guardada.
- Montar la unidad.
- Abrir la unidad en el Explorador.
- Desmontar la unidad.
- `Eliminar sesion guardada`, que desmonta si hace falta, borra la credencial de Windows Credential Manager y limpia el perfil WebView2 local usado por el login.

Al minimizar, la ventana se oculta y la aplicacion queda en la bandeja del sistema. Desde el icono de bandeja puedes abrir la ventana, montar, desmontar, eliminar la sesion guardada o salir.

Si la unidad se monta pero O2 devuelve la raiz sin elementos, la ventana lo avisa. En ese caso usa `Eliminar sesion guardada` y despues `Login nuevo` para evitar una credencial antigua, cookies antiguas del WebView2 o una sesion incompleta.

## Ejecutar desde comandos para pruebas

Comprueba primero que `O:` no este ocupada:

```powershell
Get-PSDrive O -ErrorAction SilentlyContinue
```

Para una prueba automatizada de montaje:

```powershell
.\.dotnet\dotnet.exe run --project .\src\O2CloudDrive\O2CloudDrive.csproj -c Release -- --mount O: --skip-auth --run-for-seconds 15
```

El modo normal de usuario ya no requiere consola: abre el `.exe` y pulsa `Login nuevo` o `Montar`. La app comprueba Windows Credential Manager. Si no hay sesion valida, abre una ventana WebView2 con el login oficial de O2 Cloud. Introduce telefono, contrasena y SMS ahi. Cuando la sesion valida, se guarda en Credential Manager y se monta la unidad elegida con datos reales.

El backend real permite crear carpetas, crear/subir archivos, renombrar, mover y enviar archivos o carpetas a la papelera. La escritura de archivos se confirma al cerrar o vaciar el manejador (`Flush/Cleanup` de WinFsp). Para el prototipo, la edicion de un archivo remoto existente descarga primero el archivo completo en memoria y esta limitada a 512 MB por operacion; la lectura normal sigue usando descargas parciales con `Range`.

Mientras el proceso este abierto, `O:` aparecera en el Explorador. Pulsa `Ctrl+C` en la consola para desmontar.

Para cerrar sesion y borrar la credencial guardada:

```powershell
dotnet run --project .\src\O2CloudDrive\O2CloudDrive.csproj -- --logout
```

Para montar solo el backend simulado sin login, con carpetas de prueba y escritura local:

```powershell
.\.dotnet\dotnet.exe run --project .\src\O2CloudDrive\O2CloudDrive.csproj -c Release -- --mount O: --skip-auth --run-for-seconds 15
```

Para validar el backend simulado sin montar WinFsp:

```powershell
.\.dotnet\dotnet.exe run --project .\src\O2CloudDrive\O2CloudDrive.csproj -c Release -- --self-test
```

## Configuracion

`src/O2CloudDrive/appsettings.json`:

```json
{
  "mountPoint": "O:",
  "volumeLabel": "O2 Cloud Prototype",
  "cacheDirectory": "%LOCALAPPDATA%\\O2CloudDrive\\Cache",
  "apiBaseUrl": "https://cloud.o2online.es/sapi/",
  "loginUrl": "https://cloud.o2online.es/",
  "credentialTarget": "O2CloudDrive.Session",
  "updateOwner": "chemyglez",
  "updateRepository": "O2CloudDrive",
  "checkForUpdatesOnStartup": true,
  "includePrereleaseUpdates": true,
  "useSimulatedData": false,
  "requireAuthentication": true
}
```

`O2CLOUD_VALIDATIONKEY` se puede usar para pruebas locales. El flujo normal guarda `validationkey`, cookies y user-agent en Windows Credential Manager; no se escriben en logs ni en archivos de configuracion.

## Estructura

```text
src/O2CloudDrive/
  Api/                  Cliente HTTP preparado para O2 Cloud
  Auth/                 Login WebView2, validacion y Credential Manager
  Caching/              Cache local de contenido
  Config/               Carga de appsettings y argumentos CLI
  Mounting/             Servicio de montaje/desmontaje WinFsp
  Transfers/            Fachada de subida/descarga
  Ui/                   Formulario principal y bandeja del sistema
  VirtualFileSystem/    Implementacion WinFsp, backend O2 real y backend simulado
```

## Implementacion actual

`O2VirtualFileSystem` depende de `ICloudFileStore`. La implementacion real `O2CloudFileStore` traduce:

- `GET sapi/media/folder?action=list&parentid={id}&limit=200` a carpetas.
- `POST sapi/media?action=get&folderid={id}&limit=200` a archivos.
- Descargas por `sapi/download/...` con soporte de `Range`.
- Descarga de archivos reales con URL temporal y cabecera `Range`.
- Fallback automatico de identificadores de carpeta: cuando O2 devuelve `id`, `folderid` o `folderId`, el cliente prueba los candidatos hasta encontrar el que lista contenido.
- `GET sapi/media?action=get-storage-space&softdeleted=true` a la capacidad mostrada por Windows.
- `POST sapi/media/folder?action=save` a crear/renombrar/mover carpetas.
- `POST sapi/upload/{tipo}?action=save-metadata` a renombrar/mover archivos.
- `POST sapi/media/{tipo}?action=delete&softdelete=true` a enviar archivos a papelera.
- `POST https://upload.cloud.o2online.es/sapi/upload?action=save` a subir archivos.

El `validationkey` debe tratarse siempre como credencial sensible.
