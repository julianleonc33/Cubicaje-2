using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Microsoft.MixedReality.OpenXR;

/// <summary>
/// Gestiona la fase de escaneo de piezas:
///   1. Escucha un QR maestro con formato "ID;alto;ancho;fondo;ID;alto;ancho;fondo;..."
///      (N piezas concatenadas, cada una con exactamente 4 campos separados por ';')
///   2. Muestra un holograma cian por 1 segundo como confirmación de lectura
///   3. Guarda todas las piezas en un JSON automáticamente tras el escaneo
///
/// Setup en Unity Editor:
///   - Asignar ARMarkerManager en el campo markerMgr
///</summary>
public class PieceScanManager : MonoBehaviour
{
    // ===== AR =====
    [Header("AR")]
    [Tooltip("ARMarkerManager configurado en la escena.")]
    public ARMarkerManager markerMgr;

    // ===== Apariencia holograma =====
    [Header("Holograma de escaneo")]
    [Tooltip("Color del holograma que aparece al escanear una pieza.")]
    public Color scanColor = new Color(0f, 0.45f, 0.55f, 0.8f);   // Cian semi-transparente

    [Tooltip("Tiempo que el holograma permanece visible (s).")]
    public float hologramLifetime = 4.0f;

    // ===== Filtros anti-spam =====
    [Header("Filtros QR")]
    [Tooltip("Tiempo mínimo entre el MISMO texto de QR (s).")]
    public float qrCooldown = 0.40f;

    [Tooltip("Tiempo mínimo entre CUALQUIER QR procesado (s).")]
    public float qrGlobalCooldown = 2.0f;

    [Tooltip("Segundos al inicio en que se ignoran lecturas.")]
    public float coldStartIgnore = 0.5f;

    // ===== Internos =====
    private float appStartTime;
    private string lastQR;
    private float lastQRTime = -999f;
    private float lastAnyQRTime = -999f;

    /// <summary>Piezas escaneadas, indexadas por ID (evita duplicados).</summary>
    private readonly Dictionary<string, PieceEntry> scannedPieces = new Dictionary<string, PieceEntry>();

    // ===== Ciclo de vida =====

    void Awake()
    {
        appStartTime = Time.realtimeSinceStartup;

        if (markerMgr == null)
            markerMgr = FindObjectOfType<ARMarkerManager>();

        if (markerMgr == null)
        {
            Debug.LogError("[PieceScan] No se encontró ARMarkerManager en la escena.");
            enabled = false;
            return;
        }
    }

    void OnEnable()
    {
        if (markerMgr != null)
            markerMgr.markersChanged += OnMarkersChanged;

        Debug.Log("[PieceScan] Escaneo iniciado. Apunta a los QR de las piezas.");
    }

    void OnDisable()
    {
        if (markerMgr != null)
            markerMgr.markersChanged -= OnMarkersChanged;
    }

    // ===== Eventos de marcadores =====

    private void OnMarkersChanged(ARMarkersChangedEventArgs e)
    {
        foreach (var m in e.added)   TryProcessMarker(m);
        foreach (var m in e.updated) TryProcessMarker(m);
    }

    private void TryProcessMarker(ARMarker m)
    {
        if (m == null) return;
        if (Time.realtimeSinceStartup < appStartTime + coldStartIgnore) return;
        if (m.trackingState != TrackingState.Tracking) return;

        var decoded = markerMgr.GetDecodedString(m.trackableId);
        if (string.IsNullOrEmpty(decoded)) return;

        string qr = decoded.Trim();
        float now = Time.time;

        // Cooldown global
        if (now - lastAnyQRTime < qrGlobalCooldown) return;

        // Cooldown mismo QR
        if (qr == lastQR && (now - lastQRTime) < qrCooldown) return;

        lastQR = qr;
        lastQRTime = now;
        lastAnyQRTime = now;

        // Parsear QR maestro con formato "ID;alto;ancho;fondo;ID;alto;ancho;fondo;..."
        if (!TryParseMasterQR(qr, out List<PieceEntry> entries))
        {
            Debug.LogWarning($"[PieceScan] QR no reconocido o mal formado: '{qr}'");
            return;
        }

        // Guardar todas las piezas del QR maestro como una orden completa nueva.
        scannedPieces.Clear();

        foreach (var entry in entries)
        {
            scannedPieces[entry.id] = entry;
            Debug.Log($"[PieceScan] Pieza cargada → ID={entry.id}  alto={entry.alto_m}m  ancho={entry.ancho_m}m  fondo={entry.fondo_m}m");
        }

        Debug.Log($"[PieceScan] QR maestro procesado como orden nueva. Total en sesión: {scannedPieces.Count}");

        // Auto-guardar tras el escaneo
        SaveToJson();

        // Mostrar holograma de confirmación (usando la primera pieza como referencia visual)
        SpawnScanHologram(m, entries[0]);
    }

    // ===== Parseo del QR =====

    /// <summary>
    /// Parsea el QR maestro con formato "ID;alto;ancho;fondo;ID;alto;ancho;fondo;..."
    /// Cada pieza ocupa exactamente 4 campos. La cantidad total de campos debe ser
    /// múltiplo de 4. Retorna false si el formato no es válido.
    /// </summary>
    private bool TryParseMasterQR(string qr, out List<PieceEntry> entries)
    {
        entries = new List<PieceEntry>();
        var parts = qr.Split(';');

        if (parts.Length == 0 || parts.Length % 4 != 0)
            return false;

        for (int i = 0; i < parts.Length; i += 4)
        {
            string id = parts[i].Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(id))
                return false;

            if (!float.TryParse(parts[i + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float alto))
                return false;
            if (!float.TryParse(parts[i + 2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ancho))
                return false;
            if (!float.TryParse(parts[i + 3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float fondo))
                return false;

            entries.Add(new PieceEntry
            {
                id      = id,
                alto_m  = alto,
                ancho_m = ancho,
                fondo_m = fondo
            });
        }

        return entries.Count > 0;
    }

    // ===== Holograma de escaneo =====

    private void SpawnScanHologram(ARMarker marker, PieceEntry entry)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = entry.id + "_SCAN";
        go.transform.position   = marker.transform.position;
        go.transform.rotation   = marker.transform.rotation;
        // scale: (ancho=X, alto=Y, fondo=Z) — convención Unity
        go.transform.localScale = new Vector3(entry.ancho_m, entry.alto_m, entry.fondo_m);

        var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                     ?? Shader.Find("Legacy Shaders/Diffuse");
        var mat = new Material(shader) { color = scanColor };

        var r = go.GetComponent<Renderer>();
        if (r) r.sharedMaterial = mat;

        go.GetComponent<Collider>().enabled = false;

        StartCoroutine(DestroyAfterSeconds(go, hologramLifetime));
    }

    private IEnumerator DestroyAfterSeconds(GameObject go, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (go != null) Destroy(go);
    }

    private void SaveToJson()
    {
        var wrapper = new PieceListWrapper();
        foreach (var kvp in scannedPieces)
            wrapper.piezas.Add(kvp.Value);

        // Ordenar por ID para que el JSON sea más legible
        wrapper.piezas.Sort((a, b) => string.Compare(a.id, b.id, System.StringComparison.Ordinal));

        string json = JsonUtility.ToJson(wrapper, prettyPrint: true);
        string path = Path.Combine(Application.persistentDataPath, "piezas.json");

        File.WriteAllText(path, json);

        Debug.Log($"[PieceScan] JSON guardado → {path}");
        Debug.Log($"[PieceScan] {wrapper.piezas.Count} pieza(s) registrada(s).");
        Debug.Log($"[PieceScan] Contenido:\n{json}");
    }
}
