using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eule.Core.Cloud;

/// <summary>The signed-in account as kept on this computer. Only the long-lived refresh token is stored.</summary>
public sealed record Account(string Email, string DisplayName, string RefreshToken, DateTime RefreshExpiresAt);

/// <summary>Where the account is kept. The app encrypts it for the current Windows user (DPAPI).</summary>
public interface IAccountStore
{
    Account? Load();
    void Save(Account account);
    void Clear();
}

public sealed record NoteInfo(Guid Id, string Text, string Title, DateTime? DueAt, bool HasTime, bool Done, bool Deleted,
    string Source = "voice", DateTime CreatedAt = default, string TimeZone = "", string Kind = "todo", string Body = "",
    Guid? DictationId = null);
public sealed record DictationResult(Guid DictationId, string Action, string Reply, NoteInfo? Note);
/// <summary>One entry of the dictation history and the agenda / to-do items it produced.</summary>
public sealed record DictationInfo(Guid Id, string Text, string Source, DateTime ReceivedAt, string Reply, List<NoteInfo> Notes);

public enum ApiError
{
    /// <summary>No connection, DNS, timeout: worth retrying later.</summary>
    Network,
    /// <summary>Wrong e-mail or password.</summary>
    InvalidCredentials,
    /// <summary>The session ended (logged out elsewhere, password changed, account disabled): sign in again.</summary>
    SignedOut,
    RateLimited,
    /// <summary>The server had a problem (5xx): worth retrying later.</summary>
    Server,
    /// <summary>The server refused the request itself; retrying will not help.</summary>
    Rejected,
    /// <summary>This month's dictation allowance is used up; the message says when it renews.</summary>
    LimitReached,
}

public sealed class ApiException(ApiError kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public ApiError Kind { get; } = kind;
    public bool IsTransient => Kind is ApiError.Network or ApiError.Server or ApiError.RateLimited;
}

/// <summary>
/// Talks to the Eule server. Keeps the short-lived access token in memory and refreshes it silently with the
/// stored refresh token (which rotates on every use). If the server says the session is over, the stored
/// account is cleared and <see cref="SignedOut"/> fires.
/// </summary>
public sealed class EuleApiClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    readonly HttpClient http;
    readonly IAccountStore store;
    readonly TimeProvider time;
    readonly SemaphoreSlim refreshLock = new(1, 1);
    string? accessToken;
    DateTime accessExpiresAt;

    public EuleApiClient(HttpClient http, IAccountStore store, TimeProvider? time = null)
    {
        this.http = http;
        this.store = store;
        this.time = time ?? TimeProvider.System;
        Account = store.Load();
    }

    public Account? Account { get; private set; }
    public bool IsSignedIn => Account != null;
    public Uri? Server => http.BaseAddress;

    /// <summary>The session ended on the server side; the user has to sign in again.</summary>
    public event Action? SignedOut;
    public event Action? SignedIn;

    // ------------------------------------------------------------------ account

    public async Task<Account> LoginAsync(string email, string password, string device, CancellationToken ct = default)
    {
        var res = await Send(() => Post("api/auth/login", new { email, password, device }), ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException(ApiError.InvalidCredentials, "E-posta ya da şifre hatalı.");
        await EnsureOk(res);
        var tokens = await Read<TokenDto>(res, ct);
        Accept(tokens);
        SignedIn?.Invoke();
        return Account!;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var account = Account;
        Forget(raise: false);
        if (account == null) return;
        try
        {
            using var res = await Send(() => Post("api/auth/logout", new { refreshToken = account.RefreshToken }), ct);
        }
        catch (ApiException)
        {
            // Offline: the token is already forgotten here and expires on the server by itself.
        }
    }

    // ------------------------------------------------------------------ notes

    public async Task<DictationResult> DictateAsync(Guid clientId, string text, string source, string timeZone,
        CancellationToken ct = default)
    {
        using var res = await Authorized(() => Post("api/dictations", new { clientId, text, source, timeZone }), ct);
        await EnsureOk(res);
        return await Read<DictationResult>(res, ct);
    }

    public async Task DeleteNoteAsync(Guid id, CancellationToken ct = default)
    {
        using var res = await Authorized(() => new HttpRequestMessage(HttpMethod.Delete, $"api/notes/{id}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return; // already gone
        await EnsureOk(res);
    }

    /// <summary>Removes a dictation and everything it produced (undo, and delete in the history).</summary>
    public async Task DeleteDictationAsync(Guid id, CancellationToken ct = default)
    {
        using var res = await Authorized(() => new HttpRequestMessage(HttpMethod.Delete, $"api/dictations/{id}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return; // already gone
        await EnsureOk(res);
    }

    /// <summary>The dictation history, newest first.</summary>
    public async Task<List<DictationInfo>> GetDictationsAsync(CancellationToken ct = default)
    {
        using var res = await Authorized(() => new HttpRequestMessage(HttpMethod.Get, "api/dictations"), ct);
        await EnsureOk(res);
        return await Read<List<DictationInfo>>(res, ct);
    }

    public async Task<List<NoteInfo>> GetNotesAsync(DateTime? since = null, CancellationToken ct = default)
    {
        string path = since is DateTime s ? $"api/notes?since={Uri.EscapeDataString(s.ToUniversalTime().ToString("O"))}" : "api/notes";
        using var res = await Authorized(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        await EnsureOk(res);
        return await Read<List<NoteInfo>>(res, ct);
    }

    // ------------------------------------------------------------------ plumbing

    sealed record UserDto(Guid Id, string Email, string DisplayName);
    sealed record TokenDto(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt, UserDto User);

    void Accept(TokenDto t)
    {
        accessToken = t.AccessToken;
        accessExpiresAt = t.AccessTokenExpiresAt.ToUniversalTime();
        Account = new Account(t.User.Email, t.User.DisplayName, t.RefreshToken, t.RefreshTokenExpiresAt.ToUniversalTime());
        store.Save(Account);
    }

    void Forget(bool raise)
    {
        accessToken = null;
        Account = null;
        store.Clear();
        if (raise) SignedOut?.Invoke();
    }

    async Task<HttpResponseMessage> Authorized(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        if (Account == null) throw new ApiException(ApiError.SignedOut, "Oturum açılmamış.");
        if (accessToken == null || time.GetUtcNow().UtcDateTime >= accessExpiresAt - RefreshMargin)
            await RefreshAsync(ct);

        var res = await Send(() => WithToken(make()), ct);
        if (res.StatusCode != HttpStatusCode.Unauthorized) return res;

        // The token may have been revoked or the clocks disagree: refresh once and try again.
        res.Dispose();
        await RefreshAsync(ct, force: true);
        return await Send(() => WithToken(make()), ct);
    }

    HttpRequestMessage WithToken(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    async Task RefreshAsync(CancellationToken ct, bool force = false)
    {
        string? before = accessToken;
        await refreshLock.WaitAsync(ct);
        try
        {
            // Another call refreshed while we waited.
            if (accessToken != before && accessToken != null && !force) return;
            var account = Account ?? throw new ApiException(ApiError.SignedOut, "Oturum açılmamış.");
            using var res = await Send(() => Post("api/auth/refresh", new { refreshToken = account.RefreshToken }), ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                Forget(raise: true);
                throw new ApiException(ApiError.SignedOut, "Oturum sona erdi, yeniden giriş yap.");
            }
            await EnsureOk(res);
            Accept(await Read<TokenDto>(res, ct));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    static HttpRequestMessage Post(string path, object body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };

    async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        try
        {
            using var req = make();
            return await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(ApiError.Network, "Sunucuya ulaşılamadı.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(ApiError.Network, "Sunucu zamanında yanıt vermedi.", ex);
        }
    }

    static async Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        int code = (int)res.StatusCode;
        if (code == 403)
        {
            string body = await res.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("title", out var t) && t.GetString() == "dictation_limit")
                    throw new ApiException(ApiError.LimitReached,
                        root.TryGetProperty("detail", out var d) ? d.GetString() ?? "Dikte hakkın doldu." : "Dikte hakkın doldu.");
            }
            catch (JsonException) { }
            throw new ApiException(ApiError.Rejected, $"Sunucu isteği reddetti (403): {body}");
        }
        throw code switch
        {
            401 => new ApiException(ApiError.SignedOut, "Oturum sona erdi."),
            429 => new ApiException(ApiError.RateLimited, "Çok fazla deneme. Biraz bekle."),
            >= 500 => new ApiException(ApiError.Server, $"Sunucu hatası ({code})."),
            _ => new ApiException(ApiError.Rejected, $"Sunucu isteği reddetti ({code}): {await res.Content.ReadAsStringAsync()}"),
        };
    }

    static async Task<T> Read<T>(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ApiException(ApiError.Server, "Boş yanıt.");
}
