# CubicajeHL3

Aplicación de Realidad Mixta para **HoloLens 2** que guía a un operario en el armado de un pallet (cubicaje):

1. Escanea el **QR maestro** de la orden (catálogo de piezas con sus dimensiones).
2. Escanea el **QR del pallet**: se ancla un pallet holográfico y un motor de cubicaje calcula posición, orientación y orden de armado de cada pieza.
3. Escanea el QR de cada caja: si es la siguiente de la secuencia, un holograma verde viaja a su lugar; si no, aparece en rojo.
4. Una **guía por voz** en español acompaña todo el proceso (orden escaneada, pallet escaneado, primera/siguiente pieza, pieza incorrecta, piezas que no caben, pallet completo).

---

## Requisitos

| Herramienta | Versión / detalle |
|---|---|
| Unity | **2022.3.62f1**, con el módulo *Universal Windows Platform Build Support* |
| Visual Studio | 2022, con las cargas *Desarrollo de la Plataforma universal de Windows* y *Desarrollo para el escritorio con C++* (incluir herramientas ARM64) |
| Dispositivo | HoloLens 2 en *Modo desarrollador*, con el Device Portal habilitado |

Todos los paquetes de MRTK3 / Mixed Reality están incluidos en `Packages/MixedReality/` como `.tgz`: **no** hace falta el Mixed Reality Feature Tool ni un registro de paquetes.

---

## Recrear el proyecto en otra computadora

1. Clonar el repositorio:
   ```
   git clone https://github.com/julianleonc33/Cubicaje-2.git
   ```
2. Abrir la carpeta con **Unity Hub → Add project from disk**, usando Unity 2022.3.62f1. La primera apertura tarda (importa paquetes y genera `Library/`).
3. Abrir la escena `Assets/Scenes/SampleScene.unity`.
4. **File → Build Settings**:
   - Plataforma: **Universal Windows Platform** → *Switch Platform*
   - Architecture: **ARM64**
   - Build Type: *D3D Project*
   - Agregar `SampleScene` a *Scenes In Build* si no está.
5. **Edit → Project Settings → XR Plug-in Management** (pestaña UWP): **OpenXR** activado con el grupo de funciones **Microsoft HoloLens**.
6. **Build** → genera una solución de Visual Studio en la carpeta elegida.
7. Abrir esa `.sln` en Visual Studio → configuración **Release / ARM64** → *Device* (USB) o *Remote Machine* (IP del HoloLens) → **Deploy**.
   - Alternativa: generar el paquete `.appx/.msix` y subirlo desde el Device Portal.

### Certificado de firma

El proyecto incluye `Assets/WSATestCertificate.pfx` (certificado de prueba autofirmado). Si al compilar aparece un error de certificado vencido o inválido, crear uno nuevo en **Project Settings → Player → Publishing Settings → Certificate → Create**.

---

## Configuración que ya viene en la escena (no romper)

Estos ajustes ya están guardados en `SampleScene`; se documentan para no deshacerlos por accidente:

- **`MRTK XR Rig`** (raíz) tiene los componentes `ARMarkerManager`, `CubicajeARManager` y un segundo **XR Origin** que Unity agregó automáticamente al añadir `ARMarkerManager`. Ese XR Origin debe quedar con **Tracking Origin Mode = Not Specified**; si se pone en *Device*, pelea con el `UnboundedTrackingMode` de MRTK y el origen de seguimiento cambia sin parar (`SetSceneOriginSpace` en el log).
- **`VoiceGuide`**: GameObject con el componente `VoiceGuide` (voz por defecto: *Helena*). Está asignado en el campo *Voice Guide* de `PieceScanManager` y de `CubicajeARManager`.
- **`Assets/link.xml`**: impide que el build (IL2CPP) elimine código de MRTK que se crea por reflexión. Sin él, los subsistemas de manos y de voz no arrancan en el HoloLens.

### Voz en español en el HoloLens

La app usa la voz **Microsoft Helena (es-ES)**. El HoloLens debe tener instalado el idioma de voz español (Configuración → Hora e idioma → Idioma → Español → *Voz*). Al arrancar, la app deja en el log la lista de voces instaladas (`[VoiceGuide] Voz instalada: ...`). Para usar otra voz (ej. *Laura* o *Pablo*), cambiar *Preferred Voice Name* en el componente `VoiceGuide`.

---

## Motor de cubicaje

Se elige en el Inspector: `MRTK XR Rig` → *Cubicaje AR Manager* → **Motor Cubicaje** (tiempo de búsqueda en *Max Solve Seconds*).

| Opción | Qué hace |
|---|---|
| **C Sharp Intento14** | Intento14 traducido a C# (`Assets/Script/CubicajeCSharp/`, del repo Cubicaje-HL2-Engine). Un pallet. Corre en el HoloLens y en el Editor. |
| **Nativo Intento14** | `Assets/Plugins/WSA/ARM64/CubicajeNative.dll`. Un pallet. Solo en el build del HoloLens. |
| **Greedy** | Greedy simple en C# (`CubicajeEngine.cs`). También es el respaldo si el motor elegido falla. |
| **Bin Packing C Sharp** | Reparte toda la orden en el menor número de pallets iguales; se arman **uno tras otro** con el mismo QR `PALLET`. |

### Multi-pallet (Bin Packing C Sharp)

1. Al escanear `PALLET` la voz dice cuántos pallets usa la orden y la primera pieza del pallet 1.
2. Al completar un pallet: "Pallet 1 completo. Coloque el pallet 2 y escanéelo". Durante *Next Pallet Delay Seconds* (3 s) se ignora el QR `PALLET`, para que el QR del pallet terminado no ancle el siguiente al instante.
3. Al escanear `PALLET` de nuevo, el pallet holográfico anterior se borra y se ancla el siguiente sobre ese QR.
4. Si se escanea una pieza de un pallet posterior: "La pieza X va en el pallet N". Las de pallets ya armados se ignoran.
5. Al terminar el último: "Pallet completo. Orden completa".

### Versiones (git)

| Tag / rama | Contenido |
|---|---|
| `v1.0-voz-motor-nativo` | Motor nativo + guía por voz |
| `v1.1-motor-csharp` | Motor Intento14 en C# (un pallet) |
| rama `motor-binpacking` | Multi-pallet (en prueba) |

### Recompilar la DLL nativa (solo si se modifica el motor)

El código fuente está en `Intento14 modificado/` (`Intento14.cpp`, `CubicajeApi.h`, `CubicajeNative.vcxproj`).

1. Abrir `Intento14 modificado/Intento14.sln` en Visual Studio 2022.
2. Proyecto **CubicajeNative**, configuración **Release | ARM64** → *Build*.
3. Copiar el `CubicajeNative.dll` generado a `Assets/Plugins/WSA/ARM64/`, reemplazando el existente.

---

## Formatos de QR

| QR | Formato | Ejemplo |
|---|---|---|
| Maestro (orden) | `ID;alto;ancho;fondo` repetido, en metros | `A1;0.080;0.160;0.040;B1;0.080;0.120;0.080` |
| Pallet | `PALLET` | `PALLET` |
| Pieza | ID de la pieza | `A1` |

`QR_Piezas_Cubicaje.html` y `QRpack.pdf` contienen QR de ejemplo para imprimir.

---

## Scripts principales (`Assets/Script/`)

| Script | Función |
|---|---|
| `StartMenuManager.cs` | Menú inicial (Aceptar / Cancelar); habilita el escaneo |
| `PieceScanManager.cs` | Lee el QR maestro y guarda `piezas.json` |
| `CubicajeARManager.cs` | Pallet, motor de cubicaje, secuencia de armado y hologramas |
| `CubicajeCSharpEngine.cs` | Puente con el motor Intento14 en C# (un pallet y bin packing) |
| `CubicajeCSharp/` | Motor Intento14 traducido a C# |
| `CubicajeNativeEngine.cs` | Puente (P/Invoke) con la DLL nativa |
| `CubicajeEngine.cs` | Motor greedy en C# y orden de armado |
| `VoiceGuide.cs` | Guía por voz (texto a voz de MRTK3) |
| `PieceEntry.cs` | Modelo de datos de piezas |

## Depuración

Los logs se descargan desde el **Device Portal → System → File explorer → LocalAppData → (paquete de la app) → TempState** (`UnityPlayer.log` y `UnityPlayer-prev.log`). Prefijos útiles para filtrar: `[CubicajeAR]`, `[PieceScan]`, `[VoiceGuide]`, `[CubicajeCSharp]`, `[CubicajeBinPacking]`, `[CubicajeNative]`.
