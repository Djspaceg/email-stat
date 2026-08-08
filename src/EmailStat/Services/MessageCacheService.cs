namespace EmailStat.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Persists the message-id → from-address mapping to disk, encrypted with
/// Windows DPAPI (current-user scope).  Only the logged-in Windows user can
/// decrypt the file; no passwords or keys to manage.
/// </summary>
internal sealed class MessageCacheService
{
    // ── Storage paths ─────────────────────────────────────────────────────────
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmailStat", "cache");

    private static readonly string CacheFile  = Path.Combine(CacheDir, "messages.dat");
    private static readonly string StateFile  = Path.Combine(CacheDir, "state.json");

    // ── Entropy added to DPAPI so the ciphertext is app-specific ─────────────
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("EmailStat.MessageCache.v1");

    // ── In-memory state ───────────────────────────────────────────────────────

    /// <summary>messageId → bare from-address (lower-cased).</summary>
    public Dictionary<string, string> Messages { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The Gmail historyId recorded at the end of the last full (or incremental) sync.
    /// Null when no cache exists yet.
    /// </summary>
    public ulong? LastHistoryId { get; private set; }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and decrypts the cache from disk.
    /// Returns false (and leaves the instance empty) when no cache exists yet
    /// or the file cannot be decrypted (e.g. moved to a different user account).
    /// </summary>
    public bool TryLoad()
    {
        try
        {
            if (!File.Exists(CacheFile) || !File.Exists(StateFile))
                return false;

            byte[] cipher = File.ReadAllBytes(CacheFile);
            byte[] plain  = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);

            Messages = JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);

            var state = JsonSerializer.Deserialize<CacheState>(File.ReadAllText(StateFile));
            LastHistoryId = state?.HistoryId;

            return true;
        }
        catch
        {
            // Corrupt or unreadable cache — start fresh.
            Messages = new Dictionary<string, string>(StringComparer.Ordinal);
            LastHistoryId = null;
            return false;
        }
    }

    /// <summary>Encrypts and saves the current in-memory state to disk.</summary>
    public void Save(ulong historyId)
    {
        Directory.CreateDirectory(CacheDir);

        byte[] plain  = JsonSerializer.SerializeToUtf8Bytes(Messages);
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        // Write atomically via a temp file so a crash mid-write doesn't corrupt the cache.
        string tmp = CacheFile + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, CacheFile, overwrite: true);

        File.WriteAllText(StateFile, JsonSerializer.Serialize(new CacheState { HistoryId = historyId }));
        LastHistoryId = historyId;
    }

    /// <summary>Deletes all cached data and resets in-memory state.</summary>
    public void Clear()
    {
        Messages.Clear();
        LastHistoryId = null;
        if (File.Exists(CacheFile)) File.Delete(CacheFile);
        if (File.Exists(StateFile)) File.Delete(StateFile);
    }

    // ── Internal DTO ─────────────────────────────────────────────────────────

    private sealed class CacheState
    {
        [JsonPropertyName("historyId")]
        public ulong HistoryId { get; set; }
    }
}
