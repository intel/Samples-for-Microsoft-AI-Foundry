using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TechnicianAssistant.Services.Interfaces;

namespace TechnicianAssistant.Services;

/// <summary>
/// Manages vector search over technical manuals stored in SQLite with vec0 extension.
/// Uses embeddings to find relevant manual sections for user queries.
/// </summary>
public class VectorDatabaseService : IDisposable
{
    private readonly IEmbeddingService _embeddingService;
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private Action<string>? _logger;
    private bool _disposed;

    public class SearchResult
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public string ManualName { get; set; } = string.Empty;
        public int PageNumber { get; set; }
        public float Similarity { get; set; }
    }

    public VectorDatabaseService(IEmbeddingService embeddingService, string databasePath)
    {
        _embeddingService = embeddingService;
        _databasePath = databasePath;

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"Database file not found: {databasePath}");
        }

        InitializeConnection();
        Log($"? VectorDatabaseService initialized with: {Path.GetFileName(databasePath)}");
    }

    public void SetLogger(Action<string> logger)
    {
        _logger = logger;
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        _logger?.Invoke(message + "\n");
    }

    private void InitializeConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // Load vec0 extension if needed
        LoadVec0Extension();
    }

    private void LoadVec0Extension()
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version()";
            var version = cmd.ExecuteScalar()?.ToString();
            Log($"?? SQLite version: {version}");

            // Enable extension loading
            _connection.EnableExtensions(true);
            
            // Try to find and load vec0 extension
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vec0.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sqlite_vec.dll"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vec0"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sqlite-vec", "vec0.dll"),
                "vec0",
                "sqlite_vec"
            };

            bool extensionLoaded = false;
            foreach (var path in possiblePaths)
            {
                try
                {
                    cmd.CommandText = $"SELECT load_extension('{path.Replace("\\", "\\\\")}')";
                    cmd.ExecuteNonQuery();
                    Log($"? Loaded vec0 extension from: {path}");
                    extensionLoaded = true;
                    break;
                }
                catch
                {
                    // Try next path
                }
            }

            if (!extensionLoaded)
            {
                Log("?? WARNING: vec0 extension not found!");
                Log("   Vector similarity search will not work.");
                Log("   Please install the sqlite-vec extension:");
                Log("   1. Download from: https://github.com/asg017/sqlite-vec");
                Log("   2. Place vec0.dll in the application directory");
                Log("   3. Restart the application");
            }
        }
        catch (Exception ex)
        {
            Log($"?? Error loading vec0 extension: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches the manual database for chunks most similar to the query.
    /// When <paramref name="manualNameFilter"/> is provided only chunks from those
    /// manuals are considered — call <see cref="GetMatchingManualNamesAsync"/> first
    /// to resolve the relevant manuals for the current equipment.
    /// </summary>
    public async Task<SearchResult[]> SearchAsync(
        string query,
        int topK = 3,
        float minSimilarity = 0.3f,
        string[]? manualNameFilter = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        Log($"?? Searching manuals database for: \"{query.Substring(0, Math.Min(50, query.Length))}...\"");
        if (manualNameFilter is { Length: > 0 })
            Log($"   ?? Scoped to {manualNameFilter.Length} manual(s): {string.Join(", ", manualNameFilter)}");

        try
        {
            var queryEmbedding = await Task.Run(() => _embeddingService.GetEmbedding(query));
            var embeddingBytes = ConvertEmbeddingToBytes(queryEmbedding);

            var results = new List<SearchResult>();
            using var cmd = _connection!.CreateCommand();

            // Build the optional manual-name filter clause dynamically.
            string manualFilterClause = string.Empty;
            if (manualNameFilter is { Length: > 0 })
            {
                var paramNames = manualNameFilter
                    .Select((_, i) => $"@m{i}")
                    .ToList();
                manualFilterClause = $"AND t.manual_name IN ({string.Join(", ", paramNames)})";
                for (int i = 0; i < manualNameFilter.Length; i++)
                    cmd.Parameters.AddWithValue($"@m{i}", manualNameFilter[i]);
            }

            cmd.CommandText = $@"
                SELECT id, content, manual_name, page_num, similarity
                FROM (
                    SELECT
                        t.id,
                        t.content,
                        t.manual_name,
                        t.page_num,
                        (1.0 - vec_distance_cosine(v.embedding, @embedding)) AS similarity
                    FROM vec_manuals v
                    INNER JOIN text_metadata t ON v.chunk_id = t.id
                    WHERE 1=1 {manualFilterClause}
                )
                WHERE similarity >= @minSimilarity
                ORDER BY similarity DESC
                LIMIT @topK";

            cmd.Parameters.AddWithValue("@embedding", embeddingBytes);
            cmd.Parameters.AddWithValue("@minSimilarity", minSimilarity);
            cmd.Parameters.AddWithValue("@topK", topK);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new SearchResult
                {
                    Id         = reader.GetInt32(0),
                    Content    = reader.GetString(1),
                    ManualName = reader.GetString(2),
                    PageNumber = reader.GetInt32(3),
                    Similarity = reader.GetFloat(4)
                });
            }

            Log($"? Found {results.Count} relevant chunk(s):");
            foreach (var result in results.Take(3))
            {
                var preview = result.Content.Length > 60
                    ? result.Content.Substring(0, 60) + "..."
                    : result.Content;
                Log($"   [{result.ManualName} p.{result.PageNumber}] Similarity: {result.Similarity:F3} - {preview}");
            }

            return results.ToArray();
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such module") || ex.Message.Contains("vec0"))
        {
            Log($"?? Vector DB error: {ex.Message}");
            Log("   The vec0/sqlite-vec extension is not loaded.");
            Log("   Vector search is unavailable - falling back to knowledge base.");
            return Array.Empty<SearchResult>();
        }
        catch (Exception ex)
        {
            Log($"? Vector DB error: {ex.Message}");
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// Returns the distinct manual names whose stored content chunks contain
    /// <paramref name="modelNumber"/> as a literal substring (case-insensitive).
    /// This is the reliable way to discover which manuals cover a specific model —
    /// the model number will appear in compatibility tables, spec pages, etc.
    /// Returns an empty array when no manual mentions the model number at all.
    /// </summary>
    public async Task<string[]> FindManualsContainingModelAsync(string modelNumber)
    {
        if (string.IsNullOrWhiteSpace(modelNumber))
            return Array.Empty<string>();

        var manuals = new List<string>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT manual_name
            FROM text_metadata
            WHERE content LIKE @pattern
            ORDER BY manual_name";
        cmd.Parameters.AddWithValue("@pattern", $"%{modelNumber}%");

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            manuals.Add(reader.GetString(0));

        return manuals.ToArray();
    }

    /// <summary>
    /// Returns the subset of manual names in the database whose name contains
    /// any token from <paramref name="queryOrModelNumber"/> after normalisation
    /// (strips hyphens/spaces/underscores, case-insensitive).
    /// Works for both short model numbers ("APX-36") and full query sentences
    /// ("The Carrier unit is showing 1 Red Flash").
    /// Returns an empty array when no manuals match — callers should treat this as
    /// "no service manual available for this equipment brand/model".
    /// </summary>
    public async Task<string[]> GetMatchingManualNamesAsync(string queryOrModelNumber)
    {
        if (string.IsNullOrWhiteSpace(queryOrModelNumber))
            return Array.Empty<string>();

        var allManuals = await GetManualNamesAsync();

        // Split on all common separators so both model numbers and free-text queries work.
        var rawTokens = queryOrModelNumber
            .Split(['-', '_', ' ', '.', ',', '?', '!', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

        var searchTokens = rawTokens
            .Select(Normalize)
            .Append(Normalize(queryOrModelNumber))   // also try the full string normalised
            .Where(t => t.Length >= 3)               // skip single chars and 2-char noise words
            .Where(t => !_stopwords.Contains(t))     // skip common English/HVAC words
            .Distinct()
            .Take(30)                                // cap to avoid O(n²) on very long queries
            .ToArray();

        var matches = allManuals
            .Where(name =>
            {
                var normalName = Normalize(name);
                return searchTokens.Any(token => normalName.Contains(token));
            })
            .ToArray();

        return matches;
    }

    /// <summary>Strips separators and uppercases for fuzzy model-number matching.</summary>
    private static string Normalize(string s) =>
        s.Replace("-", "").Replace("_", "").Replace(" ", "").ToUpperInvariant();

    /// <summary>
    /// Common English and HVAC-technical words that must never be used as
    /// brand/model identifier tokens. Without this list, words like "manual"
    /// or "service" (which appear in every manual filename) cause false matches.
    /// </summary>
    private static readonly IReadOnlySet<string> _stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Common English
        "the","and","for","with","this","that","are","was","has","have","been",
        "will","would","could","should","may","might","but","not","all","can",
        "its","use","did","his","her","our","out","one","had","how","who","what",
        "when","where","why","based","first","likely","causes","check","show",
        "showing","just","also","some","each","make","like","time","look","more",
        "from","into","then","than","your","over","such","about","after","before",
        "between","both","get","give","good","need","new","old","other","same",
        "see","take","those","through","two","want","way","well","while","work",
        // HVAC / document vocabulary (these appear in manual filenames!)
        "manual","service","unit","model","system","guide","tech","series",
        "install","installation","operation","maintenance","repair","board",
        "control","panel","press","high","low","heat","cool","flash","red","led",
        "pdf","rev","spec","specs","ver","vol","doc","ref","rev",
    };

    /// <summary>
    /// Finds the single most relevant chunk from the manuals database.
    /// </summary>
    public async Task<string> FindMostRelevantChunkAsync(string query, float minSimilarity = 0.3f)
    {
        var results = await SearchAsync(query, topK: 1, minSimilarity);

        if (results.Length > 0)
        {
            var best = results[0];
            Log($"? Best match from manual: {best.ManualName} (page {best.PageNumber})");
            Log($"   Similarity: {best.Similarity:F3}");
            return best.Content;
        }

        Log($"?? No relevant manual content found (threshold: {minSimilarity:F3})");
        return string.Empty;
    }

    /// <summary>
    /// Finds top N relevant chunks and concatenates them as context.
    /// Useful for RAG with multiple relevant sections.
    /// </summary>
    public async Task<string> FindRelevantContextAsync(string query, int topK = 3, float minSimilarity = 0.3f)
    {
        var results = await SearchAsync(query, topK, minSimilarity);

        if (results.Length == 0)
        {
            return string.Empty;
        }

        var contextParts = results.Select((r, idx) => 
            $"[Source: {r.ManualName}, Page {r.PageNumber}]\n{r.Content}"
        );

        return string.Join("\n\n---\n\n", contextParts);
    }

    /// <summary>
    /// Gets total number of chunks in the database.
    /// </summary>
    public async Task<int> GetChunkCountAsync()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM text_metadata";
        var count = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(count);
    }

    /// <summary>
    /// Gets list of all manuals in the database.
    /// </summary>
    public async Task<string[]> GetManualNamesAsync()
    {
        var manuals = new List<string>();
        
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT manual_name FROM text_metadata ORDER BY manual_name";
        
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            manuals.Add(reader.GetString(0));
        }

        return manuals.ToArray();
    }

    /// <summary>
    /// Searches within a specific manual only.
    /// </summary>
    public async Task<SearchResult[]> SearchInManualAsync(string query, string manualName, int topK = 3, float minSimilarity = 0.3f)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        var queryEmbedding = await Task.Run(() => _embeddingService.GetEmbedding(query));
        var embeddingBytes = ConvertEmbeddingToBytes(queryEmbedding);

        var results = new List<SearchResult>();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT id, content, manual_name, page_num, similarity
            FROM (
                SELECT 
                    t.id,
                    t.content,
                    t.manual_name,
                    t.page_num,
                    (1.0 - vec_distance_cosine(v.embedding, @embedding)) AS similarity
                FROM vec_manuals v
                INNER JOIN text_metadata t ON v.chunk_id = t.id
                WHERE t.manual_name = @manualName
            )
            WHERE similarity >= @minSimilarity
            ORDER BY similarity DESC
            LIMIT @topK";

        cmd.Parameters.AddWithValue("@embedding", embeddingBytes);
        cmd.Parameters.AddWithValue("@manualName", manualName);
        cmd.Parameters.AddWithValue("@minSimilarity", minSimilarity);
        cmd.Parameters.AddWithValue("@topK", topK);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SearchResult
            {
                Id = reader.GetInt32(0),
                Content = reader.GetString(1),
                ManualName = reader.GetString(2),
                PageNumber = reader.GetInt32(3),
                Similarity = reader.GetFloat(4)
            });
        }

        return results.ToArray();
    }

    /// <summary>
    /// Converts float[] embedding to byte[] for SQLite vec0.
    /// </summary>
    private byte[] ConvertEmbeddingToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;
    }
}
