using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;
using MixedReality.Toolkit.Speech.Windows;

#if WINDOWS_UWP
using Windows.Media.SpeechSynthesis;
#endif

/// <summary>
/// Guía por voz del flujo de cubicaje.
///   - Recibe frases con Say() y las dice una por una, en orden, sin cortarse entre sí
///   - Usa el subsistema de texto a voz de MRTK3 (WindowsTextToSpeechSubsystem)
///   - La voz suena "en la cabeza" (AudioSource 2D, no espacializado)
///   - Si no hay subsistema de voz, solo avisa en el log: el flujo sigue funcionando sin voz
///
/// Setup en Unity Editor:
///   - Crear un GameObject "VoiceGuide" en la escena y agregar este componente
///     (el AudioSource se agrega solo)
///   - Arrastrarlo al campo voiceGuide de PieceScanManager
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoiceGuide : MonoBehaviour
{
    [Header("Voz")]
    [Tooltip("Pausa en segundos entre una frase y la siguiente de la cola.")]
    public float pauseBetweenPhrases = 0.25f;

    [Tooltip("Nombre (o parte del nombre) de la voz a usar, ej. Helena, Laura o Pablo (es-ES). " +
             "Vacío = voz por defecto del sistema (en este HoloLens es David, en inglés).")]
    public string preferredVoiceName = "Helena";

    [Tooltip("Si está activo, al arrancar se listan en el log las voces instaladas en el dispositivo.")]
    public bool logInstalledVoices = true;

    private AudioSource audioSource;
    private TextToSpeechSubsystem ttsSubsystem;
    private readonly Queue<string> pendingPhrases = new Queue<string>();
    private Coroutine speakRoutine;
    private string currentPhrase;   // frase que está sonando ahora (null si ninguna)
    private bool loggedMissingSubsystem;

    /// <summary>
    /// Configura el AudioSource como sonido 2D (en la cabeza) para que la voz
    /// se oiga igual sin importar hacia dónde mire el operario.
    /// </summary>
    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;   // 0 = 2D, no espacializado
        audioSource.spatialize = false;
    }

    /// <summary>
    /// Diagnóstico: deja en el log las voces disponibles, para confirmar
    /// en el Device Portal que hay una voz en español.
    /// </summary>
    void Start()
    {
        if (logInstalledVoices)
            LogInstalledVoices();
    }

    /// <summary>
    /// Pone una frase en la cola. Se dirá cuando terminen las anteriores.
    /// Es el único método que deben llamar los otros scripts.
    /// </summary>
    public void Say(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return;

        // Evitar repeticiones acumuladas: si el operario deja un QR incorrecto a la vista,
        // la misma frase llegaría cada pocos segundos y la cola crecería sin fin.
        if (IsAlreadyQueuedOrPlaying(phrase))
        {
            Debug.Log($"[VoiceGuide] Frase repetida descartada: \"{phrase}\"");
            return;
        }

        pendingPhrases.Enqueue(phrase);

        if (speakRoutine == null)
            speakRoutine = StartCoroutine(SpeakQueue());
    }

    /// <summary>
    /// True si la frase ya está sonando o esperando en la cola.
    /// </summary>
    private bool IsAlreadyQueuedOrPlaying(string phrase)
    {
        return phrase == currentPhrase || pendingPhrases.Contains(phrase);
    }

    /// <summary>
    /// Dice las frases de la cola una por una, esperando a que cada audio
    /// termine antes de pasar a la siguiente (TrySpeak sobrescribe el clip).
    /// </summary>
    private IEnumerator SpeakQueue()
    {
        while (pendingPhrases.Count > 0)
        {
            string phrase = pendingPhrases.Dequeue();
            currentPhrase = phrase;

            if (!TryGetSubsystem())
                continue;   // sin voz: se descarta la frase, el flujo sigue

            Debug.Log($"[VoiceGuide] Diciendo: \"{phrase}\"");

            // 1) Sintetizar y arrancar el audio (asíncrono)
            Task<bool> speakTask = ttsSubsystem.TrySpeak(phrase, audioSource);
            yield return new WaitUntil(() => speakTask.IsCompleted);

            if (speakTask.IsFaulted || !speakTask.Result)
            {
                Debug.LogWarning($"[VoiceGuide] No se pudo decir: \"{phrase}\"");
                continue;
            }

            // 2) Esperar a que termine de sonar antes de la siguiente frase
            yield return new WaitWhile(() => audioSource.isPlaying);
            yield return new WaitForSeconds(pauseBetweenPhrases);
        }

        currentPhrase = null;
        speakRoutine = null;
    }

    /// <summary>
    /// Busca el subsistema de voz de MRTK la primera vez que se necesita.
    /// Se busca tarde (no en Awake) porque MRTK puede arrancarlo después.
    /// </summary>
    private bool TryGetSubsystem()
    {
        if (ttsSubsystem == null)
        {
            ttsSubsystem = XRSubsystemHelpers.GetFirstRunningSubsystem<TextToSpeechSubsystem>();
            if (ttsSubsystem != null)
                ApplyPreferredVoice();
        }

        if (ttsSubsystem == null && !loggedMissingSubsystem)
        {
            Debug.LogWarning("[VoiceGuide] No hay subsistema de texto a voz activo. La guía por voz queda desactivada.");
            loggedMissingSubsystem = true;
        }

        return ttsSubsystem != null;
    }

    /// <summary>
    /// Fija la voz en español en la configuración del subsistema de MRTK.
    /// Se hace por código (y no editando el perfil de MRTK) para tener la voz
    /// visible y cambiable desde el Inspector de este componente.
    /// </summary>
    private void ApplyPreferredVoice()
    {
        if (string.IsNullOrWhiteSpace(preferredVoiceName)) return;

        var config = XRSubsystemHelpers.GetConfiguration<WindowsTextToSpeechSubsystemConfig, WindowsTextToSpeechSubsystem>();
        if (config == null)
        {
            Debug.LogWarning("[VoiceGuide] No se encontró la configuración de voz de MRTK. Se usa la voz por defecto.");
            return;
        }

        config.Voice = TextToSpeechVoice.Other;   // "Other" = voz que no es en-US
        config.VoiceName = preferredVoiceName;
        Debug.Log($"[VoiceGuide] Voz seleccionada: '{preferredVoiceName}'.");
    }

    /// <summary>
    /// Lista las voces del sintetizador de Windows y cuál es la voz por defecto.
    /// Solo funciona en el HoloLens (UWP); en el Editor no hace nada.
    /// </summary>
    private void LogInstalledVoices()
    {
#if WINDOWS_UWP
        var defaultVoice = SpeechSynthesizer.DefaultVoice;
        Debug.Log($"[VoiceGuide] Voz por defecto: {defaultVoice.DisplayName} ({defaultVoice.Language})");

        foreach (var voice in SpeechSynthesizer.AllVoices)
            Debug.Log($"[VoiceGuide] Voz instalada: {voice.DisplayName} ({voice.Language})");
#else
        Debug.Log("[VoiceGuide] Listado de voces solo disponible en el HoloLens.");
#endif
    }
}
