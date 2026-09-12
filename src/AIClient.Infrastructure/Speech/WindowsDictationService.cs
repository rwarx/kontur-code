using System.Globalization;
using System.Speech.Recognition;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Speech;

/// <summary>
/// Dictation through the speech recognizer Windows already ships, via System.Speech.
/// </summary>
/// <remarks>
/// <para>
/// Chosen before the alternatives for what it does not cost: no cloud round-trip - the words
/// never leave the machine, which is the right default for a tool that hears everything typed
/// into a code editor - no model to download, and no native payload beyond the interop
/// assembly. The recognizer's language is the one Windows installed for speech, picked below
/// to match the interface language when that is possible.
/// </para>
/// <para>
/// The engine is created per session rather than kept alive between them. A held recognizer
/// keeps the microphone open, which reads to every other program - and to the person in front
/// of the screen, whose hardware shows a recording light - as if the application were
/// listening all the time. Creating one takes a fraction of a second; the trust it buys back
/// is worth more than the delay.
/// </para>
/// <para>
/// System.Speech raises its events on a thread of its own. This class does not marshal them,
/// by design: the interface contract says events may arrive on any thread, and the layer that
/// owns the state being updated is the layer that knows how to reach it.
/// </para>
/// </remarks>
public sealed class WindowsDictationService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WindowsDictationService> _logger;

    /// <summary>Serializes open and close, which arrive from UI clicks and can interleave.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SpeechRecognitionEngine? _engine;
    private bool _listening;
    private bool _disposed;

    public WindowsDictationService(ILogger<WindowsDictationService> logger)
    {
        _logger = logger;

        // Asked once: recognizers appear with Windows features and disappear the same way, so
        // the answer cannot usefully change while the process is running. Guarded because this
        // runs at composition time - a machine whose speech stack is broken in some interesting
        // way must cost the microphone button, not the application.
        bool available;
        int recognizerCount = 0;
        string defaultRecognizerName = "none";

        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            recognizerCount = recognizers.Count;
            available = recognizerCount > 0;

            if (available)
            {
                defaultRecognizerName = PickRecognizer(recognizers).Culture.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Asking for installed speech recognizers failed; dictation is unavailable.");
            available = false;
        }

        IsAvailable = available;

        if (IsAvailable)
        {
            _logger.LogInformation(
                "Speech recognition available: {Count} recognizer(s), default {Default}.",
                recognizerCount,
                defaultRecognizerName);
        }
        else
        {
            // Not an error: a Windows install without the desktop speech feature is unusual
            // but legitimate, and the interface hides the microphone when the answer is no.
            _logger.LogInformation("No speech recognizer installed; dictation is unavailable.");
        }
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public bool IsListening => _listening;

    /// <inheritdoc />
    public event EventHandler? IsListeningChanged;

    /// <inheritdoc />
    public event EventHandler<SpeechTranscriptEventArgs>? PartialTranscript;

    /// <inheritdoc />
    public event EventHandler<SpeechTranscriptEventArgs>? FinalTranscript;

    /// <inheritdoc />
    public event EventHandler<SpeechErrorEventArgs>? Failed;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_listening)
            {
                return;
            }

            // The engine's constructor loads a COM component and picks audio endpoints, which
            // is not work the composer button should wait on the UI thread for.
            var engine = await Task.Run(() => CreateEngine(), cancellationToken).ConfigureAwait(false);

            if (engine is null)
            {
                Failed?.Invoke(this, new SpeechErrorEventArgs
                {
                    Message = "No speech recognizer is installed on this system.",
                });
                return;
            }

            _engine = engine;
            engine.RecognizeAsync(RecognizeMode.Multiple);

            _listening = true;
            IsListeningChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting dictation failed.");
            Failed?.Invoke(this, new SpeechErrorEventArgs { Message = ex.Message });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!_listening)
            {
                return;
            }

            var engine = _engine;
            _engine = null;

            // The state flips and is announced before the engine winds down, so the recording
            // mark leaves the screen the moment the button is pressed rather than when the
            // microphone has finished letting go.
            _listening = false;
            IsListeningChanged?.Invoke(this, EventArgs.Empty);

            if (engine is null)
            {
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    // Cancel rather than a graceful stop: the guess still open belongs to the
                    // composer above, which keeps what it was already showing, and a graceful
                    // stop would hold the session while the engine listens for a settled
                    // ending the user has already decided not to wait for.
                    engine.RecognizeAsyncCancel();
                    engine.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Stopping dictation misbehaved; the session is closed either way.");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _engine, null)?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Builds the engine for one session: recognizer, default microphone, open-ended dictation.
    /// </summary>
    /// <returns>Null when there is nothing to build with, which <see cref="StartAsync"/> reports.</returns>
    private SpeechRecognitionEngine? CreateEngine()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();

        if (recognizers.Count == 0)
        {
            return null;
        }

        var recognizer = PickRecognizer(recognizers);
        _logger.LogInformation("Dictation starting with {Recognizer}.", recognizer.Culture.Name);

        var engine = new SpeechRecognitionEngine(recognizer);

        try
        {
            engine.SetInputToDefaultAudioDevice();
            engine.LoadGrammar(new DictationGrammar());

            engine.SpeechHypothesized += OnHypothesized;
            engine.SpeechRecognized += OnRecognized;
            engine.RecognizeCompleted += OnRecognizeCompleted;
            engine.AudioSignalProblemOccurred += OnAudioSignalProblemOccurred;
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return engine;
    }

    /// <summary>
    /// The recognizer whose language matches the interface, falling outward until something fits.
    /// </summary>
    /// <remarks>
    /// Dictation follows the language the user reads the application in when Windows can speak
    /// it, and falls back to the machine's own default when it cannot - a German interface on an
    /// English-only Windows dictating English is worse than a German interface dictating German,
    /// but both beat refusing.
    /// </remarks>
    private static RecognizerInfo PickRecognizer(IReadOnlyList<RecognizerInfo> recognizers)
    {
        var ui = CultureInfo.CurrentUICulture;

        return recognizers.FirstOrDefault(r => r.Culture.Name.Equals(ui.Name, StringComparison.OrdinalIgnoreCase))
            ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName
                .Equals(ui.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? recognizers.FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? recognizers[0];
    }

    private void OnHypothesized(object? sender, SpeechHypothesizedEventArgs e) =>
        PartialTranscript?.Invoke(this, new SpeechTranscriptEventArgs { Text = e.Result.Text ?? string.Empty });

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        // Continuous dictation closes every silent stretch with a result, and much of what it
        // closes with is breath and keyboard: an empty or whitespace-only result is a segment
        // boundary, not words, and forwarding it would make the composer think a segment settled.
        if (string.IsNullOrWhiteSpace(e.Result.Text))
        {
            return;
        }

        FinalTranscript?.Invoke(this, new SpeechTranscriptEventArgs { Text = e.Result.Text });
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        // State is owned by Start and Stop; this arrival is only consulted for an engine that
        // gave up on its own, where nobody above would otherwise learn dictation had ended.
        if (e.Error is not null)
        {
            _logger.LogError(e.Error, "Dictation ended with an error.");
            Failed?.Invoke(this, new SpeechErrorEventArgs { Message = e.Error.Message });
        }
    }

    private void OnAudioSignalProblemOccurred(object? sender, AudioSignalProblemOccurredEventArgs e)
    {
        _logger.LogWarning("Audio signal problem during dictation: {Problem}.", e.AudioSignalProblem);

        // A dead or silenced microphone is not a noisy one: nothing is going to arrive, and
        // leaving the recording mark on while the engine hears a wall would be a lie.
        if (e.AudioSignalProblem == AudioSignalProblem.NoSignal)
        {
            _ = StopAsync();
        }
    }
}
