using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Slate.Server.Data;
using Slate.Server.Notes;
using Slate.Server.Storage;

namespace Slate.Server.Search;

/// <summary>
/// Full-text search over a vault's notes. Postgres never stores note bodies (see the design spec's
/// "no note bodies in Postgres" principle), so this is necessarily two steps: rank candidate notes
/// cheaply via the GIN-indexed <c>search_vector</c> column, then for each match read the note's
/// content off disk (via <see cref="IVaultStorage"/>) to build a <c>ts_headline</c> snippet - the
/// original text needed for highlighting was never persisted server-side.
///
/// Raw ADO.NET (rather than EF LINQ) is used here because the ranking/highlighting logic is
/// Postgres-function-specific (<c>websearch_to_tsquery</c>, <c>ts_rank</c>, <c>ts_headline</c>) with
/// no LINQ equivalent; parameters are always passed as <see cref="NpgsqlParameter"/> values (never
/// string-interpolated into SQL), so this carries no injection risk despite being raw SQL.
/// </summary>
public class SearchService
{
    private readonly SlateDbContext _db;
    private readonly IVaultStorage _storage;

    public SearchService(SlateDbContext db, IVaultStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(Guid vaultId, string query, CancellationToken cancellationToken)
    {
        var matches = new List<(Guid Id, string Path, string Title, double Score)>();

        // Database.Open/CloseConnectionAsync (rather than manipulating the raw DbConnection
        // directly) are reference-counted, so this composes safely with EF's own connection
        // management for the rest of the request.
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = _db.Database.GetDbConnection();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, path, title, ts_rank(search_vector, websearch_to_tsquery('english', @q)) AS score
                    FROM notes
                    WHERE vault_id = @v AND is_deleted = false AND search_vector @@ websearch_to_tsquery('english', @q)
                    ORDER BY score DESC
                    LIMIT 50
                    """;
                command.Parameters.Add(new NpgsqlParameter("v", vaultId));
                command.Parameters.Add(new NpgsqlParameter("q", query));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    matches.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
                }
            }

            var results = new List<SearchResultDto>(matches.Count);
            foreach (var match in matches)
            {
                var snippet = await BuildSnippetAsync(connection, vaultId, match.Path, query, cancellationToken);
                results.Add(new SearchResultDto(match.Id, match.Path, match.Title, snippet, match.Score));
            }

            return results;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Builds a highlighted snippet for one matched note by reading its content off disk (the
    /// tsvector index alone can't produce a highlight - Postgres needs the original text) and
    /// running <c>ts_headline</c> against it with the same search query.
    /// </summary>
    private async Task<string> BuildSnippetAsync(DbConnection connection, Guid vaultId, string path, string query, CancellationToken cancellationToken)
    {
        string plainText;
        try
        {
            var content = await _storage.ReadNoteAsync(vaultId, path, cancellationToken);
            plainText = MarkdownIndexer.StripToPlainText(content);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts_headline('english', @text, websearch_to_tsquery('english', @q),
                'StartSel=<mark>, StopSel=</mark>, MaxFragments=1, MaxWords=35, MinWords=15')
            """;
        command.Parameters.Add(new NpgsqlParameter("text", plainText));
        command.Parameters.Add(new NpgsqlParameter("q", query));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? string.Empty;
    }
}
