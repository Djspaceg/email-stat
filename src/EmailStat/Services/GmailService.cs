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
/// Wraps the Gmail REST API.  Handles OAuth 2.0 and email-group aggregation.
/// </summary>
public sealed partial class GmailService
{
    // The only scope we need is read-only access.
    private static readonly string[] Scopes = [GoogleGmailApi.Scope.GmailReadonly];
    private const string AppName = "EmailStat";

    private GoogleGmailApi? _svc;

    /// <summary>True once the user has successfully authenticated.</summary>
    public bool IsAuthenticated => _svc is not null;

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs the OAuth 2.0 "installed app" flow using <paramref name="clientSecretsPath"/>.
    /// Tokens are cached in <c>%LOCALAPPDATA%\EmailStat\token\</c> for subsequent runs.
    /// </summary>
    public async Task AuthenticateAsync(string clientSecretsPath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(clientSecretsPath, FileMode.Open, FileAccess.Read);

        string tokenFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailStat", "token");

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            user: "user",
            ct,
            new FileDataStore(tokenFolder, fullPath: true));

        _svc = new GoogleGmailApi(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = AppName,
        });
    }

    // -------------------------------------------------------------------------
    // Data fetching
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches up to <paramref name="maxMessages"/> messages, extracts the From header,
    /// and returns groups sorted by email count descending.
    /// </summary>
    /// <param name="groupByDomain">
    ///   When <c>true</c> groups by sender domain; when <c>false</c> groups by exact address.
    /// </param>
    /// <param name="maxMessages">Maximum number of messages to inspect (default 5 000).</param>
    /// <param name="progress">Optional progress callback: (fetched, total).</param>
    public async Task<IReadOnlyList<EmailGroup>> FetchGroupsAsync(
        bool groupByDomain,
        int maxMessages = 5_000,
        IProgress<(int Fetched, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        EnsureAuthenticated();

        var fromCounts = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;
        int fetched = 0;

        do
        {
            ct.ThrowIfCancellationRequested();

            // ---- List a page of message IDs ----
            var listReq = _svc!.Users.Messages.List("me");
            listReq.PageToken = pageToken;
            listReq.MaxResults = Math.Min(500, maxMessages - fetched);
            listReq.Fields = "nextPageToken,messages(id)";

            ListMessagesResponse listResp = await listReq.ExecuteAsync(ct);
            if (listResp.Messages is null) break;

            // ---- Batch-fetch From headers in chunks of 100 ----
            var ids = listResp.Messages.Select(m => m.Id).ToList();
            for (int offset = 0; offset < ids.Count; offset += 100)
            {
                ct.ThrowIfCancellationRequested();

                var chunk = ids.Skip(offset).Take(100).ToList();
                var tasks = chunk.Select(id => GetFromHeaderAsync(id, ct));
                string?[] headers = await Task.WhenAll(tasks);

                foreach (string? h in headers)
                {
                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        string addr = ExtractAddress(h);
                        fromCounts.AddOrUpdate(addr, 1, (_, v) => v + 1);
                    }
                }

                fetched += chunk.Count;
                progress?.Report((fetched, maxMessages));
            }

            pageToken = listResp.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && fetched < maxMessages);

        return groupByDomain ? BuildDomainGroups(fromCounts) : BuildAddressGroups(fromCounts);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<string?> GetFromHeaderAsync(string messageId, CancellationToken ct)
    {
        try
        {
            var req = _svc!.Users.Messages.Get("me", messageId);
            req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            req.MetadataHeaders = ["From"];
            req.Fields = "payload/headers";

            Message msg = await req.ExecuteAsync(ct);
            return msg.Payload?.Headers?
                .FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }
        catch (Google.GoogleApiException)
        {
            // Skip individual messages that are inaccessible or return API errors
            // (e.g. 403 Forbidden on specific messages, 404 Not Found).
            return null;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Transient network error for a single message — skip and continue.
            return null;
        }
        // All other exceptions (e.g. OperationCanceledException) propagate to the caller.
    }

    private static IReadOnlyList<EmailGroup> BuildAddressGroups(
        ConcurrentDictionary<string, long> fromCounts)
    {
        return fromCounts
            .Select(kv => new EmailGroup
            {
                Key = kv.Key,
                DisplayName = kv.Key,
                EmailCount = kv.Value,
                IsDomain = false,
            })
            .OrderByDescending(g => g.EmailCount)
            .ToList();
    }

    private static IReadOnlyList<EmailGroup> BuildDomainGroups(
        ConcurrentDictionary<string, long> fromCounts)
    {
        // Aggregate by domain
        var domains = new Dictionary<string, (long Total, List<EmailGroup> Subs)>(StringComparer.OrdinalIgnoreCase);

        foreach ((string addr, long count) in fromCounts)
        {
            string domain = ExtractDomain(addr);

            if (!domains.TryGetValue(domain, out var entry))
                entry = (0, []);

            domains[domain] = (
                entry.Total + count,
                [.. entry.Subs, new EmailGroup
                {
                    Key = addr,
                    DisplayName = addr,
                    EmailCount = count,
                    IsDomain = false,
                }]
            );
        }

        return domains
            .Select(kv => new EmailGroup
            {
                Key = kv.Key,
                DisplayName = kv.Key,
                EmailCount = kv.Value.Total,
                IsDomain = true,
                SubGroups = [.. kv.Value.Subs.OrderByDescending(s => s.EmailCount)],
            })
            .OrderByDescending(g => g.EmailCount)
            .ToList();
    }

    // ---- String parsing ----

    /// <summary>Extracts a bare email address from a "Display Name &lt;addr&gt;" or plain "addr" string.</summary>
    private static string ExtractAddress(string from)
    {
        Match m = AngleBracketRegex().Match(from);
        string addr = m.Success ? m.Groups[1].Value : from;
        return addr.Trim().ToLowerInvariant();
    }

    private static string ExtractDomain(string address)
    {
        int at = address.LastIndexOf('@');
        return at >= 0 ? address[(at + 1)..] : address;
    }

    [GeneratedRegex(@"<([^>]+)>", RegexOptions.Compiled)]
    private static partial Regex AngleBracketRegex();

    private void EnsureAuthenticated()
    {
        if (_svc is null)
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
    }
}
