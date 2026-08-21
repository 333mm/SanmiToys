using System;
using System.Speech.Synthesis;

namespace SanmiToys.Modules.SnapTrans.Services;

public class TextToSpeechService : IDisposable
{
    private SpeechSynthesizer? _synthesizer;

    public TextToSpeechService()
    {
        try
        {
            _synthesizer = new SpeechSynthesizer();
        }
        catch
        {
            _synthesizer = null;
        }
    }

    public void Speak(string text)
    {
        if (_synthesizer == null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text);
        }
        catch { }
    }

    public void Stop()
    {
        try
        {
            _synthesizer?.SpeakAsyncCancelAll();
        }
        catch { }
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
        _synthesizer = null;
    }
}
