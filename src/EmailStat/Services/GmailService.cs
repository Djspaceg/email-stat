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
    // ── Developer setup ──────────────────────────────────────────────────────
    // Register this app once in Google Cloud Console:
    //   1. console.cloud.google.com → New project
    //   2. APIs & Services → Library → enable "Gmail API"
    //   3. APIs & Services → Credentials → Create Credentials → OAuth client ID
    //      Application type: Desktop application
    //   4. Download the JSON file and save it as:
    //        src\EmailStat\client_secret.json
    //      This file is listed in .gitignore and must NOT be committed to source control.
    // ─────────────────────────────────────────────────────────────────────────

    // Path to the OAuth client secret file downloaded from Google Cloud Console.
    // Resolved relative to the app's base directory so it works both from the
    // IDE (project output) and when the binary is run directly.
    private static readonly string ClientSecretPath = Path.Combine(
        AppContext.BaseDirectory, "client_secret.json");

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
    /// Runs the OAuth 2.0 "installed app" flow using <c>client_secret.json</c>.
    /// Opens the system browser for the Google consent screen on first run.
    /// Tokens are cached in <c>%LOCALAPPDATA%\EmailStat\token\</c> for subsequent runs.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <c>client_secret.json</c> is not found next to the executable.
    /// Download it from Google Cloud Console and place it in the project directory
    /// with its Build Action set to "Content" and "Copy to Output Directory" enabled.
    /// </exception>
    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ClientSecretPath))
        {
            throw new FileNotFoundException(
                "client_secret.json not found. Download it from Google Cloud Console " +
                "(APIs & Services → Credentials → your OAuth 2.0 Client ID → Download JSON) " +
                $"and place it at: {ClientSecretPath}",
                ClientSecretPath);
        }

        GoogleClientSecrets googleSecrets;
        await using (var stream = File.OpenRead(ClientSecretPath))
            googleSecrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

        string tokenFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailStat", "token");

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            googleSecrets.Secrets,
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
    /// Fetches up to <paramref name="maxMessages"/> messages from the user's <b>Inbox</b>,
    /// extracts the From header, and returns groups sorted by email count descending.
    /// Only messages with the INBOX label are counted; sent, archived, and spam messages
    /// are excluded so the treemap reflects actual inbox composition.
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
            listReq.LabelIds = new Google.Apis.Util.Repeatable<string>(new[] { "INBOX" });
            listReq.Fields = "nextPageToken,messages(id)";

            ListMessagesResponse listResp = await listReq.ExecuteAsync(ct);
            if (listResp.Messages is null) break;

            // ---- Batch-fetch From headers in chunks of 100, throttled ----
                    var ids = listResp.Messages.Select(m => m.Id).ToList();
                    for (int offset = 0; offset < ids.Count; offset += 100)
                    {
                        ct.ThrowIfCancellationRequested();

                        var chunk = ids.Skip(offset).Take(100).ToList();

                        // Throttle to avoid hitting Gmail's QPM limit.
                        // Process in small parallel groups with a delay between each group.
                        const int concurrency = 5;
                        for (int i = 0; i < chunk.Count; i += concurrency)
                        {
                            ct.ThrowIfCancellationRequested();
                            var group = chunk.Skip(i).Take(concurrency).ToList();
                            var tasks = group.Select(id => GetFromHeaderAsync(id, ct));
                            string?[] headers = await Task.WhenAll(tasks);

                            foreach (string? h in headers)
                            {
                                if (!string.IsNullOrWhiteSpace(h))
                                {
                                    string addr = ExtractAddress(h);
                                    fromCounts.AddOrUpdate(addr, 1, (_, v) => v + 1);
                                }
                            }

                            fetched += group.Count;
                            progress?.Report((fetched, maxMessages));

                            // Brief pause between groups to stay well under the QPM ceiling.
                            if (i + concurrency < chunk.Count)
                                await Task.Delay(200, ct);
                        }
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
        int delayMs = 500;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var req = _svc!.Users.Messages.Get("me", messageId);
                req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                req.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(new[] { "From" });
                req.Fields = "payload/headers";

                Message msg = await req.ExecuteAsync(ct);
                return msg.Payload?.Headers?
                    .FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests
                                                    || (int)ex.HttpStatusCode == 429
                                                    || ex.Error?.Code == 403)
            {
                if (attempt == 4) return null;
                await Task.Delay(delayMs, ct);
                delayMs *= 2; // exponential backoff
            }
            catch (Google.GoogleApiException)
            {
                return null;
            }
            catch (System.Net.Http.HttpRequestException)
            {
                return null;
            }
        }
        return null;
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
        // Aggregate by domain — mutate the existing sub-list to avoid O(n²) allocations.
        var domains = new Dictionary<string, (long Total, List<EmailGroup> Subs)>(StringComparer.OrdinalIgnoreCase);

        foreach ((string addr, long count) in fromCounts)
        {
            string domain = ExtractDomain(addr);

            if (!domains.TryGetValue(domain, out var entry))
            {
                entry = (0, []);
                domains[domain] = entry;
            }

            entry.Subs.Add(new EmailGroup
            {
                Key = addr,
                DisplayName = addr,
                EmailCount = count,
                IsDomain = false,
            });
            domains[domain] = (entry.Total + count, entry.Subs);
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
