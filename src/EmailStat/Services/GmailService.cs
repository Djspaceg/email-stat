namespace EmailStat.Services;

using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using EmailStat.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

// Alias the Google SDK type to avoid the naming clash with this class.
using GoogleGmailApi = Google.Apis.Gmail.v1.GmailService;

/// <summary>
/// Wraps the Gmail REST API.  Handles OAuth 2.0, email-group aggregation,
/// and DPAPI-encrypted local caching with incremental history sync.
/// </summary>
public sealed partial class GmailService
{
    // Path to the OAuth client secret file downloaded from Google Cloud Console.
    private static readonly string ClientSecretPath = Path.Combine(
        AppContext.BaseDirectory, "client_secret.json");

    private static readonly string[] Scopes = [GoogleGmailApi.Scope.GmailReadonly];
    private const string AppName = "EmailStat";

    private GoogleGmailApi? _svc;
    private readonly MessageCacheService _cache = new();

    /// <summary>True once the user has successfully authenticated.</summary>
    public bool IsAuthenticated => _svc is not null;

    /// <summary>
    /// True when a cached OAuth token exists on disk, meaning the user has previously
    /// authenticated and the browser consent screen will be skipped on next connect.
    /// </summary>
    public bool HasStoredToken
    {
        get
        {
            string tokenFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmailStat", "token");
            // The Google SDK stores token files named after the user key ("user").
            return Directory.Exists(tokenFolder) &&
                   Directory.EnumerateFiles(tokenFolder).Any();
        }
    }

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ClientSecretPath))
            throw new FileNotFoundException(
                "client_secret.json not found. Download it from Google Cloud Console " +
                $"and place it at: {ClientSecretPath}", ClientSecretPath);

        GoogleClientSecrets googleSecrets;
        await using (var stream = File.OpenRead(ClientSecretPath))
            googleSecrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

        string tokenFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailStat", "token");

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            googleSecrets.Secrets, Scopes, "user", ct,
            new FileDataStore(tokenFolder, fullPath: true));

        _svc = new GoogleGmailApi(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = AppName,
        });
    }

    // -------------------------------------------------------------------------
    // Data fetching — cache-aware
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns grouped email counts.  On the first call a full fetch is performed
    /// and results are encrypted and saved locally.  On subsequent calls only the
    /// messages added or removed since the last sync are fetched from Gmail.
    /// If <paramref name="maxMessages"/> exceeds the number of currently cached
    /// messages, a top-up fetch is performed before applying the history diff.
    /// </summary>
    public async Task<IReadOnlyList<EmailGroup>> FetchGroupsAsync(
        bool groupByDomain,
        int maxMessages = 5_000,
        IProgress<(int Fetched, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        EnsureAuthenticated();

        bool cacheLoaded = _cache.TryLoad();

        if (cacheLoaded && _cache.LastHistoryId.HasValue)
        {
            // If the user wants more messages than we have cached, fetch the gap first.
            if (_cache.Messages.Count < maxMessages)
                await TopUpFetchAsync(maxMessages, progress, ct);
            else
                progress?.Report((0, 0)); // signal start for incremental sync

            await ApplyHistoryDiffAsync(_cache.LastHistoryId.Value, progress, ct);
        }
        else
        {
            await FullFetchAsync(maxMessages, progress, ct);
        }

        return groupByDomain
            ? BuildDomainGroups(_cache.Messages)
            : BuildAddressGroups(_cache.Messages);
    }

    /// <summary>Clears the local encrypted cache, forcing a full re-fetch next time.</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Re-groups the already-cached messages without any network calls.
    /// Returns null if no cache is loaded yet.
    /// </summary>
    public IReadOnlyList<EmailGroup>? RegroupCache(bool groupByDomain)
    {
        if (_cache.Messages.Count == 0) return null;
        return groupByDomain ? BuildDomainGroups(_cache.Messages) : BuildAddressGroups(_cache.Messages);
    }

    // -------------------------------------------------------------------------
    // Full fetch (first run)
    // -------------------------------------------------------------------------

    private async Task FullFetchAsync(
        int maxMessages,
        IProgress<(int Fetched, int Total)>? progress,
        CancellationToken ct)
    {
        _cache.Clear();

        // Grab the current historyId *before* we start paging so that any messages
        // that arrive during the fetch are captured on the next incremental sync.
        ulong startHistoryId = await GetCurrentHistoryIdAsync(ct);

        string? pageToken = null;
        int fetched = 0;

        do
        {
            ct.ThrowIfCancellationRequested();

            var listReq = _svc!.Users.Messages.List("me");
            listReq.PageToken   = pageToken;
            listReq.MaxResults  = Math.Min(500, maxMessages - fetched);
            listReq.LabelIds    = new Google.Apis.Util.Repeatable<string>(["INBOX"]);
            listReq.Fields      = "nextPageToken,messages(id)";

            ListMessagesResponse listResp = await listReq.ExecuteAsync(ct);
            if (listResp.Messages is null) break;

            var ids = listResp.Messages.Select(m => m.Id).ToList();
            fetched = await FetchAndStoreFromHeadersAsync(ids, fetched, maxMessages, progress, ct);

            pageToken = listResp.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && fetched < maxMessages);

        _cache.Save(startHistoryId);
    }

    // -------------------------------------------------------------------------
    // Top-up fetch (cache exists but user wants more than we have)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches additional messages beyond what is already cached, up to
    /// <paramref name="maxMessages"/> total.  Already-cached IDs are skipped so
    /// no duplicate work is done.
    /// </summary>
    private async Task TopUpFetchAsync(
        int maxMessages,
        IProgress<(int Fetched, int Total)>? progress,
        CancellationToken ct)
    {
        int needed  = maxMessages - _cache.Messages.Count;
        int fetched = _cache.Messages.Count; // start the progress counter from where we are

        string? pageToken = null;

        do
        {
            ct.ThrowIfCancellationRequested();

            var listReq = _svc!.Users.Messages.List("me");
            listReq.PageToken  = pageToken;
            listReq.MaxResults = Math.Min(500, needed);
            listReq.LabelIds   = new Google.Apis.Util.Repeatable<string>(["INBOX"]);
            listReq.Fields     = "nextPageToken,messages(id)";

            ListMessagesResponse listResp = await listReq.ExecuteAsync(ct);
            if (listResp.Messages is null) break;

            // Skip IDs we already have in cache.
            var newIds = listResp.Messages
                .Select(m => m.Id)
                .Where(id => !_cache.Messages.ContainsKey(id))
                .ToList();

            fetched = await FetchAndStoreFromHeadersAsync(newIds, fetched, maxMessages, progress, ct);
            needed -= newIds.Count;

            pageToken = listResp.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && needed > 0);

        // Save with the existing historyId — the history diff will update it.
        if (_cache.LastHistoryId.HasValue)
            _cache.Save(_cache.LastHistoryId.Value);
    }

    // -------------------------------------------------------------------------
    // Incremental sync via History API
    // -------------------------------------------------------------------------

    private async Task ApplyHistoryDiffAsync(
        ulong startHistoryId,
        IProgress<(int Fetched, int Total)>? progress,
        CancellationToken ct)
    {
        var added   = new List<string>();
        var removed = new HashSet<string>(StringComparer.Ordinal);

        string? pageToken = null;
        ulong latestHistoryId = startHistoryId;

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();

                var req = _svc!.Users.History.List("me");
                req.StartHistoryId = startHistoryId;
                req.PageToken      = pageToken;
                req.LabelId        = "INBOX";

                ListHistoryResponse resp = await req.ExecuteAsync(ct);

                if (resp.HistoryId.HasValue)
                    latestHistoryId = resp.HistoryId.Value;

                if (resp.History is not null)
                {
                    foreach (var record in resp.History)
                    {
                        if (record.MessagesAdded is not null)
                            foreach (var m in record.MessagesAdded)
                                added.Add(m.Message.Id);

                        if (record.MessagesDeleted is not null)
                            foreach (var m in record.MessagesDeleted)
                                removed.Add(m.Message.Id);
                    }
                }

                pageToken = resp.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            // historyId too old — fall back to a full fetch.
            await FullFetchAsync(5_000, progress, ct);
            return;
        }

        // Remove first so a message that was added-then-deleted isn't fetched at all.
        foreach (string id in removed)
            _cache.Messages.Remove(id);

        // Only fetch IDs we don't already have.
        var toFetch = added.Where(id => !_cache.Messages.ContainsKey(id)).ToList();
        await FetchAndStoreFromHeadersAsync(toFetch, 0, toFetch.Count, progress, ct);

        _cache.Save(latestHistoryId);
    }

    // -------------------------------------------------------------------------
    // Shared fetch helper
    // -------------------------------------------------------------------------

    private async Task<int> FetchAndStoreFromHeadersAsync(
        List<string> ids,
        int fetchedSoFar,
        int total,
        IProgress<(int Fetched, int Total)>? progress,
        CancellationToken ct)
    {
        const int concurrency = 5;
        int fetched = fetchedSoFar;

        for (int offset = 0; offset < ids.Count; offset += 100)
        {
            var chunk = ids.Skip(offset).Take(100).ToList();

            for (int i = 0; i < chunk.Count; i += concurrency)
            {
                ct.ThrowIfCancellationRequested();

                var group = chunk.Skip(i).Take(concurrency).ToList();
                (string Id, string? From)[] results = await Task.WhenAll(
                    group.Select(async id => (Id: id, From: await GetFromHeaderAsync(id, ct))));

                foreach (var (id, from) in results)
                    if (!string.IsNullOrWhiteSpace(from))
                        _cache.Messages[id] = ExtractAddress(from);

                fetched += group.Count;
                progress?.Report((fetched, total));

                if (i + concurrency < chunk.Count)
                    await Task.Delay(200, ct);
            }
        }

        return fetched;
    }

    // -------------------------------------------------------------------------
    // Gmail helpers
    // -------------------------------------------------------------------------

    private async Task<ulong> GetCurrentHistoryIdAsync(CancellationToken ct)
    {
        var req = _svc!.Users.GetProfile("me");
        req.Fields = "historyId";
        var profile = await req.ExecuteAsync(ct);
        return profile.HistoryId ?? 0;
    }

    private async Task<string?> GetFromHeaderAsync(string messageId, CancellationToken ct)
    {
        int delayMs = 500;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var req = _svc!.Users.Messages.Get("me", messageId);
                req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                req.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(["From"]);
                req.Fields = "payload/headers";

                Message msg = await req.ExecuteAsync(ct);
                return msg.Payload?.Headers?
                    .FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
            }
            catch (Google.GoogleApiException ex) when (
                ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                (int)ex.HttpStatusCode == 429 ||
                ex.Error?.Code == 403)
            {
                if (attempt == 4) return null;
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
            catch (Google.GoogleApiException) { return null; }
            catch (System.Net.Http.HttpRequestException) { return null; }
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Grouping
    // -------------------------------------------------------------------------

    private static IReadOnlyList<EmailGroup> BuildAddressGroups(Dictionary<string, string> messages)
    {
        return messages.Values
            .GroupBy(addr => addr, StringComparer.OrdinalIgnoreCase)
            .Select(g => new EmailGroup
            {
                Key = g.Key, DisplayName = g.Key,
                EmailCount = g.LongCount(), IsDomain = false,
            })
            .OrderByDescending(g => g.EmailCount)
            .ToList();
    }

    private static IReadOnlyList<EmailGroup> BuildDomainGroups(Dictionary<string, string> messages)
    {
        var domains = new Dictionary<string, (long Total, List<EmailGroup> Subs)>(StringComparer.OrdinalIgnoreCase);

        foreach (var addr in messages.Values)
        {
            string domain = ExtractDomain(addr);
            if (!domains.TryGetValue(domain, out var entry))
            {
                entry = (0, []);
                domains[domain] = entry;
            }
            var existing = entry.Subs.FirstOrDefault(s => s.Key == addr);
            if (existing is null)
                entry.Subs.Add(new EmailGroup { Key = addr, DisplayName = addr, EmailCount = 1, IsDomain = false });
            else
                existing.EmailCount++;
            domains[domain] = (entry.Total + 1, entry.Subs);
        }

        return domains
            .Select(kv => new EmailGroup
            {
                Key = kv.Key, DisplayName = kv.Key,
                EmailCount = kv.Value.Total, IsDomain = true,
                SubGroups = [.. kv.Value.Subs.OrderByDescending(s => s.EmailCount)],
            })
            .OrderByDescending(g => g.EmailCount)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // String parsing
    // -------------------------------------------------------------------------

    private static string ExtractAddress(string from)
    {
        Match m = FromAngleBracketRegex().Match(from);
        string addr = m.Success ? m.Groups[1].Value : from;
        return addr.Trim().ToLowerInvariant();
    }

    private static string ExtractDomain(string address)
    {
        int at = address.LastIndexOf('@');
        return at >= 0 ? address[(at + 1)..] : address;
    }

    [GeneratedRegex(@"<([^>]+)>", RegexOptions.Compiled)]
    private static partial Regex FromAngleBracketRegex();

    private void EnsureAuthenticated()
    {
        if (_svc is null)
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
    }
}

