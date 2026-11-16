using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Resultado de colocación de una pieza dentro del pallet.
/// Equivalente dinámico al PieceDef hardcodeado en CubicajeARManager.
/// </summary>
[Serializable]
public class PlacedPiece
{
    public string id;          // Identificador de la pieza (ej: "A1")
    public Vector3 localPos;   // Centro en espacio local del pallet
    public Vector3 localEuler; // Rotación Euler en espacio local (0: el tamaño ya viene orientado en scale)
    public Vector3 scale;      // Dimensiones reales tras aplicar orientación (X=ancho, Y=alto, Z=fondo)
}

/// <summary>
/// Motor de cubicaje greedy en C#: calcula posición y orientación de cada pieza dentro del pallet.
/// Se usa en el Editor y como respaldo si el motor nativo (CubicajeNativeEngine) no está disponible.
///
/// Convención del pallet (espacio local): la cara abierta está en -Z y la pared trasera en +Z.
///
/// Algoritmo: shelf packing greedy 3D.
///   1. Ordena piezas por volumen descendente (piezas más grandes primero).
///   2. Para cada pieza elige la orientación con menor altura (Y) que quepa.
///   3. Coloca de izquierda a derecha (X); cuando la fila se llena, avanza hacia el frente (-Z).
///   4. Cuando la capa se llena, sube a la siguiente capa (Y).
///   5. La secuencia de ensamblado se ordena con SortByAssemblyOrder
///      (piso primero, fondo antes que frente, izquierda antes que derecha).
/// </summary>
public static class CubicajeEngine
{
    // Orientación candidata de una pieza: sus dimensiones ya permutadas sobre los ejes del pallet.
    // No lleva rotación: el holograma es un cubo escalado a (w, h, d), así que no hace falta rotarlo.
    private struct Orientation
    {
        public float w;        // Dimensión en X (ancho real tras rotar)
        public float h;        // Dimensión en Y (alto real tras rotar)
        public float d;        // Dimensión en Z (fondo real tras rotar)
    }

    /// <summary>
    /// Resuelve la colocación de todas las piezas dentro del pallet y devuelve
    /// la lista ordenada como secuencia de ensamblado.
    /// </summary>
    /// <param name="pieces">Lista de piezas a colocar (de piezas.json).</param>
    /// <param name="palletInner">Volumen interior del pallet en espacio local (m).</param>
    /// <param name="margin">Margen mínimo entre piezas y entre piezas y paredes (m).</param>
    /// <returns>Piezas colocadas, ordenadas por secuencia de ensamblado.</returns>
    public static List<PlacedPiece> Solve(List<PieceEntry> pieces, Bounds palletInner, float margin = 0.002f)
    {
        if (pieces == null || pieces.Count == 0)
        {
            Debug.LogWarning("[CubicajeEngine] La lista de piezas está vacía.");
            return new List<PlacedPiece>();
        }

        // Ordenar por volumen descendente: piezas grandes primero aprovechan mejor el espacio
        var sorted = new List<PieceEntry>(pieces);
        sorted.Sort((a, b) => PieceVolume(b).CompareTo(PieceVolume(a)));

        var result = new List<PlacedPiece>();

        // Estado del cursor de colocación (en espacio local del pallet).
        // Se parte en la esquina izquierda-piso-fondo y se avanza hacia la cara abierta (-Z).
        float layerYMin  = palletInner.min.y + margin;  // borde inferior de la capa actual
        float layerMaxH  = 0f;                          // altura máxima vista en la capa actual
        float rowZMax    = palletInner.max.z - margin;  // borde trasero de la fila actual
        float rowMaxD    = 0f;                          // profundidad máxima vista en la fila actual
        float cursorX    = palletInner.min.x + margin;  // cursor horizontal dentro de la fila

        foreach (var piece in sorted)
        {
            // Elegir la orientación con menor altura que quepa en el pallet
            var ori = PickLowestFittingOrientation(piece, palletInner.size, margin);

            if (ori == null)
            {
                Debug.LogWarning($"[CubicajeEngine] Pieza '{piece.id}' no cabe en ninguna orientación. Se omite.");
                continue;
            }

            float pw = ori.Value.w;
            float ph = ori.Value.h;
            float pd = ori.Value.d;

            // ¿La pieza supera el borde derecho de la fila actual?
            if (cursorX + pw > palletInner.max.x - margin)
            {
                // Iniciar nueva fila dentro de la misma capa, más cerca del frente
                rowZMax  -= rowMaxD + margin;
                rowMaxD   = 0f;
                cursorX   = palletInner.min.x + margin;
            }

            // ¿La fila supera la cara abierta (frente) del pallet?
            if (rowZMax - pd < palletInner.min.z + margin)
            {
                // Subir a la siguiente capa y volver al fondo
                layerYMin += layerMaxH + margin;
                layerMaxH  = 0f;
                rowZMax    = palletInner.max.z - margin;
                rowMaxD    = 0f;
                cursorX    = palletInner.min.x + margin;
            }

            // ¿Hay espacio vertical en la capa?
            if (layerYMin + ph > palletInner.max.y - margin)
            {
                Debug.LogWarning($"[CubicajeEngine] Pieza '{piece.id}': no hay espacio vertical restante. Se omite.");
                continue;
            }

            // Centro de la pieza en espacio local del pallet
            float cx = cursorX + pw / 2f;
            float cy = layerYMin + ph / 2f;
            float cz = rowZMax - pd / 2f;

            result.Add(new PlacedPiece
            {
                id         = piece.id,
                localPos   = new Vector3(cx, cy, cz),
                localEuler = Vector3.zero,
                scale      = new Vector3(pw, ph, pd)
            });

            // Avanzar cursor y actualizar máximos de la fila/capa
            cursorX  += pw + margin;
            rowMaxD   = Mathf.Max(rowMaxD, pd);
            layerMaxH = Mathf.Max(layerMaxH, ph);
        }

        SortByAssemblyOrder(result);

        Debug.Log($"[CubicajeEngine] Solución: {result.Count}/{sorted.Count} piezas colocadas.");

        return result;
    }

    /// <summary>
    /// Ordena las piezas colocadas en la secuencia de ensamblado. Lo usan ambos motores
    /// (greedy y nativo) para que el operador siga siempre el mismo criterio:
    ///   1. Base más baja primero      → piso antes que techo
    ///   2. Cara trasera más al fondo  → fondo (+Z) antes que frente (-Z)
    ///   3. Borde izquierdo primero    → izquierda antes que derecha
    /// Se compara en milímetros enteros para que el ruido de float no altere el orden.
    /// </summary>
    public static void SortByAssemblyOrder(List<PlacedPiece> placed)
    {
        placed.Sort((a, b) =>
        {
            int yComp = ToMm(MinY(a)).CompareTo(ToMm(MinY(b)));
            if (yComp != 0) return yComp;

            int zComp = ToMm(MaxZ(b)).CompareTo(ToMm(MaxZ(a))); // descendente: fondo primero
            if (zComp != 0) return zComp;

            return ToMm(MinX(a)).CompareTo(ToMm(MinX(b)));
        });
    }

    // ===== Métodos auxiliares =====

    /// <summary>
    /// Devuelve el volumen de una pieza.
    /// </summary>
    private static float PieceVolume(PieceEntry p) =>
        p.ancho_m * p.alto_m * p.fondo_m;

    /// <summary>Base (Y mínima) de una pieza colocada.</summary>
    private static float MinY(PlacedPiece p) => p.localPos.y - p.scale.y / 2f;

    /// <summary>Cara trasera (Z máxima) de una pieza colocada.</summary>
    private static float MaxZ(PlacedPiece p) => p.localPos.z + p.scale.z / 2f;

    /// <summary>Borde izquierdo (X mínima) de una pieza colocada.</summary>
    private static float MinX(PlacedPiece p) => p.localPos.x - p.scale.x / 2f;

    /// <summary>Convierte metros a milímetros enteros (redondeando) para comparar.</summary>
    private static int ToMm(float meters) => Mathf.RoundToInt(meters * 1000f);

    /// <summary>
    /// Elige la orientación con menor altura (Y) entre las que caben en el pallet.
    /// Evalúa las 6 orientaciones posibles de una caja rectangular.
    /// </summary>
    private static Orientation? PickLowestFittingOrientation(
        PieceEntry p, Vector3 palletInnerSize, float margin)
    {
        float a = p.ancho_m;
        float b = p.alto_m;
        float c = p.fondo_m;

        // Las 6 permutaciones posibles de (ancho, alto, fondo) sobre los ejes (X, Y, Z)
        var candidates = new Orientation[]
        {
            new Orientation { w=a, h=b, d=c },
            new Orientation { w=c, h=b, d=a },
            new Orientation { w=a, h=c, d=b },
            new Orientation { w=c, h=a, d=b },
            new Orientation { w=b, h=a, d=c },
            new Orientation { w=b, h=c, d=a },
        };

        Orientation? best = null;

        foreach (var o in candidates)
        {
            // Verificar que la pieza con este tamaño cabe dentro de los límites del pallet
            bool fitsX = o.w <= palletInnerSize.x - 2f * margin;
            bool fitsY = o.h <= palletInnerSize.y - 2f * margin;
            bool fitsZ = o.d <= palletInnerSize.z - 2f * margin;

            if (!fitsX || !fitsY || !fitsZ)
                continue;

            // Preferir la orientación con menor altura (deja más espacio para capas superiores)
            if (best == null || o.h < best.Value.h)
                best = o;
        }

        return best;
    }
}
