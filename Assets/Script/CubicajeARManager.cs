using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Microsoft.MixedReality.OpenXR;

public class CubicajeARManager : MonoBehaviour
{
    // ===== AR / Managers =====
    [Header("AR")]
    [Tooltip("Asigna aquí el ARMarkerManager que ya tienes configurado en la escena.")]
    public ARMarkerManager markerMgr;

    // ===== Apariencia pallet =====
    [Header("Apariencia pallet")]
    public Color palletWallColor = new Color(0.58f, 0.85f, 1f, 0.35f);
    public Color palletFloorColor = new Color(0.50f, 0.80f, 1f, 0.50f);
    public Color palletBackColorIdle = new Color(0.0f, 0.5f, 0.15f, 0.75f);
    public Color palletBackStrongGreen = new Color(0.0f, 0.85f, 0.15f, 0.95f);

    // ===== Hologramas =====
    [Header("Hologramas en vuelo / error (100% alpha)")]
    public Color travelColor = new Color(0.1f, 1f, 0.1f, 1f);   // Verde opaco
    public Color errorColor = new Color(1f, 0f, 0f, 1f);       // Rojo opaco

    // ===== Geometría PALLET =====
    [Header("Pallet")]
    [Tooltip("Volumen INTERIOR útil del pallet (m), ya descontadas las paredes. 20.5 x 20.5 x 20.5 cm.")]
    public Vector3 palletInnerDims = new Vector3(0.205f, 0.205f, 0.205f);
    [Tooltip("Grosor de las paredes holográficas (m). Se suma por fuera del volumen interior.")]
    public float wallThickness = 0.005f;
    public float baseHeightNudge = 0.0f;   // 0 → apoyado justo encima del QR

    [Header("Tiempos / filtros")]
    public float coldStartIgnoreSeconds = 0.5f;  // ignorar lecturas muy al inicio

    [Tooltip("Filtro anti-spam para el MISMO texto de QR (s).")]
    public float qrCooldown = 0.40f;

    [Tooltip("Tiempo mínimo entre lecturas/procesos de CUALQUIER QR (s).")]
    public float qrGlobalCooldownSeconds = 2.0f;

    public float errorLifetimeSeconds = 1.0f;  // duración holograma rojo

    // ===== Animaciones =====
    [Header("Animación pallet")]
    public float palletPopDuration = 0.25f;

    [Header("Movimiento de piezas")]
    [Tooltip("Velocidad constante del trayecto (m/s).")]
    public float pieceTravelSpeed = 1.0f;

    [Tooltip("Desplazamiento del punto de entrada por delante del pallet (m).")]
    public float entryFrontOffset = 0.07f;

    [Tooltip("Margen interior para no tocar paredes (m).")]
    public float innerMargin = 0.008f;

    [Tooltip("Factor de caída vertical de la curva (proporcional a la distancia).")]
    public float curveDropFactor = 0.15f;

    [Tooltip("Resolución de muestreo por cada segmento de la curva.")]
    public int curveSamplesPerSegment = 30;

    [Header("Voz")]
    [Tooltip("Guía por voz. Opcional: si está vacío, el armado funciona sin voz.")]
    public VoiceGuide voiceGuide;

    /// <summary>Motores de cubicaje disponibles. Si el elegido falla, se usa el greedy.</summary>
    public enum CubicajeMotor
    {
        CSharpIntento14,   // Intento14 traducido a C#: corre en HoloLens y en el Editor
        NativoIntento14,   // CubicajeNative.dll: solo en el build de HoloLens
        Greedy,            // greedy simple en C# (CubicajeEngine)
        BinPackingCSharp   // varios pallets, uno tras otro (BinPackingSolver en C#). Va al final: Unity guarda el enum como entero
    }

    [Header("Motor de cubicaje")]
    [Tooltip("Motor a usar al escanear el pallet. Si falla (o el nativo no existe, como en el Editor), se usa el greedy.")]
    public CubicajeMotor motorCubicaje = CubicajeMotor.CSharpIntento14;

    [Tooltip("Tiempo máximo de búsqueda de Intento14 (s), en C# o nativo. La app queda congelada mientras calcula.")]
    [FormerlySerializedAs("nativeMaxTimeSeconds")]
    public int maxSolveSeconds = 2;

    [Tooltip("Solo multi-pallet: segundos tras completar un pallet en los que se ignora el QR PALLET, " +
             "para que el QR del pallet recién terminado (aún a la vista) no ancle el siguiente al instante.")]
    public float nextPalletDelaySeconds = 3f;

    [Tooltip("Holgura visual entre hologramas vecinos (m). Solo achica lo que se dibuja, no el cálculo.")]
    public float visualGap = 0.002f;

    // ===== Internos AR / escena =====
    private ARSession arSession;
    private Camera cam;

    private GameObject pallet;      // raíz del pallet hueco
    private Transform palletFloor; // piso
    private Bounds palletInnerBounds;
    private Renderer palletBackRenderer;

    private float appStartTime;

    private enum State { WaitingPallet, PalletLocked, WaitingNextPallet, Done }
    private State state = State.WaitingPallet;

    private int seqIndex = 0;

    // Control lecturas
    private string lastQR;
    private float lastQRTime = -999f;
    private float lastAnyQRProcessTime = -999f;   // << NUEVO: tiempo de último QR procesado (global)

    // Holograma en vuelo (persistente hasta el siguiente escaneo correcto)
    private GameObject currentTravelPiece;
    private Coroutine currentTravelRoutine;

    // Holograma de error temporal
    private GameObject currentErrorPiece;

    // ===== Piezas (calculadas dinámicamente por CubicajeEngine) =====

    /// <summary>Piezas colocadas por el algoritmo de cubicaje, en secuencia de ensamblado.</summary>
    private List<PlacedPiece> placedPieces = new List<PlacedPiece>();

    /// <summary>Secuencia de IDs en el orden de ensamblado calculado.</summary>
    private string[] assemblySequence = new string[0];

    /// <summary>IDs de la orden que el motor no pudo meter en el pallet (no caben).</summary>
    private List<string> unplacedPieceIds = new List<string>();

    /// <summary>Plan de cada pallet (multi-pallet). Con los motores de un pallet tiene un solo elemento.</summary>
    private List<List<PlacedPiece>> palletPlans = new List<List<PlacedPiece>>();

    /// <summary>Índice del pallet que se está armando dentro de palletPlans.</summary>
    private int currentPalletIndex = 0;

    /// <summary>Momento (Time.time) en que se completó el último pallet; para nextPalletDelaySeconds.</summary>
    private float palletCompletedTime = -999f;

    // ===== Ciclo de vida =====
    void Awake()
    {
        arSession = FindObjectOfType<ARSession>();
        cam = Camera.main;
        appStartTime = Time.realtimeSinceStartup;

        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }
        RenderSettings.skybox = null;

        if (markerMgr == null)
        {
            markerMgr = FindObjectOfType<ARMarkerManager>();
            if (markerMgr == null)
            {
                Debug.LogError("[CubicajeAR] No se encontró ningún ARMarkerManager en la escena.");
                enabled = false;
                return;
            }
        }

        state = State.WaitingPallet;
        seqIndex = 0;

        markerMgr.enabled = true;

        Debug.Log($"[CubicajeAR] Awake OK — markerMgr={(markerMgr != null ? markerMgr.name : "NULL")}");
    }

    /// <summary>
    /// Lee piezas.json desde el almacenamiento persistente, llama al motor de cubicaje
    /// y construye la secuencia de ensamblado. Retorna false si algo falla.
    /// </summary>
    private bool LoadAndSolve()
    {
        string path = Path.Combine(Application.persistentDataPath, "piezas.json");

        if (!File.Exists(path))
        {
            Debug.LogError($"[CubicajeAR] piezas.json no encontrado en: {path}");
            return false;
        }

        string json = File.ReadAllText(path);
        var wrapper = JsonUtility.FromJson<PieceListWrapper>(json);

        if (wrapper == null || wrapper.piezas == null || wrapper.piezas.Count == 0)
        {
            Debug.LogError("[CubicajeAR] piezas.json está vacío o mal formado.");
            return false;
        }

        palletPlans = SolveAllPallets(wrapper.piezas, ComputeInnerBounds());
        ComputeUnplaced(wrapper.piezas);
        LoadPalletPlan(0);
        return true;
    }

    /// <summary>
    /// Calcula el plan de cada pallet. Con BinPackingCSharp reparte la orden en varios pallets;
    /// con los demás motores (o si el bin packing falla) devuelve un solo pallet.
    /// </summary>
    private List<List<PlacedPiece>> SolveAllPallets(List<PieceEntry> pieces, Bounds palletInner)
    {
        if (motorCubicaje == CubicajeMotor.BinPackingCSharp)
        {
            var plans = CubicajeCSharpEngine.SolveBinPacking(pieces, palletInner, maxSolveSeconds);
            if (plans != null && plans.Count > 0)
            {
                Debug.Log($"[CubicajeAR] Motor usado: bin packing en C# ({plans.Count} pallets).");
                return plans;
            }
            Debug.LogWarning("[CubicajeAR] El bin packing falló. Se arma un solo pallet con el greedy.");
        }

        return new List<List<PlacedPiece>> { SolvePlacement(pieces, palletInner) };
    }

    /// <summary>
    /// Llena unplacedPieceIds con las piezas de la orden que no quedaron en ningún pallet
    /// (no caben) y lo deja en el log.
    /// </summary>
    private void ComputeUnplaced(List<PieceEntry> pieces)
    {
        unplacedPieceIds.Clear();
        var placedIds = new HashSet<string>();
        foreach (var plan in palletPlans)
            foreach (var placed in plan)
                placedIds.Add(placed.id);

        foreach (var piece in pieces)
        {
            if (!placedIds.Contains(piece.id))
                unplacedPieceIds.Add(piece.id);
        }

        if (unplacedPieceIds.Count > 0)
            Debug.LogWarning($"[CubicajeAR] Armado parcial: no todas las piezas caben en el pallet. " +
                             $"Colocadas={placedIds.Count}/{pieces.Count}. " +
                             $"Fuera del pallet: {string.Join(", ", unplacedPieceIds)}. " +
                             "La secuencia continuará solo con las piezas colocadas.");
    }

    /// <summary>
    /// Activa el plan del pallet indicado: sus piezas pasan a ser la secuencia actual
    /// y el armado arranca desde el primer paso.
    /// </summary>
    private void LoadPalletPlan(int index)
    {
        currentPalletIndex = index;
        placedPieces = palletPlans[index];
        seqIndex = 0;

        assemblySequence = new string[placedPieces.Count];
        for (int i = 0; i < placedPieces.Count; i++)
            assemblySequence[i] = placedPieces[i].id;

        Debug.Log($"[CubicajeAR] Pallet {index + 1}/{palletPlans.Count}: " +
                  $"secuencia calculada ({assemblySequence.Length} piezas): " +
                  string.Join(" → ", assemblySequence));
    }

    /// <summary>True si queda al menos un pallet por armar después del actual.</summary>
    private bool HasNextPallet()
    {
        return currentPalletIndex + 1 < palletPlans.Count;
    }

    /// <summary>
    /// Índice del pallet al que pertenece una pieza, o -1 si no está en ningún plan.
    /// </summary>
    private int FindPalletOfPiece(string id)
    {
        for (int p = 0; p < palletPlans.Count; p++)
            foreach (var placed in palletPlans[p])
                if (placed.id == id) return p;
        return -1;
    }

    /// <summary>
    /// Resuelve el cubicaje con el motor elegido en el Inspector (motorCubicaje).
    /// Si ese motor falla o no está disponible, usa el greedy para no quedar nunca sin secuencia.
    /// </summary>
    private List<PlacedPiece> SolvePlacement(List<PieceEntry> pieces, Bounds palletInner)
    {
        List<PlacedPiece> result = TrySelectedEngine(pieces, palletInner);
        if (result != null)
            return result;

        Debug.Log("[CubicajeAR] Motor usado: greedy en C#.");
        return CubicajeEngine.Solve(pieces, palletInner, innerMargin);
    }

    /// <summary>
    /// Llama al motor Intento14 elegido (C# o nativo). Devuelve null si se eligió el greedy,
    /// si el motor falló o si no está disponible en esta plataforma.
    /// </summary>
    private List<PlacedPiece> TrySelectedEngine(List<PieceEntry> pieces, Bounds palletInner)
    {
        switch (motorCubicaje)
        {
            case CubicajeMotor.CSharpIntento14:
            {
                var csharpResult = CubicajeCSharpEngine.Solve(pieces, palletInner, maxSolveSeconds);
                if (csharpResult != null)
                {
                    Debug.Log("[CubicajeAR] Motor usado: C# (Intento14).");
                    return csharpResult;
                }
                Debug.LogWarning("[CubicajeAR] El motor C# falló. Se usa el greedy en C# como respaldo.");
                return null;
            }

            case CubicajeMotor.NativoIntento14:
            {
                if (!CubicajeNativeEngine.IsAvailable)
                {
                    Debug.LogWarning("[CubicajeAR] El motor nativo solo existe en el build de HoloLens. Se usa el greedy.");
                    return null;
                }
                var nativeResult = CubicajeNativeEngine.Solve(pieces, palletInner, maxSolveSeconds);
                if (nativeResult != null)
                {
                    Debug.Log("[CubicajeAR] Motor usado: nativo (Intento14).");
                    return nativeResult;
                }
                Debug.LogWarning("[CubicajeAR] El motor nativo falló. Se usa el greedy en C# como respaldo.");
                return null;
            }

            default:
                return null;   // Greedy elegido explícitamente
        }
    }

    /// <summary>
    /// Dimensiones exteriores del pallet holográfico: el volumen interior más las paredes.
    /// Paredes en los lados (X, ambos), piso (Y, abajo) y pared trasera (Z, atrás); el frente va abierto.
    /// </summary>
    private Vector3 PalletOuterDims()
    {
        return new Vector3(
            palletInnerDims.x + 2f * wallThickness,
            palletInnerDims.y + wallThickness,
            palletInnerDims.z + wallThickness);
    }

    /// <summary>
    /// Volumen interior del pallet en su espacio local. No está centrado en 0: el piso
    /// lo empuja medio grosor hacia arriba y la pared trasera medio grosor hacia el frente (-Z).
    /// Lo usan los motores de cubicaje y la trayectoria de entrada de las piezas.
    /// </summary>
    private Bounds ComputeInnerBounds()
    {
        var center = new Vector3(0f, wallThickness * 0.5f, -wallThickness * 0.5f);
        return new Bounds(center, palletInnerDims);
    }

    void OnEnable()
    {
        Debug.Log($"[CubicajeAR] OnEnable — markerMgr={(markerMgr != null ? "OK" : "NULL → NO SE SUSCRIBE")}");
        if (markerMgr != null)
            markerMgr.markersChanged += OnMarkersChanged;
    }

    void OnDisable()
    {
        if (markerMgr != null)
            markerMgr.markersChanged -= OnMarkersChanged;
    }

    // ===== Eventos de marcadores =====
    private void OnMarkersChanged(ARMarkersChangedEventArgs e)
    {
        if (state == State.Done)
            return;

        foreach (var m in e.added) TryProcessMarker(m);
        foreach (var m in e.updated) TryProcessMarker(m);
    }

    private void TryProcessMarker(ARMarker m)
    {
        if (m == null) return;

        var _decoded = markerMgr.GetDecodedString(m.trackableId);
        Debug.Log($"[CubicajeAR] TryProcessMarker — qr='{_decoded}' tracking={m.trackingState} state={state}");

        if (Time.realtimeSinceStartup < appStartTime + coldStartIgnoreSeconds) return;
        if (m.trackingState != TrackingState.Tracking) return;

        var decoded = markerMgr.GetDecodedString(m.trackableId);
        if (string.IsNullOrEmpty(decoded)) return;

        string qr = decoded.Trim().ToUpperInvariant();
        float now = Time.time;

        // --- 1) Cooldown GLOBAL: no procesar ningún QR hasta que pase el tiempo configurado ---
        if (now - lastAnyQRProcessTime < qrGlobalCooldownSeconds)
        {
            return;
        }

        // --- 2) Cooldown por MISMO TEXTO para evitar duplicados pegados ---
        if (qr == lastQR && (now - lastQRTime) < qrCooldown)
        {
            return;
        }

        // A partir de aquí, se acepta este QR como "procesado"
        lastQR = qr;
        lastQRTime = now;
        lastAnyQRProcessTime = now;

        // Esperando pallet
        if (state == State.WaitingPallet)
        {
            if (qr == "PALLET" || qr == "PALLET.")
                ProcessPalletMarker(m);
            return;
        }

        // Multi-pallet: esperando el pallet siguiente
        if (state == State.WaitingNextPallet)
        {
            if (qr == "PALLET" || qr == "PALLET.")
                TryProcessNextPallet(m);
            return;
        }

        // Después de fijar el pallet
        if (state != State.PalletLocked) return;

        if (qr == "PALLET" || qr == "PALLET.")
            return; // ignorar pallet una vez fijado

        if (!IsKnownPieceId(qr))
        {
            // Multi-pallet: la pieza pertenece a otro pallet de la orden
            if (HandlePieceOfOtherPallet(qr))
                return;

            // Pieza de la orden que no cupo: avisar por voz para que el operario la aparte
            if (unplacedPieceIds.Contains(qr))
            {
                Debug.Log($"[CubicajeAR] QR '{qr}' ignorado: la pieza no cabe en el pallet.");
                Speak(BuildUnplacedPhrase(new List<string> { qr }));
                return;
            }

            // QR ajeno a la orden
            Debug.Log($"[CubicajeAR] QR '{qr}' ignorado: no está en la secuencia de armado.");
            return;
        }

        if (seqIndex >= assemblySequence.Length) return;

        if (IsAlreadyPlaced(qr))
        {
            // El QR de una pieza recién colocada suele seguir a la vista y se relee
            // al pasar el cooldown: no es un error del operario, se ignora en silencio.
            Debug.Log($"[CubicajeAR] QR '{qr}' ignorado: pieza ya colocada.");
            return;
        }

        string expectedId = assemblySequence[seqIndex];

        if (qr != expectedId)
        {
            // incorrecta → holograma rojo
            Debug.LogWarning($"[CubicajeAR] Pieza INCORRECTA: escaneada={qr}, esperada={expectedId} {StepLabel()}");
            Speak($"Pieza incorrecta. La siguiente es {expectedId}");
            var wrongDef = FindPlacedById(qr);
            SpawnErrorAtMarker(m, wrongDef);
            return;
        }

        // correcta → holograma verde que viaja curvado hacia el pallet
        Debug.Log($"[CubicajeAR] Pieza CORRECTA: {qr} {StepLabel()} — viajando a su slot.");
        var def = FindPlacedById(expectedId);
        HandleCorrectPiece(m, def);
    }

    // ===== Pallet =====
    /// <summary>
    /// Primer escaneo del QR PALLET: calcula el plan de todos los pallets, ancla el pallet 1
    /// y anuncia por voz el inicio del armado.
    /// </summary>
    private void ProcessPalletMarker(ARMarker m)
    {
        if (pallet != null) return;

        // Leer piezas.json y calcular la secuencia de ensamblado en este momento,
        // cuando ya se escanearon todas las piezas en la misma sesión.
        if (!LoadAndSolve()) return;

        SpawnPallet(m);

        // Guía por voz: confirmar pallet y arrancar diciendo la primera pieza,
        // para que el operario no tenga que adivinar por prueba y error.
        Speak("Pallet escaneado");
        if (palletPlans.Count > 1)
            Speak($"La orden usa {palletPlans.Count} pallets");
        if (unplacedPieceIds.Count > 0)
            Speak(BuildUnplacedPhrase(unplacedPieceIds));
        if (assemblySequence.Length > 0)
            Speak($"Primera pieza: {assemblySequence[0]}");

        state = State.PalletLocked;
    }

    /// <summary>
    /// Multi-pallet: escaneo de PALLET después de completar un pallet. Ignora el QR durante
    /// nextPalletDelaySeconds (el QR del pallet terminado suele seguir a la vista); luego
    /// reemplaza el pallet holográfico por el siguiente y anuncia su primera pieza.
    /// </summary>
    private void TryProcessNextPallet(ARMarker m)
    {
        if (Time.time - palletCompletedTime < nextPalletDelaySeconds)
        {
            Debug.Log("[CubicajeAR] QR PALLET ignorado: aún no pasa el retardo para el pallet siguiente.");
            return;
        }

        ClearCurrentPallet();
        LoadPalletPlan(currentPalletIndex + 1);
        SpawnPallet(m);

        int palletNumber = currentPalletIndex + 1;
        Debug.Log($"[CubicajeAR] Pallet {palletNumber}/{palletPlans.Count} anclado.");
        Speak($"Pallet {palletNumber} escaneado");
        if (assemblySequence.Length > 0)
            Speak($"Primera pieza: {assemblySequence[0]}");

        state = State.PalletLocked;
    }

    /// <summary>
    /// Borra el pallet holográfico terminado y el último holograma de pieza,
    /// para dejar la escena lista para el pallet siguiente.
    /// </summary>
    private void ClearCurrentPallet()
    {
        if (currentTravelRoutine != null)
        {
            StopCoroutine(currentTravelRoutine);
            currentTravelRoutine = null;
        }
        if (currentTravelPiece != null)
        {
            Destroy(currentTravelPiece);
            currentTravelPiece = null;
        }
        if (pallet != null)
        {
            Destroy(pallet);
            pallet = null;
        }
    }

    /// <summary>
    /// Crea el pallet hueco sobre el QR escaneado, con el frente hacia la cámara,
    /// lo ancla en el mundo (ARAnchor) y lanza la animación de entrada.
    /// </summary>
    private void SpawnPallet(ARMarker m)
    {
        var qrPos = m.transform.position;
        var qrYaw = Quaternion.Euler(0f, m.transform.rotation.eulerAngles.y, 0f);

        var palletOuterDims = PalletOuterDims();

        pallet = CreateHollowPallet("PALLET", palletOuterDims, wallThickness,
                                    palletWallColor, palletFloorColor,
                                    palletBackColorIdle,
                                    out palletFloor, out palletInnerBounds, out palletBackRenderer);

        var centerAboveQR = qrPos + Vector3.up * (palletOuterDims.y * 0.5f + baseHeightNudge);
        pallet.transform.SetPositionAndRotation(centerAboveQR, qrYaw);

        // frente del pallet hacia la cámara
        if (cam != null)
        {
            Vector3 toCam = (cam.transform.position - pallet.transform.position);
            toCam.y = 0f;
            if (toCam.sqrMagnitude > 0.0001f)
            {
                var faceCam = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                faceCam *= Quaternion.Euler(0f, 180f, 0f);
                pallet.transform.rotation = faceCam;
            }
        }

        if (pallet.GetComponent<ARAnchor>() == null)
            pallet.AddComponent<ARAnchor>();

        StartCoroutine(AnimatePalletIn());
    }

    private GameObject CreateHollowPallet(
        string name, Vector3 outer, float t,
        Color wallCol, Color floorCol, Color backCol,
        out Transform floor, out Bounds inner, out Renderer backRenderer)
    {
        GameObject root = new GameObject(name);

        var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                     ?? Shader.Find("Legacy Shaders/Diffuse");

        var matWall = new Material(shader) { color = wallCol };
        var matFloor = new Material(shader) { color = floorCol };
        var matBack = new Material(shader) { color = backCol };

        // Piso
        var baseGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseGO.name = "Floor";
        baseGO.transform.SetParent(root.transform, false);
        baseGO.transform.localScale = new Vector3(outer.x, t, outer.z);
        baseGO.transform.localPosition = new Vector3(0, -outer.y * 0.5f + t * 0.5f, 0);
        baseGO.GetComponent<Renderer>().sharedMaterial = matFloor;
        baseGO.GetComponent<Collider>().enabled = false;

        // Cara trasera
        var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
        back.name = "Back";
        back.transform.SetParent(root.transform, false);
        back.transform.localScale = new Vector3(outer.x, outer.y, t);
        back.transform.localPosition = new Vector3(0, 0, outer.z * 0.5f - t * 0.5f);
        var backR = back.GetComponent<Renderer>();
        backR.sharedMaterial = matBack;
        back.GetComponent<Collider>().enabled = false;

        // Lados
        var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
        left.name = "Left";
        left.transform.SetParent(root.transform, false);
        left.transform.localScale = new Vector3(t, outer.y, outer.z);
        left.transform.localPosition = new Vector3(-outer.x * 0.5f + t * 0.5f, 0, 0);
        left.GetComponent<Renderer>().sharedMaterial = matWall;
        left.GetComponent<Collider>().enabled = false;

        var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
        right.name = "Right";
        right.transform.SetParent(root.transform, false);
        right.transform.localScale = new Vector3(t, outer.y, outer.z);
        right.transform.localPosition = new Vector3(outer.x * 0.5f - t * 0.5f, 0, 0);
        right.GetComponent<Renderer>().sharedMaterial = matWall;
        right.GetComponent<Collider>().enabled = false;

        floor = baseGO.transform;
        inner = ComputeInnerBounds();
        backRenderer = backR;
        return root;
    }

    private IEnumerator AnimatePalletIn()
    {
        if (pallet == null) yield break;

        float duration = palletPopDuration;
        float t = 0f;
        Vector3 start = Vector3.zero;
        Vector3 end = Vector3.one;

        pallet.transform.localScale = start;

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / duration);
            u = u * u * (3f - 2f * u);
            pallet.transform.localScale = Vector3.Lerp(start, end, u);
            yield return null;
        }

        pallet.transform.localScale = end;
    }

    // ===== Piezas =====

    /// <summary>
    /// Busca una pieza en la lista calculada por el cubicaje.
    /// Si no se encuentra (pieza desconocida), retorna un PlacedPiece con escala mínima.
    /// </summary>
    private PlacedPiece FindPlacedById(string id)
    {
        foreach (var p in placedPieces)
            if (p.id == id) return p;

        // Pieza no encontrada en el plan: usar tamaño genérico para mostrar error
        return new PlacedPiece
        {
            id         = id,
            localPos   = Vector3.zero,
            localEuler = Vector3.zero,
            scale      = new Vector3(0.1f, 0.1f, 0.1f)
        };
    }

    /// <summary>
    /// Pide a la guía de voz que diga una frase. Si no hay VoiceGuide asignado,
    /// no hace nada: el armado sigue funcionando sin voz.
    /// </summary>
    private void Speak(string phrase)
    {
        if (voiceGuide != null)
            voiceGuide.Say(phrase);
    }

    /// <summary>
    /// True si la pieza ya fue colocada, es decir, está antes del paso actual en la secuencia.
    /// </summary>
    private bool IsAlreadyPlaced(string id)
    {
        int index = System.Array.IndexOf(assemblySequence, id);
        return index >= 0 && index < seqIndex;
    }

    /// <summary>
    /// Arma la frase de voz para piezas que no caben en el pallet.
    /// Ej.: "La pieza A6 no cabe en el pallet" / "Las piezas A6 y B2 no caben en el pallet".
    /// </summary>
    private string BuildUnplacedPhrase(List<string> ids)
    {
        if (ids.Count == 1)
            return $"La pieza {ids[0]} no cabe en el pallet";

        string allButLast = string.Join(", ", ids.GetRange(0, ids.Count - 1));
        return $"Las piezas {allButLast} y {ids[ids.Count - 1]} no caben en el pallet";
    }

    /// <summary>
    /// Devuelve el paso actual de la secuencia como texto, ej. "(paso 3/11)".
    /// Se usa solo en los logs para ubicar cada escaneo dentro del armado.
    /// </summary>
    private string StepLabel()
    {
        return $"(paso {seqIndex + 1}/{assemblySequence.Length})";
    }

    /// <summary>
    /// Multi-pallet: si la pieza pertenece a otro pallet de la orden, lo maneja y devuelve true.
    /// Pallet posterior → aviso por voz de a qué pallet va. Pallet anterior → ya colocada (silencio).
    /// </summary>
    private bool HandlePieceOfOtherPallet(string id)
    {
        int palletIndex = FindPalletOfPiece(id);
        if (palletIndex < 0 || palletIndex == currentPalletIndex)
            return false;

        if (palletIndex < currentPalletIndex)
        {
            Debug.Log($"[CubicajeAR] QR '{id}' ignorado: pieza ya colocada en el pallet {palletIndex + 1}.");
            return true;
        }

        Debug.Log($"[CubicajeAR] QR '{id}' ignorado: la pieza va en el pallet {palletIndex + 1} " +
                  $"(se está armando el pallet {currentPalletIndex + 1}).");
        Speak($"La pieza {id} va en el pallet {palletIndex + 1}");
        return true;
    }

    private bool IsKnownPieceId(string id)
    {
        foreach (var p in placedPieces)
            if (p.id == id) return true;

        return false;
    }

    private void HandleCorrectPiece(ARMarker marker, PlacedPiece def)
    {
        // Eliminar holograma de viaje anterior si existía
        if (currentTravelRoutine != null)
        {
            StopCoroutine(currentTravelRoutine);
            currentTravelRoutine = null;
        }
        if (currentTravelPiece != null)
        {
            Destroy(currentTravelPiece);
            currentTravelPiece = null;
        }

        // Posición de inicio: donde está el QR físico
        Vector3 startPos = marker.transform.position;
        Quaternion startRot = marker.transform.rotation;

        // Posición de destino: slot calculado por el cubicaje, en espacio mundo
        Vector3 endPos = pallet.transform.TransformPoint(def.localPos);
        Quaternion endRot = pallet.transform.rotation * Quaternion.Euler(def.localEuler);

        currentTravelPiece = CreateTravelPiece(def, startPos, startRot);

        var path = BuildCurvedEntryPath(startPos, endPos, def);

        currentTravelRoutine = StartCoroutine(MoveAlongPath(
            currentTravelPiece,
            path,
            pieceTravelSpeed,
            endRot,
            () =>
            {
                seqIndex++;
                Debug.Log($"[CubicajeAR] Pieza {def.id} colocada en su slot ({seqIndex}/{assemblySequence.Length}).");
                if (seqIndex >= assemblySequence.Length)
                {
                    if (HasNextPallet())
                        OnPalletCompletedWithMoreLeft();
                    else
                        OnOrderCompleted();
                }
                else
                {
                    // si aún faltan piezas, el holograma se queda en su slot
                    // y se anuncia la siguiente (justo cuando se acepta su escaneo)
                    Speak($"Siguiente pieza: {assemblySequence[seqIndex]}");
                }
            }
        ));
    }

    /// <summary>
    /// Multi-pallet: terminó un pallet y quedan más. Pared trasera en verde, se pide el pallet
    /// siguiente por voz y se espera su QR (el escaneo sigue activo).
    /// </summary>
    private void OnPalletCompletedWithMoreLeft()
    {
        int done = currentPalletIndex + 1;
        Debug.Log($"[CubicajeAR] PALLET {done}/{palletPlans.Count} COMPLETADO: {assemblySequence.Length} piezas. " +
                  "Esperando el QR del pallet siguiente.");
        Speak($"Pallet {done} completo. Coloque el pallet {done + 1} y escanéelo");
        StartCoroutine(PalletCompletedFeedback());
        palletCompletedTime = Time.time;
        state = State.WaitingNextPallet;
    }

    /// <summary>
    /// Último pallet terminado: fin de la orden. Pared trasera en verde y escaneo de QR desactivado.
    /// </summary>
    private void OnOrderCompleted()
    {
        Debug.Log($"[CubicajeAR] SECUENCIA COMPLETADA: {assemblySequence.Length} piezas colocadas " +
                  $"(pallet {currentPalletIndex + 1}/{palletPlans.Count}). Escaneo de QR desactivado.");
        Speak(palletPlans.Count > 1 ? "Pallet completo. Orden completa" : "Pallet completo");
        state = State.Done;
        StartCoroutine(PalletCompletedFeedback());
        markerMgr.enabled = false;
    }

    private GameObject CreateTravelPiece(PlacedPiece d, Vector3 worldPos, Quaternion worldRot)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = d.id + "_TRAVEL";
        go.transform.position   = worldPos;
        go.transform.rotation   = worldRot;
        go.transform.localScale = VisualScale(d.scale);

        var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                     ?? Shader.Find("Legacy Shaders/Diffuse");
        var mat = new Material(shader) { color = travelColor };

        var r = go.GetComponent<Renderer>();
        if (r) r.sharedMaterial = mat;
        go.GetComponent<Collider>().enabled = false;

        return go;
    }

    /// <summary>
    /// Tamaño con el que se dibuja una pieza: su tamaño real menos visualGap, para que
    /// dos hologramas verdes pegados (el motor nativo no deja margen) se vean separados.
    /// </summary>
    private Vector3 VisualScale(Vector3 realScale)
    {
        return new Vector3(
            Mathf.Max(realScale.x - visualGap, 0.001f),
            Mathf.Max(realScale.y - visualGap, 0.001f),
            Mathf.Max(realScale.z - visualGap, 0.001f));
    }

    private void SpawnErrorAtMarker(ARMarker marker, PlacedPiece def)
    {
        if (currentErrorPiece != null)
        {
            Destroy(currentErrorPiece);
            currentErrorPiece = null;
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = def.id + "_ERROR";
        go.transform.position   = marker.transform.position;
        go.transform.rotation   = marker.transform.rotation;
        go.transform.localScale = def.scale;

        var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                     ?? Shader.Find("Legacy Shaders/Diffuse");
        var mat = new Material(shader) { color = errorColor };

        var r = go.GetComponent<Renderer>();
        if (r) r.sharedMaterial = mat;
        go.GetComponent<Collider>().enabled = false;

        currentErrorPiece = go;

        StartCoroutine(DestroyAfterSeconds(go, errorLifetimeSeconds));
    }

    private IEnumerator DestroyAfterSeconds(GameObject go, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (go != null) Destroy(go);
        if (go == currentErrorPiece) currentErrorPiece = null;
    }

    private IEnumerator PalletCompletedFeedback()
    {
        if (palletBackRenderer == null) yield break;

        var mat = palletBackRenderer.material;
        Color ini = mat.color;
        Color fin = palletBackStrongGreen;
        float t = 0f;
        float dur = 0.35f;

        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            mat.color = Color.Lerp(ini, fin, u);
            yield return null;
        }

        mat.color = fin;
    }

    // ===== Trayectoria curvada hacia abajo y entrada frontal =====
    private List<Vector3> BuildCurvedEntryPath(Vector3 startPos, Vector3 endPos, PlacedPiece def)
    {
        var points = new List<Vector3>();

        // Punto de entrada por delante del pallet (z negativo en local, frontal abierto)
        float frontZ = palletInnerBounds.min.z - entryFrontOffset;

        // Mantener altura parecida a la del destino pero dentro de los márgenes
        float yLocal = Mathf.Clamp(def.localPos.y,
            palletInnerBounds.min.y + innerMargin,
            palletInnerBounds.max.y - innerMargin);

        float xLocal = Mathf.Clamp(def.localPos.x,
            palletInnerBounds.min.x + innerMargin,
            palletInnerBounds.max.x - innerMargin);

        Vector3 entryLocal = new Vector3(xLocal, yLocal, frontZ);
        Vector3 entryWorld = pallet.transform.TransformPoint(entryLocal);

        // Primer segmento: start → entry, curvado hacia abajo
        float d1 = Vector3.Distance(startPos, entryWorld);
        Vector3 mid1 = (startPos + entryWorld) * 0.5f;
        Vector3 ctrl1 = mid1 + Vector3.down * (d1 * curveDropFactor);

        var seg1 = SampleQuadraticBezier(startPos, ctrl1, entryWorld, curveSamplesPerSegment);

        // Segundo segmento: entry → end, también curvado hacia abajo
        float d2 = Vector3.Distance(entryWorld, endPos);
        Vector3 mid2 = (entryWorld + endPos) * 0.5f;
        Vector3 ctrl2 = mid2 + Vector3.down * (d2 * curveDropFactor * 0.7f);

        var seg2 = SampleQuadraticBezier(entryWorld, ctrl2, endPos, curveSamplesPerSegment);

        points.AddRange(seg1);
        for (int i = 1; i < seg2.Count; i++) points.Add(seg2[i]);

        return points;
    }

    private List<Vector3> SampleQuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, int samples)
    {
        var list = new List<Vector3>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float u = 1f - t;
            Vector3 p = (u * u) * p0 + 2f * u * t * p1 + (t * t) * p2;
            list.Add(p);
        }
        return list;
    }

    private IEnumerator MoveAlongPath(
        GameObject go,
        List<Vector3> path,
        float speed,
        Quaternion endRot,
        Action onArrive)
    {
        if (go == null || path == null || path.Count < 2) yield break;

        float totalLen = 0f;
        for (int i = 1; i < path.Count; i++)
            totalLen += Vector3.Distance(path[i - 1], path[i]);

        if (totalLen < 0.001f)
        {
            go.transform.position = path[path.Count - 1];
            go.transform.rotation = endRot;
            onArrive?.Invoke();
            yield break;
        }

        float traveled = 0f;

        while (traveled < totalLen && go != null)
        {
            float step = speed * Time.deltaTime;
            traveled += step;
            float d = Mathf.Clamp(traveled, 0f, totalLen);

            // Buscar el segmento donde cae la distancia d
            float acc = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                float dl = Vector3.Distance(path[i - 1], path[i]);
                if (acc + dl >= d)
                {
                    float t = Mathf.Clamp01((d - acc) / dl);
                    Vector3 pos = Vector3.Lerp(path[i - 1], path[i], t);
                    go.transform.position = pos;

                    float u = d / totalLen;
                    go.transform.rotation = Quaternion.Slerp(go.transform.rotation, endRot, Mathf.Clamp01(u));

                    break;
                }
                acc += dl;
            }

            yield return null;
        }

        if (go != null)
        {
            go.transform.position = path[path.Count - 1];
            go.transform.rotation = endRot;
        }

        onArrive?.Invoke();
        currentTravelRoutine = null;
    }
}
