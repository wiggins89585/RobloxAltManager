using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AltManager.Core.Models;

namespace AltManager.Core.Roblox;

public sealed class CookieRotatedEventArgs(string oldCookie, string newCookie) : EventArgs
{
    public string OldCookie { get; } = oldCookie;
    public string NewCookie { get; } = newCookie;
}

/// <summary>
/// Stateless-per-account Roblox web API client. The handler has UseCookies=false,
/// so no cookie jar is shared between accounts — every request carries exactly
/// the cookie passed in. CSRF tokens are cached per cookie.
/// </summary>
public sealed class RobloxApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _csrf = new();

    // Per-account proxies: cookie -> proxy string, and a lazily-built HttpClient per distinct proxy.
    private readonly ConcurrentDictionary<string, string> _cookieProxy = new();
    private readonly ConcurrentDictionary<string, HttpClient> _byProxy = new();

    /// <summary>Raised when Roblox issues a replacement .ROBLOSECURITY via Set-Cookie. Persist it!</summary>
    public event EventHandler<CookieRotatedEventArgs>? CookieRotated;

    public RobloxApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AltManager/1.0");
    }

    // ---------- Authentication ----------

    /// <summary>Returns the user for a cookie, or null if the cookie is invalid/expired (401).</summary>
    public async Task<AuthenticatedUser?> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var res = await Send(req, ct);
        CheckRotation(res, cookie);

        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();

        var dto = await res.Content.ReadFromJsonAsync<UserDto>(cancellationToken: ct)
                  ?? throw new InvalidDataException("Empty /authenticated response.");
        return new AuthenticatedUser(dto.Id, dto.Name, dto.DisplayName);
    }

    /// <summary>
    /// Classifies a cookie: Valid (200 + user), Invalid (401), Banned/moderated (403),
    /// or Error (network/unexpected). Never throws for Roblox/network failures.
    /// 403 on this GET endpoint reliably means the account is moderation-banned — a live
    /// cookie that Roblox refuses to identify — whereas an expired cookie returns 401.
    /// </summary>
    public async Task<(AccountState State, AuthenticatedUser? User)> GetAccountStateAsync(string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        HttpResponseMessage res;
        try { res = await Send(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (AccountState.Error, null); }

        using (res)
        {
            CheckRotation(res, cookie);
            if (res.StatusCode == HttpStatusCode.Unauthorized) return (AccountState.Invalid, null);
            if (res.StatusCode == HttpStatusCode.Forbidden) return (AccountState.Banned, null);
            if (!res.IsSuccessStatusCode) return (AccountState.Error, null);

            var dto = await res.Content.ReadFromJsonAsync<UserDto>(cancellationToken: ct);
            return dto is null ? (AccountState.Error, null) : (AccountState.Valid, new AuthenticatedUser(dto.Id, dto.Name, dto.DisplayName));
        }
    }

    /// <summary>
    /// True if the account has an email address on file, false if not, null if Roblox
    /// wouldn't say (error / rate-limit). Used to route email-bound alts to manual sign-in.
    /// </summary>
    public async Task<bool?> HasEmailAttachedAsync(string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, "https://accountsettings.roblox.com/v1/email", cookie);
        try
        {
            using var res = await Send(req, ct);
            CheckRotation(res, cookie);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync<EmailDto>(cancellationToken: ct);
            return !string.IsNullOrWhiteSpace(dto?.EmailAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Resolves a username to its user (public endpoint, no auth). Includes banned users.</summary>
    public async Task<AuthenticatedUser?> ResolveUserAsync(string username, CancellationToken ct = default)
    {
        using var res = await SendWithCsrfAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users");
            r.Content = JsonContent.Create(new { usernames = new[] { username }, excludeBannedUsers = false });
            return r;
        }, username, ct);
        if (!res.IsSuccessStatusCode) return null;

        var dto = await res.Content.ReadFromJsonAsync<UsernameLookup>(cancellationToken: ct);
        var hit = dto?.Data?.FirstOrDefault();
        return hit is null ? null : new AuthenticatedUser(hit.Id, hit.Name, hit.DisplayName ?? hit.Name);
    }

    /// <summary>
    /// One-time launch ticket used by the roblox-player:// protocol. Tickets are
    /// single-use and short-lived, so request a fresh one for every launch.
    /// </summary>
    public async Task<string> GetAuthenticationTicketAsync(string cookie, CancellationToken ct = default)
    {
        using var res = await SendWithCsrfAsync(() =>
        {
            var r = Build(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie);
            r.Headers.Referrer = new Uri("https://www.roblox.com/");
            r.Content = JsonContent.Create(new { });
            return r;
        }, cookie, ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Cookie is no longer valid.");
        res.EnsureSuccessStatusCode();

        if (!res.Headers.TryGetValues("rbx-authentication-ticket", out var vals))
            throw new InvalidDataException("Roblox did not return an authentication ticket.");
        return vals.First();
    }

    // ---------- Thumbnails ----------

    /// <summary>Batch headshot lookup (max 100 ids per call). Returns userId → image URL.</summary>
    public async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in userIds.Distinct().Chunk(100))
        {
            var url = "https://thumbnails.roblox.com/v1/users/avatar-headshot" +
                      $"?userIds={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false";
            try
            {
                var dto = await _http.GetFromJsonAsync<ThumbResponse>(url, ct);
                if (dto?.Data is null) continue;
                foreach (var t in dto.Data)
                    if (t.State == "Completed" && t.ImageUrl is not null)
                        result[t.TargetId] = t.ImageUrl;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // transient / rate-limit: leave these ids missing so the caller can retry
            }
        }
        return result;
    }

    /// <summary>Batch game-icon lookup by place id. Returns placeId → image URL.</summary>
    public async Task<Dictionary<long, string>> GetPlaceIconsAsync(IEnumerable<long> placeIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in placeIds.Distinct().Chunk(100))
        {
            var url = "https://thumbnails.roblox.com/v1/places/gameicons" +
                      $"?placeIds={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false";
            try
            {
                var dto = await _http.GetFromJsonAsync<ThumbResponse>(url, ct);
                if (dto?.Data is null) continue;
                foreach (var t in dto.Data)
                    if (t.State == "Completed" && t.ImageUrl is not null)
                        result[t.TargetId] = t.ImageUrl;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* leave missing */ }
        }
        return result;
    }

    /// <summary>Resolves place ids to their game names (needs an authenticated cookie). placeId → name.</summary>
    public async Task<Dictionary<long, string>> GetPlaceNamesAsync(IEnumerable<long> placeIds, string cookie, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in placeIds.Distinct().Chunk(50))
        {
            var query = string.Join("&", chunk.Select(id => $"placeIds={id}"));
            using var req = Build(HttpMethod.Get, $"https://games.roblox.com/v1/games/multiget-place-details?{query}", cookie);
            try
            {
                using var res = await Send(req, ct);
                if (!res.IsSuccessStatusCode) continue;
                var dto = await res.Content.ReadFromJsonAsync<List<PlaceDetailDto>>(cancellationToken: ct);
                if (dto is null) continue;
                foreach (var d in dto)
                    if (d.Name is not null) result[d.PlaceId] = d.Name;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* leave missing */ }
        }
        return result;
    }

    /// <summary>Searches Roblox for games by keyword (omni-search). Returns root place ids + names, best-effort.</summary>
    public async Task<List<GameSearchResult>> SearchGamesAsync(string keyword, CancellationToken ct = default)
    {
        var q = keyword.Trim();
        if (q.Length == 0) return new();
        var url = "https://apis.roblox.com/search-api/omni-search" +
                  $"?searchQuery={Uri.EscapeDataString(q)}&sessionId={Guid.NewGuid()}&pageToken=";
        try
        {
            var dto = await _http.GetFromJsonAsync<OmniResponse>(url, ct);
            var seen = new HashSet<long>();
            var list = new List<GameSearchResult>();
            foreach (var group in dto?.SearchResults ?? new())
                foreach (var g in group.Contents ?? new())
                    if (g.RootPlaceId is > 0 && g.Name is not null && seen.Add(g.RootPlaceId.Value))
                        list.Add(new GameSearchResult(g.RootPlaceId.Value, g.Name, (int?)g.PlayerCount));
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new();
        }
    }

    /// <summary>Presence (offline / online / in-game) for many users. Needs an authenticated cookie.</summary>
    public async Task<Dictionary<long, UserPresence>> GetPresencesAsync(IEnumerable<long> userIds, string cookie, CancellationToken ct = default)
    {
        var result = new Dictionary<long, UserPresence>();
        foreach (var chunk in userIds.Distinct().Chunk(100))
        {
            try
            {
                using var res = await SendWithCsrfAsync(() =>
                {
                    var r = Build(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie);
                    r.Content = JsonContent.Create(new { userIds = chunk });
                    return r;
                }, cookie, ct);
                if (!res.IsSuccessStatusCode) continue;
                var dto = await res.Content.ReadFromJsonAsync<PresenceResponse>(cancellationToken: ct);
                foreach (var p in dto?.UserPresences ?? new())
                    result[p.UserId] = new UserPresence(p.UserId, (PresenceType)p.UserPresenceType,
                        string.IsNullOrWhiteSpace(p.LastLocation) ? null : p.LastLocation);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* leave missing */ }
        }
        return result;
    }

    /// <summary>Lists public servers for a place (one page). Returns servers + the next-page cursor.</summary>
    public async Task<(List<ServerInfo> Servers, string? NextCursor)> GetServersAsync(
        long placeId, string? cursor = null, CancellationToken ct = default)
    {
        var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder=Asc&limit=100" +
                  (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        try
        {
            var dto = await _http.GetFromJsonAsync<ServerPage>(url, ct);
            var servers = (dto?.Data ?? new())
                .Where(s => s.Id is not null)
                .Select(s => new ServerInfo(s.Id!, s.Playing ?? 0, s.MaxPlayers ?? 0, s.Fps, s.Ping))
                .ToList();
            return (servers, dto?.NextPageCursor);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (new(), null);
        }
    }

    // ---------- Account info ----------

    /// <summary>The account's own Robux balance (its own cookie).</summary>
    public async Task<long?> GetRobuxAsync(string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, "https://economy.roblox.com/v1/user/currency", cookie);
        try
        {
            using var res = await Send(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync<CurrencyDto>(cancellationToken: ct);
            return dto?.Robux;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>
    /// Reads the account's punishment. Known=false if Roblox wouldn't say; Ends=null when Known
    /// means no end date, i.e. permanent.
    /// </summary>
    public async Task<(bool Known, DateTimeOffset? Ends)> GetBanInfoAsync(string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, "https://usermoderation.roblox.com/v1/not-approved", cookie);
        try
        {
            using var res = await Send(req, ct);
            if (!res.IsSuccessStatusCode) return (false, null);
            var dto = await res.Content.ReadFromJsonAsync<NotApprovedDto>(cancellationToken: ct);
            if (dto?.PunishmentType is null) return (false, null);
            return (true, DateTimeOffset.TryParse(dto.EndDate, out var ends) ? ends : null); // "" = no end date
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return (false, null); }
    }

    /// <summary>Whether the user is in the group (public endpoint). Null if Roblox wouldn't say.</summary>
    public async Task<bool?> IsInGroupAsync(long userId, long groupId, CancellationToken ct = default)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await _http.GetStringAsync($"https://groups.roblox.com/v2/users/{userId}/groups/roles", ct));
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Any(g => g.GetProperty("group").GetProperty("id").GetInt64() == groupId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                      or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>Adds an email to the account; Roblox then sends it a verification link.</summary>
    public async Task<(bool Ok, string Message)> SetEmailAsync(string email, string password, string cookie, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendWithCsrfAsync(() =>
            {
                var r = Build(HttpMethod.Post, "https://accountsettings.roblox.com/v1/email", cookie);
                r.Content = JsonContent.Create(new { emailAddress = email, password });
                return r;
            }, cookie, ct);
            if (res.IsSuccessStatusCode) return (true, "added");
            return (false, ExtractError(await res.Content.ReadAsStringAsync(ct)) ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, ex.Message); }
    }

    /// <summary>Whether the account has Premium (its own cookie).</summary>
    public async Task<bool?> GetPremiumAsync(long userId, string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership", cookie);
        try
        {
            using var res = await Send(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(ct);
            return bool.TryParse(body.Trim(), out var b) ? b : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>When the account was created (public endpoint).</summary>
    public async Task<DateTimeOffset?> GetCreatedAsync(long userId, CancellationToken ct = default)
    {
        try
        {
            var dto = await _http.GetFromJsonAsync<UserInfoDto>($"https://users.roblox.com/v1/users/{userId}", ct);
            return dto?.Created;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Redeems a promo code for the account. Returns Roblox's result message.</summary>
    public async Task<(bool Ok, string Message)> RedeemPromoCodeAsync(string code, string cookie, CancellationToken ct = default)
    {
        var c = code.Trim();
        if (c.Length == 0) return (false, "empty code");
        try
        {
            using var res = await SendWithCsrfAsync(() =>
            {
                var r = Build(HttpMethod.Post, "https://apis.roblox.com/promotion-codes/v1/redeem", cookie);
                r.Content = JsonContent.Create(new { code = c });
                return r;
            }, cookie, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return (true, "redeemed");
            return (false, ExtractError(body) ?? ExtractMessage(body) ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, ex.Message); }
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    /// <summary>The account's saved outfits (its own cookie). Returns (outfitId, name).</summary>
    public async Task<List<(long Id, string Name)>> GetOutfitsAsync(long userId, string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"https://avatar.roblox.com/v1/users/{userId}/outfits?page=1&itemsPerPage=50", cookie);
        try
        {
            using var res = await Send(req, ct);
            if (!res.IsSuccessStatusCode) return new();
            var dto = await res.Content.ReadFromJsonAsync<OutfitsResponse>(cancellationToken: ct);
            return (dto?.Data ?? new()).Where(o => o.Name is not null).Select(o => (o.Id, o.Name!)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(); }
    }

    /// <summary>Makes the account wear one of its saved outfits.</summary>
    public async Task<(bool Ok, string Message)> WearOutfitAsync(long outfitId, string cookie, CancellationToken ct = default)
    {
        try
        {
            using var res = await SendWithCsrfAsync(
                () => Build(HttpMethod.Post, $"https://avatar.roblox.com/v1/outfits/{outfitId}/wear", cookie), cookie, ct);
            if (res.IsSuccessStatusCode) return (true, "worn");
            return (false, ExtractError(await res.Content.ReadAsStringAsync(ct)) ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, ex.Message); }
    }

    // ---------- Groups ----------

    /// <summary>The group's Robux fund balance (a cookie with group-funds permission).</summary>
    public async Task<long?> GetGroupFundsAsync(long groupId, string cookie, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"https://economy.roblox.com/v1/groups/{groupId}/currency", cookie);
        try
        {
            using var res = await Send(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync<CurrencyDto>(cancellationToken: ct);
            return dto?.Robux;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>One-time group payout: pays each recipient a fixed Robux amount from group funds (payer cookie).</summary>
    public async Task<(bool Ok, string Message)> GroupPayoutAsync(
        long groupId, string cookie, IReadOnlyList<(long UserId, long Amount)> recipients, CancellationToken ct = default)
    {
        if (recipients.Count == 0) return (false, "no recipients");
        try
        {
            using var res = await SendWithCsrfAsync(() =>
            {
                var r = Build(HttpMethod.Post, $"https://groups.roblox.com/v1/groups/{groupId}/payouts", cookie);
                r.Content = JsonContent.Create(new
                {
                    PayoutType = "FixedAmount",
                    Recipients = recipients.Select(x => new { recipientId = x.UserId, recipientType = "User", amount = x.Amount })
                });
                return r;
            }, cookie, ct);
            if (res.IsSuccessStatusCode) return (true, "paid");
            return (false, ExtractError(await res.Content.ReadAsStringAsync(ct)) ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, ex.Message); }
    }

    /// <summary>Pulls the first Roblox API error message out of a JSON error body, if present.</summary>
    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0 &&
                errs[0].TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch (System.Text.Json.JsonException) { /* not JSON */ }
        return null;
    }

    // ---------- Private server share links ----------

    /// <summary>
    /// Resolves a modern share link (roblox.com/share?code=XXX&amp;type=Server) to (placeId, linkCode).
    /// Requires an authenticated cookie. Returns null if the link isn't a server invite.
    /// </summary>
    public async Task<(long PlaceId, string LinkCode)?> ResolveServerShareLinkAsync(
        string cookie, string shareCode, CancellationToken ct = default)
    {
        using var res = await SendWithCsrfAsync(() =>
        {
            var r = Build(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie);
            r.Content = JsonContent.Create(new { linkId = shareCode, linkType = "Server" });
            return r;
        }, cookie, ct);
        res.EnsureSuccessStatusCode();

        var dto = await res.Content.ReadFromJsonAsync<ShareLinkDto>(cancellationToken: ct);
        var inv = dto?.PrivateServerInviteData;
        return inv is { PlaceId: > 0, LinkCode: not null } ? (inv.PlaceId, inv.LinkCode) : null;
    }

    // ---------- Plumbing ----------

    private static HttpRequestMessage Build(HttpMethod m, string url, string cookie)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.TryAddWithoutValidation("Cookie", $"{RobloxCookie.Name}={cookie}");
        return req;
    }

    // ---------- Per-account proxies ----------

    /// <summary>
    /// Routes this account's web-API traffic (validation, auth-ticket for launch, info lookups)
    /// through <paramref name="proxy"/>. Blank clears it. Accepts host:port, host:port:user:pass,
    /// or scheme://[user:pass@]host:port (http/https/socks5/socks4). The game client process itself
    /// is not proxied — only the authenticated web calls this app makes on the account's behalf.
    /// </summary>
    public void SetProxy(string cookie, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return;
        if (string.IsNullOrWhiteSpace(proxy)) _cookieProxy.TryRemove(cookie, out _);
        else _cookieProxy[cookie] = proxy.Trim();
    }

    /// <summary>True if <paramref name="proxy"/> parses to a usable proxy (blank is allowed = no proxy).</summary>
    public static bool IsValidProxy(string? proxy) =>
        string.IsNullOrWhiteSpace(proxy) || BuildProxy(proxy) is not null;

    /// <summary>Sends a request through the account's proxy client when one is set, else the default client.</summary>
    private Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct) =>
        ClientFor(req).SendAsync(req, ct);

    private HttpClient ClientFor(HttpRequestMessage req)
    {
        if (_cookieProxy.IsEmpty) return _http;
        if (!req.Headers.TryGetValues("Cookie", out var cookies)) return _http;
        var prefix = RobloxCookie.Name + "=";
        foreach (var c in cookies)
            if (c.StartsWith(prefix, StringComparison.Ordinal) &&
                _cookieProxy.TryGetValue(c[prefix.Length..], out var proxy))
                return _byProxy.GetOrAdd(proxy, CreateProxyClient);
        return _http;
    }

    private static HttpClient CreateProxyClient(string proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (BuildProxy(proxy) is { } wp) { handler.Proxy = wp; handler.UseProxy = true; }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AltManager/1.0");
        return client;
    }

    /// <summary>Parses a proxy string into a <see cref="WebProxy"/>, or null if it is unusable.</summary>
    private static WebProxy? BuildProxy(string proxy)
    {
        try
        {
            var s = proxy.Trim();
            if (s.Length == 0) return null;

            var scheme = "http";
            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) { scheme = s[..schemeIdx].ToLowerInvariant(); s = s[(schemeIdx + 3)..]; }

            string? user = null, pass = null, hostPort;
            var at = s.LastIndexOf('@');
            if (at >= 0)
            {
                var creds = s[..at].Split(':', 2);
                user = creds[0];
                pass = creds.Length > 1 ? creds[1] : "";
                hostPort = s[(at + 1)..];
            }
            else
            {
                // host:port  or  host:port:user:pass
                var parts = s.Split(':');
                if (parts.Length == 4) { hostPort = parts[0] + ":" + parts[1]; user = parts[2]; pass = parts[3]; }
                else hostPort = s;
            }

            if (!hostPort.Contains(':')) return null; // need host:port
            var wp = new WebProxy($"{scheme}://{hostPort}");
            if (user is not null) wp.Credentials = new NetworkCredential(user, pass ?? "");
            return wp;
        }
        catch { return null; }
    }

    /// <summary>Roblox returns 403 + x-csrf-token on the first mutating call; retry once with it.</summary>
    private async Task<HttpResponseMessage> SendWithCsrfAsync(Func<HttpRequestMessage> factory, string cookie, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var req = factory();
            if (_csrf.TryGetValue(cookie, out var token))
                req.Headers.TryAddWithoutValidation("x-csrf-token", token);

            var res = await Send(req, ct);
            req.Dispose();
            CheckRotation(res, cookie);

            if (res.StatusCode == HttpStatusCode.Forbidden &&
                res.Headers.TryGetValues("x-csrf-token", out var fresh))
            {
                _csrf[cookie] = fresh.First();
                res.Dispose();
                continue;
            }
            return res;
        }
        throw new HttpRequestException("CSRF handshake failed.");
    }

    private void CheckRotation(HttpResponseMessage res, string oldCookie)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var setCookies)) return;
        foreach (var sc in setCookies)
        {
            var first = sc.Split(';', 2)[0];
            if (!first.StartsWith(RobloxCookie.Name + "=", StringComparison.Ordinal)) continue;
            var value = first[(RobloxCookie.Name.Length + 1)..];
            if (value.Length > 0 && value != oldCookie)
            {
                if (_csrf.TryRemove(oldCookie, out var tok)) _csrf[value] = tok;
                if (_cookieProxy.TryRemove(oldCookie, out var px)) _cookieProxy[value] = px;
                CookieRotated?.Invoke(this, new CookieRotatedEventArgs(oldCookie, value));
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var c in _byProxy.Values) c.Dispose();
    }

    private sealed record UserDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("displayName")] string DisplayName);

    private sealed record EmailDto(
        [property: JsonPropertyName("emailAddress")] string? EmailAddress,
        [property: JsonPropertyName("verified")] bool Verified);

    private sealed record UsernameLookup([property: JsonPropertyName("data")] List<LookupDto>? Data);

    private sealed record LookupDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("displayName")] string? DisplayName);

    private sealed record ShareLinkDto(
        [property: JsonPropertyName("privateServerInviteData")] InviteDto? PrivateServerInviteData);

    private sealed record InviteDto(
        [property: JsonPropertyName("placeId")] long PlaceId,
        [property: JsonPropertyName("linkCode")] string? LinkCode);

    private sealed record ThumbResponse([property: JsonPropertyName("data")] List<ThumbDto>? Data);

    private sealed record PlaceDetailDto(
        [property: JsonPropertyName("placeId")] long PlaceId,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record OmniResponse([property: JsonPropertyName("searchResults")] List<OmniGroup>? SearchResults);
    private sealed record OmniGroup([property: JsonPropertyName("contents")] List<OmniGame>? Contents);
    private sealed record OmniGame(
        [property: JsonPropertyName("rootPlaceId")] long? RootPlaceId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("playerCount")] long? PlayerCount);

    private sealed record ServerPage(
        [property: JsonPropertyName("nextPageCursor")] string? NextPageCursor,
        [property: JsonPropertyName("data")] List<ServerDto>? Data);
    private sealed record ServerDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("maxPlayers")] int? MaxPlayers,
        [property: JsonPropertyName("playing")] int? Playing,
        [property: JsonPropertyName("fps")] double? Fps,
        [property: JsonPropertyName("ping")] int? Ping);

    private sealed record OutfitsResponse([property: JsonPropertyName("data")] List<OutfitDto>? Data);
    private sealed record OutfitDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record NotApprovedDto(
        [property: JsonPropertyName("punishmentTypeDescription")] string? PunishmentType,
        [property: JsonPropertyName("endDate")] string? EndDate);

    private sealed record CurrencyDto([property: JsonPropertyName("robux")] long Robux);
    private sealed record UserInfoDto([property: JsonPropertyName("created")] DateTimeOffset? Created);

    private sealed record PresenceResponse([property: JsonPropertyName("userPresences")] List<PresenceDto>? UserPresences);
    private sealed record PresenceDto(
        [property: JsonPropertyName("userId")] long UserId,
        [property: JsonPropertyName("userPresenceType")] int UserPresenceType,
        [property: JsonPropertyName("lastLocation")] string? LastLocation);

    private sealed record ThumbDto(
        [property: JsonPropertyName("targetId")] long TargetId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("imageUrl")] string? ImageUrl);
}
