using NAudio.Wave;
using Serilog;

namespace Eule.App.Speech;

public enum MicProblem { None, NoDevice, Blocked, Failed }

/// <summary>What one recording produced. <see cref="Samples"/> are 16 kHz mono floats in [-1, 1], ready for Whisper.</summary>
public sealed record MicResult(float[] Samples, bool HeardSpeech, MicProblem Problem = MicProblem.None);

/// <summary>
/// Records from the default microphone until the speaker has finished: it waits for speech, then stops after a short
/// silence. The speech threshold adapts to the room: the first moments set the noise floor.
/// Events arrive on a background thread.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    const int ChunkMs = 50;
    const int CalibrationMs = 300;
    const int EndSilenceMs = 1200;
    const int NoSpeechTimeoutMs = 7000;
    const int MaxMs = 30000;

    readonly List<float> samples = new(SampleRate * 10);
    WaveInEvent? wave;
    float noiseFloor = float.MaxValue, speechThreshold = 0.02f;
    int totalMs, silenceMs;
    bool heardSpeech, allZero = true, stopping;
    MicProblem problem;

    /// <summary>Loudness of the last 50 ms, 0..1, for the "listening" animation.</summary>
    public event Action<float>? Level;
    public event Action<MicResult>? Finished;

    public static bool HasDevice
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; } catch { return false; }
        }
    }

    public void Start()
    {
        if (!HasDevice)
        {
            Finished?.Invoke(new MicResult([], false, MicProblem.NoDevice));
            return;
        }
        wave = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = ChunkMs };
        wave.DataAvailable += OnData;
        wave.RecordingStopped += OnStopped;
        try
        {
            wave.StartRecording();
            Log.Information("Mikrofon kaydı başladı");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mikrofon açılamadı");
            wave.Dispose();
            wave = null;
            Finished?.Invoke(new MicResult([], false, MicProblem.Blocked));
        }
    }

    /// <summary>Stops now (the user pressed "done"); <see cref="Finished"/> follows.</summary>
    public void Stop()
    {
        if (stopping || wave == null) return;
        stopping = true;
        wave.StopRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            short s = BitConverter.ToInt16(e.Buffer, i * 2);
            if (s != 0) allZero = false;
            float f = s / 32768f;
            samples.Add(f);
            sum += f * f;
        }
        float rms = count > 0 ? (float)Math.Sqrt(sum / count) : 0;
        int ms = count * 1000 / SampleRate;
        totalMs += ms;
        Level?.Invoke(Math.Min(1f, rms * 8));

        if (totalMs <= CalibrationMs)
        {
            noiseFloor = Math.Min(noiseFloor, rms);
            speechThreshold = Math.Max(0.015f, noiseFloor * 3.5f);
            return;
        }

        if (rms >= speechThreshold) { heardSpeech = true; silenceMs = 0; }
        else if (heardSpeech) silenceMs += ms;

        // Windows privacy settings that block the microphone give a stream of exact zeros.
        if (totalMs >= 1500 && allZero) { problem = MicProblem.Blocked; Stop(); return; }
        if (heardSpeech && silenceMs >= EndSilenceMs) Stop();
        else if (!heardSpeech && totalMs >= NoSpeechTimeoutMs) Stop();
        else if (totalMs >= MaxMs) Stop();
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Log.Warning(e.Exception, "Mikrofon kaydı hata ile bitti");
            problem = MicProblem.Failed;
        }
        Log.Information("Mikrofon kaydı bitti: {Ms} ms, konuşma: {Heard}", totalMs, heardSpeech);
        var result = new MicResult(samples.ToArray(), heardSpeech && problem == MicProblem.None, problem);
        wave?.Dispose();
        wave = null;
        Finished?.Invoke(result);
    }

    public void Dispose()
    {
        if (wave == null) return;
        wave.DataAvailable -= OnData;
        wave.RecordingStopped -= OnStopped;
        try { wave.StopRecording(); } catch { }
        wave.Dispose();
        wave = null;
    }
}
