using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using LexBrowser.Core.AdBlock;

namespace LexBrowser.Core;

/// <summary>
/// Motor local de lectura en voz alta bajo demanda basado en Windows SAPI (System.Speech).
/// Se instancia perezosamente al primer Speak y se libera al completar o detener la lectura (0% RAM/CPU en reposo).
/// </summary>
public static class ReadAloudService
{
    public const int MaxCharacters = 30000;

    private static readonly object _syncLock = new();
    private static SpeechSynthesizer? _activeSynthesizer;
    private static string? _ownerTabId;
    private static bool _isSpeaking;

    public static bool IsSpeaking
    {
        get
        {
            lock (_syncLock)
            {
                return _isSpeaking;
            }
        }
    }

    public static void Speak(string text, string? languageHint)
        => Speak(text, languageHint, ownerTabId: null);

    public static void Speak(string text, string? languageHint, string? ownerTabId)
    {
        string normalized = NormalizeText(text);
        if (string.IsNullOrEmpty(normalized)) return;

        var s = SettingsManager.Current;
        int rate = Math.Clamp(s?.ReadAloudRate ?? 0, -5, 5);
        string preferredVoice = s?.ReadAloudVoiceName ?? string.Empty;

        SpeechSynthesizer? synth = null;
        try
        {
            synth = new SpeechSynthesizer();
            synth.Rate = rate;

            var installed = synth.GetInstalledVoices()
                .Where(v => v.Enabled && v.VoiceInfo != null)
                .Select(v => (v.VoiceInfo.Name, v.VoiceInfo.Culture?.TwoLetterISOLanguageName ?? string.Empty))
                .ToList();

            string? chosenVoice = SelectVoiceName(installed, preferredVoice, languageHint);
            if (!string.IsNullOrEmpty(chosenVoice))
            {
                try
                {
                    synth.SelectVoice(chosenVoice);
                }
                catch (Exception voiceEx)
                {
                    ShieldLogger.Log("READ_ALOUD_VOICE_FALLBACK", "local", "", "ReadAloud", voiceEx.Message);
                }
            }

            synth.SetOutputToDefaultAudioDevice();

            var localSynth = synth;
            localSynth.SpeakCompleted += (_, args) =>
            {
                if (args.Error != null)
                {
                    ShieldLogger.Log("READ_ALOUD_ERROR", "local", "", "ReadAloud", args.Error.Message);
                }

                lock (_syncLock)
                {
                    if (ReferenceEquals(_activeSynthesizer, localSynth))
                    {
                        _activeSynthesizer = null;
                        _ownerTabId = null;
                        _isSpeaking = false;
                    }
                }
                try { localSynth.Dispose(); } catch { }
            };

            lock (_syncLock)
            {
                StopInternalUnderLock();
                _activeSynthesizer = synth;
                _ownerTabId = ownerTabId;
                _isSpeaking = true;
            }

            synth.SpeakAsync(normalized);
        }
        catch (Exception ex)
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_activeSynthesizer, synth))
                {
                    _activeSynthesizer = null;
                    _ownerTabId = null;
                    _isSpeaking = false;
                }
            }
            try { synth?.Dispose(); } catch { }
            ShieldLogger.Log("READ_ALOUD_ERROR", "local", "", "ReadAloud", ex.Message);
        }
    }

    public static void Stop()
    {
        SpeechSynthesizer? toDispose = null;
        lock (_syncLock)
        {
            toDispose = DetachSynthesizerUnderLock();
        }
        DisposeSynthesizerSafely(toDispose);
    }

    public static void StopIfOwner(string? ownerTabId)
    {
        if (string.IsNullOrEmpty(ownerTabId)) return;
        SpeechSynthesizer? toDispose = null;
        lock (_syncLock)
        {
            if (_activeSynthesizer != null && string.Equals(_ownerTabId, ownerTabId, StringComparison.Ordinal))
            {
                toDispose = DetachSynthesizerUnderLock();
            }
        }
        DisposeSynthesizerSafely(toDispose);
    }

    public static void Dispose()
    {
        Stop();
    }

    public static IReadOnlyList<(string Name, string DisplayLabel)> GetAvailableVoices()
    {
        var result = new List<(string Name, string DisplayLabel)>();
        try
        {
            using var synth = new SpeechSynthesizer();
            foreach (var voice in synth.GetInstalledVoices())
            {
                if (!voice.Enabled || voice.VoiceInfo == null) continue;
                string culture = voice.VoiceInfo.Culture?.Name ?? string.Empty;
                string label = string.IsNullOrEmpty(culture)
                    ? voice.VoiceInfo.Name
                    : $"{voice.VoiceInfo.Name} ({culture})";
                result.Add((voice.VoiceInfo.Name, label));
            }
        }
        catch (Exception ex)
        {
            ShieldLogger.Log("READ_ALOUD_LIST_VOICES_ERROR", "local", "", "ReadAloud", ex.Message);
        }
        return result;
    }

    /// <summary>
    /// Normaliza texto seleccionado: recorta, elimina caracteres de control y colapsa espacios repetidos, limitando a 30 000 caracteres.
    /// </summary>
    public static string NormalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(raw.Length, MaxCharacters));
        bool pendingSpace = false;

        foreach (char ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            if (pendingSpace)
            {
                if (sb.Length >= MaxCharacters) break;
                sb.Append(' ');
                pendingSpace = false;
            }

            if (sb.Length >= MaxCharacters) break;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Selecciona una voz por preferencia explícita del usuario o coincidencia por idioma (código ISO de 2 letras).
    /// Devuelve null para indicar voz predeterminada cuando no hay coincidencia.
    /// </summary>
    public static string? SelectVoiceName(
        IEnumerable<(string Name, string TwoLetterIsoLanguage)> installedVoices,
        string? preferredVoiceName,
        string? languageHint)
    {
        if (installedVoices == null) return null;

        var voices = installedVoices as IReadOnlyList<(string Name, string TwoLetterIsoLanguage)>
                     ?? installedVoices.ToList();
        if (voices.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredVoiceName))
        {
            string trimmedPref = preferredVoiceName.Trim();
            var exact = voices.FirstOrDefault(v => string.Equals(v.Name, trimmedPref, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(exact.Name))
                return exact.Name;
        }

        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            string normalizedLang = languageHint.Trim();
            int dash = normalizedLang.IndexOfAny(new[] { '-', '_' });
            string twoLetter = dash > 0 ? normalizedLang.Substring(0, dash) : normalizedLang;
            if (twoLetter.Length >= 2)
            {
                twoLetter = twoLetter.Substring(0, 2);
                var match = voices.FirstOrDefault(v =>
                    string.Equals(v.TwoLetterIsoLanguage, twoLetter, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Name))
                    return match.Name;
            }
        }

        return null;
    }

    private static void StopInternalUnderLock()
    {
        var prev = DetachSynthesizerUnderLock();
        if (prev != null)
        {
            try { prev.SpeakAsyncCancelAll(); } catch { }
            try { prev.Dispose(); } catch { }
        }
    }

    private static SpeechSynthesizer? DetachSynthesizerUnderLock()
    {
        var prev = _activeSynthesizer;
        _activeSynthesizer = null;
        _ownerTabId = null;
        _isSpeaking = false;
        return prev;
    }

    private static void DisposeSynthesizerSafely(SpeechSynthesizer? synth)
    {
        if (synth == null) return;
        try { synth.SpeakAsyncCancelAll(); } catch { }
        try { synth.Dispose(); } catch { }
    }
}

