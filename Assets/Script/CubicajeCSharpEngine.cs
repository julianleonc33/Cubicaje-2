using System;
using System.Collections.Generic;
using UnityEngine;
using Cubicaje;

/// <summary>
/// Puente hacia el motor de cubicaje Intento14 traducido a C# (carpeta CubicajeCSharp/,
/// tomada de github.com/julianleonc33/Cubicaje-HL2-Engine). Es el mismo GRASP + Beam Search
/// de la DLL nativa, pero como código C#: corre en el HoloLens y también en el Editor.
///
/// Conversión de ejes (idéntica a CubicajeNativeEngine; el motor usa Z como eje vertical):
///   Unity X (ancho) ↔ motor x
///   Unity Y (alto)  ↔ motor z   (z1 == 0 es el piso)
///   Unity Z (fondo) ↔ motor y   invertido: y = 0 es la pared trasera (+Z) y crece hacia el frente (-Z)
/// El origen (0,0,0) del motor es la esquina izquierda-piso-fondo del interior del pallet.
///
/// Unidades: el motor trabaja en milímetros enteros.
/// </summary>
public static class CubicajeCSharpEngine
{
    /// <summary>
    /// Resuelve el cubicaje. Llamada síncrona: bloquea hasta maxTimeSeconds.
    /// Devuelve las piezas colocadas en secuencia de ensamblado, o null si el motor falló
    /// (el llamador debe usar el greedy como respaldo).
    /// </summary>
    /// <param name="pieces">Lista de piezas a colocar (de piezas.json).</param>
    /// <param name="palletInner">Volumen interior del pallet en espacio local (m).</param>
    /// <param name="maxTimeSeconds">Tiempo máximo de búsqueda del motor (s).</param>
    public static List<PlacedPiece> Solve(List<PieceEntry> pieces, Bounds palletInner, float maxTimeSeconds)
    {
        if (pieces == null || pieces.Count == 0)
            return new List<PlacedPiece>();

        ResultadoCubicaje resultado;
        try
        {
            // Contenedor: alto de Unity (Y) va en z del motor, fondo de Unity (Z) en y
            resultado = CubicajeSolver.Resolver(
                MetersToMmFloor(palletInner.size.x),
                MetersToMmFloor(palletInner.size.z),
                MetersToMmFloor(palletInner.size.y),
                BuildInput(pieces),
                maxTimeSeconds);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CubicajeCSharp] El motor lanzó una excepción: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        LogResult(resultado, pieces.Count);

        List<PlacedPiece> result = ConvertOutput(resultado, pieces, palletInner);
        CubicajeEngine.SortByAssemblyOrder(result);
        return result;
    }

    /// <summary>
    /// Bin packing (variante ③): reparte TODAS las piezas en el menor número de pallets
    /// idénticos. Devuelve un plan por pallet (cada uno en secuencia de ensamblado),
    /// o null si el motor falló. Las piezas que no caben ni en un pallet vacío no aparecen.
    /// </summary>
    /// <param name="pieces">Lista de piezas a colocar (de piezas.json).</param>
    /// <param name="palletInner">Volumen interior de un pallet en espacio local (m).</param>
    /// <param name="maxTimeSeconds">Tiempo máximo de búsqueda del motor (s).</param>
    public static List<List<PlacedPiece>> SolveBinPacking(List<PieceEntry> pieces, Bounds palletInner, float maxTimeSeconds)
    {
        if (pieces == null || pieces.Count == 0)
            return new List<List<PlacedPiece>>();

        ResultadoBinPacking resultado;
        try
        {
            resultado = BinPackingSolver.Resolver(
                MetersToMmFloor(palletInner.size.x),
                MetersToMmFloor(palletInner.size.z),
                MetersToMmFloor(palletInner.size.y),
                BuildInput(pieces),
                maxTimeSeconds);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CubicajeBinPacking] El motor lanzó una excepción: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        LogBinPackingResult(resultado, pieces.Count);

        var plans = new List<List<PlacedPiece>>(resultado.NumContenedores);
        foreach (ResultadoCubicaje contenedor in resultado.Contenedores)
        {
            List<PlacedPiece> plan = ConvertOutput(contenedor, pieces, palletInner);
            CubicajeEngine.SortByAssemblyOrder(plan);
            plans.Add(plan);
        }
        return plans;
    }

    // ===== Métodos auxiliares =====

    /// <summary>
    /// Deja en el log el resultado del bin packing: número de pallets frente a la cota
    /// inferior por volumen, utilización de cada pallet y rendimiento (iter/s).
    /// </summary>
    private static void LogBinPackingResult(ResultadoBinPacking r, int totalPieces)
    {
        int colocadas = 0;
        var utilizaciones = new List<string>();
        foreach (ResultadoCubicaje c in r.Contenedores)
        {
            colocadas += c.Colocadas.Count;
            utilizaciones.Add($"{c.Colocadas.Count} piezas {c.Utilizacion:P1}");
        }

        double iterPerSecond = r.DuracionSeg > 0 ? r.Iteraciones / r.DuracionSeg : 0;
        Debug.Log($"[CubicajeBinPacking] Solución: {r.NumContenedores} pallets (cota inferior {r.CotaInferior}), " +
                  $"colocadas {colocadas}/{totalPieces}. Por pallet: [{string.Join(" | ", utilizaciones)}]  " +
                  $"Iteraciones={r.Iteraciones} (búsqueda local={r.IteracionesBusquedaLocal})  " +
                  $"Duración={r.DuracionSeg:F2}s  ({iterPerSecond:F0} iter/s)");
    }

    /// <summary>
    /// Convierte cada PieceEntry a una pieza del motor, en mm y con el alto en z.
    /// La posición en la lista es la que el motor devuelve en IndicePieza.
    /// Rotación libre (las 6 orientaciones), igual que la DLL nativa.
    /// </summary>
    private static List<PiezaEntrada> BuildInput(List<PieceEntry> pieces)
    {
        var entrada = new List<PiezaEntrada>(pieces.Count);
        foreach (var piece in pieces)
        {
            entrada.Add(new PiezaEntrada(
                piece.id,
                MetersToMmCeil(piece.ancho_m),
                MetersToMmCeil(piece.fondo_m),
                MetersToMmCeil(piece.alto_m)));
        }
        return entrada;
    }

    /// <summary>
    /// Convierte las cajas colocadas por el motor (esquinas en mm, z vertical, origen en
    /// la esquina izquierda-piso-fondo) a PlacedPiece en espacio local del pallet.
    /// </summary>
    private static List<PlacedPiece> ConvertOutput(ResultadoCubicaje resultado, List<PieceEntry> pieces, Bounds palletInner)
    {
        var result = new List<PlacedPiece>(resultado.Colocadas.Count);
        foreach (PiezaColocada c in resultado.Colocadas)
        {
            result.Add(new PlacedPiece
            {
                id         = pieces[c.IndicePieza].id,
                localPos   = new Vector3(
                    palletInner.min.x + (c.X1 + c.X2) / 2000f,
                    palletInner.min.y + (c.Z1 + c.Z2) / 2000f,
                    palletInner.max.z - (c.Y1 + c.Y2) / 2000f),
                localEuler = Vector3.zero, // el tamaño orientado ya va en scale
                scale      = new Vector3(
                    (c.X2 - c.X1) / 1000f,
                    (c.Z2 - c.Z1) / 1000f,
                    (c.Y2 - c.Y1) / 1000f)
            });
        }
        return result;
    }

    /// <summary>
    /// Deja en el log el resultado y el esfuerzo del motor: las iteraciones por segundo
    /// son la medida para comparar el rendimiento del HoloLens con el del PC.
    /// </summary>
    private static void LogResult(ResultadoCubicaje r, int totalPieces)
    {
        double iterPerSecond = r.DuracionSeg > 0 ? r.Iteraciones / r.DuracionSeg : 0;
        Debug.Log($"[CubicajeCSharp] Solución: {r.Colocadas.Count}/{totalPieces} piezas colocadas. " +
                  $"Utilización={r.Utilizacion:P2}  Iteraciones={r.Iteraciones} (búsqueda local={r.IteracionesBusquedaLocal})  " +
                  $"Duración={r.DuracionSeg:F2}s  ({iterPerSecond:F0} iter/s)");
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
