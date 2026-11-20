using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Whisper.net;

namespace Eule.App.Speech;

/// <summary>
/// Turns recorded speech into Turkish text on this computer with Whisper (whisper.cpp).
/// The model is downloaded once, only after the user agrees, and kept outside the install folder so updates keep it.
/// No audio ever leaves the computer.
/// </summary>
public sealed class SpeechService(string modelDir)
{
    /// <summary>"small" multilingual model, 5-bit quantised: good Turkish at a modest size.</summary>
    public const string ModelFile = "ggml-small-q5_1.bin";
    public const int ApproxModelMegabytes = 190;
    static readonly Uri ModelUrl = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin");
    const long MinValidBytes = 100_000_000;

    public string ModelPath => Path.Combine(modelDir, ModelFile);

    public bool IsModelReady => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > MinValidBytes;

    /// <summary>Downloads the model with progress (0..1). A partial file never counts as ready.</summary>
    public async Task DownloadModelAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelDir);
        string tmp = ModelPath + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Eule");
        using var res = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? ApproxModelMegabytes * 1_000_000L;
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report(Math.Min(1, (double)done / total));
            }
        }
        if (new FileInfo(tmp).Length < MinValidBytes) throw new IOException("Model dosyası eksik indi.");
        File.Move(tmp, ModelPath, overwrite: true);
        Log.Information("Konuşma modeli indirildi: {Path}", ModelPath);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("tr").Build();
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                sb.Append(segment.Text);
            string text = Clean(sb.ToString());
            Log.Information("Yazıya çevrildi: {Seconds:0.0} sn ses, {Ms} ms", samples.Length / (double)MicRecorder.SampleRate,
                (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return text;
        }, ct);
    }

    // Whisper invents captions for silence or noise: "[Müzik]", "(alkış)", "Altyazı M.K." …
    static readonly Regex Bracketed = new(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", RegexOptions.CultureInvariant);
    static readonly string[] Hallucinations = ["altyazı", "abone ol", "izlediğiniz için teşekkür", "teşekkürler."];

    public static string Clean(string text)
    {
        text = Bracketed.Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        string lower = text.ToLowerInvariant();
        if (Hallucinations.Any(h => lower.StartsWith(h, StringComparison.Ordinal)) && text.Length < 60) return "";
        return text.Trim('-', ' ');
    }
}
