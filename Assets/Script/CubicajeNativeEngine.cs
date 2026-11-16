using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Puente hacia el motor de cubicaje nativo Intento14 (CubicajeNative.dll, ARM64 UWP).
/// Solo existe en el build de HoloLens: en el Editor IsAvailable es false y
/// CubicajeARManager usa el greedy en C# (CubicajeEngine).
///
/// Conversión de ejes (el motor usa Z como eje vertical):
///   Unity X (ancho) ↔ motor x
///   Unity Y (alto)  ↔ motor z   (z1 == 0 es el piso)
///   Unity Z (fondo) ↔ motor y   invertido: y = 0 es la pared trasera (+Z) y crece hacia el frente (-Z)
/// El origen (0,0,0) del motor es la esquina izquierda-piso-fondo del interior del pallet.
///
/// Unidades: el motor trabaja en milímetros enteros.
/// </summary>
public static class CubicajeNativeEngine
{
    // Debe coincidir con CubicajePiezaEntrada de CubicajeApi.h
    [StructLayout(LayoutKind.Sequential)]
    private struct PiezaEntrada
    {
        public int dimx, dimy, dimz;
    }

    // Debe coincidir con CubicajePiezaColocada de CubicajeApi.h
    [StructLayout(LayoutKind.Sequential)]
    private struct PiezaColocada
    {
        public int indicePieza;
        public int x1, y1, z1, x2, y2, z2;
    }

#if UNITY_WSA && !UNITY_EDITOR
    [DllImport("CubicajeNative")]
    private static extern int Cubicaje_Resolver(
        int contDimx, int contDimy, int contDimz,
        [In] PiezaEntrada[] piezas, int numPiezas,
        int maxTimeSeg,
        [Out] PiezaColocada[] salida, int capacidadSalida);

    [DllImport("CubicajeNative")]
    private static extern double Cubicaje_UltimaUtilizacion();
#endif

    /// <summary>
    /// Indica si el motor nativo se puede llamar en esta plataforma (solo build UWP, no Editor).
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
#if UNITY_WSA && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Resuelve el cubicaje con el motor nativo. Llamada síncrona: bloquea hasta maxTimeSeconds.
    /// Devuelve las piezas colocadas en secuencia de ensamblado, o null si el motor falló
    /// (el llamador debe usar el greedy como respaldo).
    /// </summary>
    /// <param name="pieces">Lista de piezas a colocar (de piezas.json).</param>
    /// <param name="palletInner">Volumen interior del pallet en espacio local (m).</param>
    /// <param name="maxTimeSeconds">Tiempo máximo de búsqueda del motor (s).</param>
    public static List<PlacedPiece> Solve(List<PieceEntry> pieces, Bounds palletInner, int maxTimeSeconds)
    {
#if UNITY_WSA && !UNITY_EDITOR
        if (pieces == null || pieces.Count == 0)
            return new List<PlacedPiece>();

        PiezaEntrada[] entrada = BuildInput(pieces);
        var salida = new PiezaColocada[entrada.Length];

        int colocadas;
        try
        {
            // Contenedor: alto de Unity (Y) va en dimz, fondo de Unity (Z) en dimy
            colocadas = Cubicaje_Resolver(
                MetersToMmFloor(palletInner.size.x),
                MetersToMmFloor(palletInner.size.z),
                MetersToMmFloor(palletInner.size.y),
                entrada, entrada.Length,
                maxTimeSeconds,
                salida, salida.Length);
        }
        catch (Exception ex)
        {
            // DllNotFoundException / EntryPointNotFoundException si la DLL no viajó en el build
            Debug.LogError($"[CubicajeNative] No se pudo llamar a la DLL: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (colocadas < 0)
        {
            Debug.LogError($"[CubicajeNative] El motor devolvió el código de error {colocadas}.");
            return null;
        }

        List<PlacedPiece> result = ConvertOutput(salida, colocadas, pieces, palletInner);
        CubicajeEngine.SortByAssemblyOrder(result);

        Debug.Log($"[CubicajeNative] Solución: {result.Count}/{pieces.Count} piezas colocadas. " +
                  $"Utilización={Cubicaje_UltimaUtilizacion()}");
        return result;
#else
        Debug.LogWarning("[CubicajeNative] El motor nativo solo está disponible en el build de HoloLens.");
        return null;
#endif
    }

    // ===== Métodos auxiliares =====

    /// <summary>
    /// Convierte cada PieceEntry al struct de entrada del motor, en mm y con el alto en dimz.
    /// El índice en el arreglo es el que el motor devuelve en indicePieza.
    /// </summary>
    private static PiezaEntrada[] BuildInput(List<PieceEntry> pieces)
    {
        var entrada = new PiezaEntrada[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            entrada[i] = new PiezaEntrada
            {
                dimx = MetersToMmCeil(pieces[i].ancho_m),
                dimy = MetersToMmCeil(pieces[i].fondo_m),
                dimz = MetersToMmCeil(pieces[i].alto_m)
            };
        }
        return entrada;
    }

    /// <summary>
    /// Convierte las cajas colocadas por el motor (esquinas en mm, z vertical, origen en
    /// la esquina izquierda-piso-fondo) a PlacedPiece en espacio local del pallet.
    /// </summary>
    private static List<PlacedPiece> ConvertOutput(
        PiezaColocada[] salida, int colocadas, List<PieceEntry> pieces, Bounds palletInner)
    {
        var result = new List<PlacedPiece>(colocadas);
        for (int i = 0; i < colocadas; i++)
        {
            PiezaColocada c = salida[i];
            if (c.indicePieza < 0 || c.indicePieza >= pieces.Count)
            {
                Debug.LogWarning($"[CubicajeNative] Índice de pieza fuera de rango: {c.indicePieza}. Se omite.");
                continue;
            }

            result.Add(new PlacedPiece
            {
                id         = pieces[c.indicePieza].id,
                localPos   = new Vector3(
                    palletInner.min.x + (c.x1 + c.x2) / 2000f,
                    palletInner.min.y + (c.z1 + c.z2) / 2000f,
                    palletInner.max.z - (c.y1 + c.y2) / 2000f),
                localEuler = Vector3.zero, // el tamaño orientado ya va en scale
                scale      = new Vector3(
                    (c.x2 - c.x1) / 1000f,
                    (c.z2 - c.z1) / 1000f,
                    (c.y2 - c.y1) / 1000f)
            });
        }
        return result;
    }

    /// <summary>
    /// Metros → mm enteros redondeando hacia arriba (piezas: nunca se achican, no se superponen).
    /// Se redondea antes a 3 decimales para que 0.166f no termine en 167 por error de float.
    /// </summary>
    private static int MetersToMmCeil(float meters) =>
        (int)Math.Ceiling(Math.Round(meters * 1000.0, 3));

    /// <summary>
    /// Metros → mm enteros redondeando hacia abajo (pallet: ninguna pieza sobresale).
    /// </summary>
    private static int MetersToMmFloor(float meters) =>
        (int)Math.Floor(Math.Round(meters * 1000.0, 3));
}
