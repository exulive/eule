using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;

namespace Eule.Server;

/// <summary>
/// Serves the web app's page at "/". Its asset links end in <c>?v=__V__</c>; that placeholder becomes a hash of the
/// assets, so every deploy that changes them gives them a new address. Cloudflare in front of the server caches
/// by address (and overrides our Cache-Control), so without this a browser could keep an old or missing app.js.
/// The page itself is never cached.
/// </summary>
public static class WebApp
{
    static readonly string[] Assets = ["app.js", "app.css", "eule.png"];
    static string? page;

    public static IResult Page(IWebHostEnvironment env)
    {
        page ??= Build(env.WebRootFileProvider);
        return Results.Content(page, "text/html; charset=utf-8");
    }

    static string Build(IFileProvider files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in Assets)
        {
            var f = files.GetFileInfo(name);
            if (!f.Exists) continue;
            using var s = f.CreateReadStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            sha.AppendData(ms.ToArray());
        }
        string version = Convert.ToHexString(sha.GetHashAndReset())[..10].ToLowerInvariant();

        var index = files.GetFileInfo("index.html");
        using var r = new StreamReader(index.CreateReadStream());
        return r.ReadToEnd().Replace("__V__", version);
    }
}
