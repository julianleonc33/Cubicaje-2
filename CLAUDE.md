# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**CubicajeHL3** is a Unity Mixed Reality application for HoloLens 2. It guides a user through assembling a physical cubic puzzle: first scanning a **master QR** that encodes the full piece catalog for the order, then scanning a **pallet QR** to anchor a holographic pallet and dynamically compute an assembly sequence, showing holographic feedback as each piece is scanned.

## Build & Deploy

This is a Unity project targeting **UWP / HoloLens 2**. There is no CLI build script — all builds go through the Unity Editor:

1. Open the project in Unity (tested with the version matching `ProjectSettings/ProjectVersion.txt`)
2. Build target: **Universal Windows Platform** → ARM64
3. XR Plugin: **OpenXR** with Microsoft HoloLens feature set enabled
4. Deploy the generated Visual Studio `.sln` to the device via the HoloLens Device Portal or USB

All MRTK and Mixed Reality packages are vendored locally under `Packages/MixedReality/` as `.tgz` files — no package registry needed.

## Architecture

### Two Independent Operational Flows

**Flow 1 — Master QR Catalog Scan** (`PieceScanManager.cs`)
- Scans a single **master QR** encoding N pieces concatenated as `"ID;alto;ancho;fondo;ID;alto;ancho;fondo;..."` (each piece = exactly 4 fields, total field count must be a multiple of 4; e.g. `"A1;0.166;0.083;0.0415;A2;0.12;0.09;0.05"`)
- Each successful scan **replaces** the current order (`scannedPieces.Clear()` before loading), so re-scanning a master QR starts a fresh order rather than merging with a previous one
- Displays a cyan hologram at the QR location (sized to the first piece in the QR) for `hologramLifetime` seconds as scan confirmation
- Auto-saves all pieces from the scan to `Application.persistentDataPath/piezas.json` immediately after parsing
- There is no per-piece QR format anymore and no "Detener" finalize button in this script — one master QR scan is the complete catalog load

**Flow 2 — Assembly Guidance** (`CubicajeARManager.cs`)
- Scans a `"PALLET"` QR; on first detection it reads `piezas.json`, runs `CubicajeEngine.Solve()` to compute placement + assembly order, then anchors a holographic hollow-box pallet in world space (via `ARAnchor`)
- The assembly sequence is **not hardcoded** — it is whatever `CubicajeEngine.Solve()` returns for the pieces present in `piezas.json` at pallet-scan time (see Cubicaje Engine below)
- If a piece doesn't fit in the pallet, it's dropped from the sequence (logged as "Armado parcial"); scanning its QR afterward is ignored (`IsKnownPieceId`) rather than shown as an error
- Correct piece: green hologram travels along a two-segment quadratic Bézier curve (approach in front of the pallet, then into its slot)
- Wrong piece (a known ID, just out of order): red hologram appears at the QR location for ~1 second
- Completion: back wall of the pallet pulses to bright green, and marker scanning is disabled

### Flow Control (`StartMenuManager.cs`)
- On start, shows an MRTK3 billboard menu and disables both `PieceScanManager` and `ARMarkerManager`
- **Accept** button: fades out menu, enables `markerManager` and `scanManager`
- **Cancel** button: quits the application

### Data Model (`PieceEntry.cs`)
- `PieceEntry`: `{ id, alto_m, ancho_m, fondo_m }` — dimensions in Unity meters (Y=alto, X=ancho, Z=fondo)
- `PieceListWrapper`: JSON serialization wrapper for `List<PieceEntry>`

### Cubicaje Engine (`CubicajeEngine.cs`)
- `CubicajeEngine.Solve(pieces, palletInner, margin)` is a greedy 3D shelf-packing algorithm, called by `CubicajeARManager` right when the `PALLET` QR is first scanned
- Steps: sort pieces by volume descending → for each piece pick the orientation (of 6 possible) with the lowest height that fits → place left→right (X), wrap to next row in depth (Z) when full, wrap to next layer (Y) when a row can't fit
- Output is `List<PlacedPiece>` (`{ id, localPos, localEuler, scale }`, local to the pallet), sorted into assembly order by `Y asc → Z asc → X asc` (floor before ceiling, back of pallet before front, left before right)
- Pieces that don't fit in any orientation are logged and dropped from both the placement and the sequence

### Key Design Decisions
- Piece positions, rotations, and the assembly sequence are computed **dynamically** by `CubicajeEngine.Solve()` from whatever is in `piezas.json` at the moment the `PALLET` QR is scanned — nothing about piece count or order is hardcoded in `CubicajeARManager`
- The piece catalog itself comes from a single **master QR** (`PieceScanManager`), not one QR per piece
- QR spam is suppressed by two independent cooldowns: per-text (`qrCooldown`) and global (`qrGlobalCooldownSeconds`), both configurable in the Inspector
- The pallet is procedurally built from primitive Cubes at runtime (`CreateHollowPallet`), using `Legacy Shaders/Transparent/Diffuse` for semi-transparent walls
- The pallet faces the camera at the moment of first detection and does not track the QR further (locked via `ARAnchor`)

## Scenes

- `Assets/Scenes/SampleScene.unity` — main scene (assembly guidance flow)
- `Assets/Cambio con letrero.unity` — alternate scene (purpose unknown from code alone)

## QR Code Formats

| Context | Format | Example |
|---|---|---|
| Master QR (catalog, Flow 1) | `ID;alto;ancho;fondo` repeated, concatenated | `A1;0.166;0.083;0.0415;A2;0.12;0.09;0.05` |
| Pallet marker (Flow 2) | `PALLET` | `PALLET` |
| Assembly piece (Flow 2) | bare ID, must match a piece placed by `CubicajeEngine.Solve()` | `A1`, `B3` |

---

## Rol de la IA

Actúa como un desarrollador senior de Unity especializado en AR/MR con HoloLens 2 y MRTK3.
Antes de escribir cualquier línea de código:
- Explora la estructura del proyecto y los scripts existentes
- Haz las preguntas necesarias para entender el contexto
- Diseña la solución en texto antes de implementarla
- Evalúa si cada cambio puede generar efectos en cascada sobre otros scripts
- Prefiere soluciones simples, legibles y fáciles de debuggear sobre soluciones complejas

---

## Stack técnico

- Motor: Unity 2022.3.62f1
- Plataforma destino: HoloLens 2
- Framework AR: MRTK3
- Lenguaje: C#

---

## Descripción del proyecto

Se recreará una zona de empaquetado en AR. El flujo completo es:

1. Un operador (packer) con HoloLens 2 escanea un código QR de una orden
2. El sistema recupera los datos de la orden: lista de piezas con sus dimensiones y datos del pallet destino
3. Un algoritmo de cubicaje calcula la orientación y posición óptima (x, y, z) de cada pieza dentro del pallet
4. El algoritmo genera una secuencia de acomodación: primero las piezas con menor Y (piso), priorizando las más cercanas al origen (0,0,0) del pallet, llenando de atrás hacia adelante
5. El operador escanea el QR de cada caja física (ej: ID "A1")
6. Al escanear, aparece un holograma de esa pieza que viaja a su posición y orientación correcta dentro del pallet virtual
7. Si la pieza escaneada no corresponde a la siguiente en la secuencia, el holograma aparece en rojo y no se desplaza
8. Al completar toda la secuencia, aparece un mensaje de finalización

El script CubicajeAR existente tiene un ejemplo hardcodeado de este flujo.
Ese es el punto de partida y la guía de referencia de estilo y estructura.
El objetivo es hacer ese flujo dinámico, no reemplazar la lógica sino alimentarla con datos reales.

---

## Flujo de trabajo

- Claude propone → Julian despliega en HoloLens 2 → Julian da feedback → se ajusta → siguiente componente
- No avanzar al siguiente componente sin confirmación
- Si se detecta riesgo de romper algo existente, avisar antes de proceder
- Si se necesita ver un script específico, pedirlo explícitamente

---

## Estilo de código

- Comentarios en cada método explicando qué hace y por qué
- Variables con nombres descriptivos, consistentes en todo el proyecto
- Métodos cortos con responsabilidad única
- Sin lógica compleja en un solo bloque; preferir funciones auxiliares con nombres claros
- Si algo no está claro, preguntar antes de asumir
- Nunca generar scripts con demasiada complejidad; priorizar que sean legibles y debuggeables

---

## Paso 0 — Inicio de cada sesión

Antes de cualquier implementación:
1. Leer todos los scripts existentes del proyecto
2. Identificar qué está implementado vs qué falta
3. Presentar un mapa de componentes: qué script hace qué y qué se conecta con qué
4. Proponer el plan por fases antes de escribir código

No escribir ningún script hasta que Julian apruebe el plan.
