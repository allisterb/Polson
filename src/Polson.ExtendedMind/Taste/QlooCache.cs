namespace Polson.ExtendedMind.Taste;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// File-backed store of Qloo answers, keyed by the service and the canonical request together.
/// </summary>
/// <remarks>
/// <para>
/// <b>The raw body is stored, not the parsed result</b>, and a hit goes through the same parser as a fresh
/// answer — so a hit and a miss deliver the same thing, and there is one reader of Qloo's JSON rather than two.
/// </para>
/// <para>
/// <b>Shared across projects by default</b> (<see cref="DefaultDirectory"/>), unlike the document cache, which
/// lives in each project. A taste answer is about an audience, not about a project's files: the same preset run
/// in a fresh project asks the same questions, and on a 10,000-a-month key it should not ask Qloo twice. It is
/// also outside any repository on purpose — Qloo's terms allow private server-side caching for as long as we
/// like, and forbid storing its responses in a public repository.
/// </para>
/// <para>
/// No expiry. Taste moves slowly, and every hit carries <see cref="CachedQlooAnswer.RetrievedUtc"/>, so the age
/// of the evidence is shown rather than hidden.
/// </para>
/// </remarks>
public sealed class QlooCache : Runtime
{
    #region Constructors
    public QlooCache(string? directory = null)
    {
        this.directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        Directory.CreateDirectory(this.directory);
    }
    #endregion

    #region Properties
    public static string DefaultDirectory => Path.Combine(CamelDir, "qloo");

    public string Location => directory;
    #endregion

    #region Methods
    /// <summary>The content address of one request to one service. The key is never part of it.</summary>
    public static string KeyOf(string baseUrl, string request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl.TrimEnd('/') + "|" + request)))[..32];

    /// <summary>The stored answer, or null when nothing is held or the entry is unusable.</summary>
    public CachedQlooAnswer? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<CachedQlooAnswer>(File.ReadAllText(path));

            // An entry with no body is not an answer. Missing beats serving an empty one, which would read as
            // "this audience likes nothing".
            return string.IsNullOrWhiteSpace(stored?.Body) ? null : stored;
        }
        catch (Exception ex)
        {
            // A cache is an optimisation and must never be why a call fails. A bad entry is a miss, and the next
            // successful call rewrites it.
            Warn("Discarding unreadable Qloo cache entry {0}: {1}", key, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Stores an answer. Written beside the target and moved into place, so a reader never sees half an entry.
    /// Failure to write is logged and otherwise ignored.
    /// </summary>
    public void Put(string key, CachedQlooAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (string.IsNullOrWhiteSpace(answer.Body)) return;

        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(answer));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Warn("Could not write Qloo cache entry {0}: {1}", key, ex.Message);
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string PathFor(string key) => Path.Combine(directory, key + ".json");
    #endregion

    #region Fields
    private readonly string directory;
    #endregion
}

/// <summary>A Qloo answer as it is held between runs.</summary>
public sealed record CachedQlooAnswer
{
    /// <summary>The endpoint and canonical query, without the key — what was asked.</summary>
    public required string Request { get; init; }

    /// <summary>The response body exactly as Qloo sent it.</summary>
    public required string Body { get; init; }

    /// <summary>When Qloo answered, not when the answer was served.</summary>
    public required DateTime RetrievedUtc { get; init; }

    /// <summary>Qloo's request id for the original call, for quoting to Qloo.</summary>
    public string? RequestId { get; init; }
}
