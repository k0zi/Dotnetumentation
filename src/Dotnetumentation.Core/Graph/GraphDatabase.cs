using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Dotnetumentation.Core.Graph;

/// <summary>
/// The code graph as an SQLite database (<c>graph.db</c>): writing it deterministically, merging project graphs and the
/// queries of the search. The reading methods return file and document paths as full paths.
/// </summary>
public sealed partial class GraphDatabase : IDisposable
{
    public const string FileName = "graph.db";
    public const string SolutionFileSuffix = ".graph.db";
    public const int SchemaVersion = 1;

    /// <summary>The schema, also shown in <c>graph.md</c>.</summary>
    public const string Schema = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE nodes (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            full_name TEXT NOT NULL,
            project TEXT,
            namespace TEXT,
            visibility TEXT,
            signature TEXT,
            declaration TEXT,
            summary TEXT,
            file TEXT,
            line INTEGER,
            is_external INTEGER NOT NULL,
            is_stub INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX nodes_by_kind ON nodes (kind);
        CREATE INDEX nodes_by_name ON nodes (name COLLATE NOCASE);
        CREATE TABLE edges (
            source TEXT NOT NULL,
            kind TEXT NOT NULL,
            target TEXT NOT NULL,
            label TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (source, kind, target, label)
        ) WITHOUT ROWID;
        CREATE INDEX edges_by_target ON edges (target, kind);
        CREATE TABLE documents (
            node TEXT NOT NULL,
            kind TEXT NOT NULL,
            path TEXT NOT NULL,
            PRIMARY KEY (node, kind, path)
        ) WITHOUT ROWID;
        CREATE VIRTUAL TABLE search USING fts5 (id UNINDEXED, name, words, full_name, summary);
        """;

    private readonly SqliteConnection _connection;
    private readonly string _directory;

    private GraphDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _directory = System.IO.Path.GetDirectoryName(Path)!;
        _connection = new SqliteConnection(ConnectionString(Path, SqliteOpenMode.ReadOnly));
        _connection.Open();
    }

    public string Path { get; }

    public static GraphDatabase Open(string path)
    {
        if (!System.IO.File.Exists(path))
            throw new FileNotFoundException($"Graph database not found: {path}", path);
        return new GraphDatabase(path);
    }

    public void Dispose() => _connection.Dispose();

    // Pooling would keep the file open after the connection is closed, so it could not be replaced.
    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    /// <summary>
    /// Writes the graph; the same graph always gives the same bytes. The file is only replaced when its content
    /// changes: false means it was up to date.
    /// </summary>
    public static bool Write(string path, CodeGraph graph)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        System.IO.File.Delete(temporary);
        try
        {
            using (var connection = new SqliteConnection(ConnectionString(temporary, SqliteOpenMode.ReadWriteCreate)))
            {
                connection.Open();
                Execute(connection, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;");
                Execute(connection, Schema);
                using var transaction = connection.BeginTransaction();
                Insert(connection, "INSERT INTO meta VALUES ($1, $2)",
                    graph.Meta.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new object?[] { m.Key, m.Value }));
                Insert(connection, "INSERT INTO nodes VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)",
                    graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => new object?[]
                    {
                        n.Id, n.Kind, n.Name, n.FullName, n.Project, n.Namespace, n.Visibility, n.Signature, n.Declaration,
                        n.Summary, n.File, n.Line, n.IsExternal ? 1 : 0, n.IsStub ? 1 : 0,
                    }));
                Insert(connection, "INSERT INTO edges VALUES ($1, $2, $3, $4)",
                    graph.Edges.Distinct()
                        .OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal)
                        .ThenBy(e => e.Target, StringComparer.Ordinal).ThenBy(e => e.Label, StringComparer.Ordinal)
                        .Select(e => new object?[] { e.Source, e.Kind, e.Target, e.Label }));
                Insert(connection, "INSERT INTO documents VALUES ($1, $2, $3)",
                    graph.Documents.Distinct()
                        .OrderBy(d => d.Node, StringComparer.Ordinal).ThenBy(d => d.Kind, StringComparer.Ordinal)
                        .ThenBy(d => d.Path, StringComparer.Ordinal)
                        .Select(d => new object?[] { d.Node, d.Kind, d.Path }));
                Insert(connection, "INSERT INTO search VALUES ($1, $2, $3, $4, $5)",
                    graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal)
                        .Select(n => new object?[] { n.Id, n.Name, Words(n.Name), n.FullName, n.Summary ?? "" }));
                transaction.Commit();
            }

            if (System.IO.File.Exists(path) && System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(temporary)))
                return false;
            System.IO.File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            System.IO.File.Delete(temporary);
        }
    }

    /// <summary>
    /// The solution graph: the project graphs in one database. A node defined in one project and referenced by others
    /// keeps the full row of its own project; paths are rebased to the folder of <paramref name="path"/>.
    /// </summary>
    public static bool Merge(string path, IEnumerable<string> sources, IReadOnlyDictionary<string, string> meta)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new HashSet<GraphEdge>();
        var documents = new HashSet<GraphDocument>();
        foreach (var source in sources)
        {
            var sourceDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(source))!;
            string Rebase(string relative) =>
                System.IO.Path.GetRelativePath(directory, System.IO.Path.GetFullPath(System.IO.Path.Combine(sourceDirectory, relative))).Replace('\\', '/');

            var graph = Read(source);
            foreach (var node in graph.Nodes)
            {
                var rebased = node.File is null ? node : node with { File = Rebase(node.File) };
                if (!nodes.TryGetValue(node.Id, out var existing) || existing.IsStub && !node.IsStub)
                    nodes[node.Id] = rebased;
            }
            edges.UnionWith(graph.Edges);
            documents.UnionWith(graph.Documents.Select(d => d with { Path = Rebase(d.Path) }));
        }
        return Write(path, new CodeGraph(meta, nodes.Values.ToList(), edges.ToList(), documents.ToList()));
    }

    /// <summary>Everything in the database, with the paths as stored (relative to its folder).</summary>
    public static CodeGraph Read(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        var meta = Query(connection, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(m => m.Item1, m => m.Item2);
        var nodes = Query(connection, $"SELECT {NodeColumns} FROM nodes", r => ReadNode(r, 0));
        var edges = Query(connection, "SELECT source, target, kind, label FROM edges",
            r => new GraphEdge(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        var documents = Query(connection, "SELECT node, kind, path FROM documents", r => new GraphDocument(r.GetString(0), r.GetString(1), r.GetString(2)));
        return new CodeGraph(meta, nodes, edges, documents);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, string sql, IEnumerable<object?[]> rows)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameters = new List<SqliteParameter>();
        foreach (var row in rows)
        {
            if (parameters.Count == 0)
            {
                for (var i = 0; i < row.Length; i++)
                    parameters.Add(command.Parameters.Add(new SqliteParameter($"${i + 1}", null)));
                command.Prepare();
            }
            for (var i = 0; i < row.Length; i++)
                parameters[i].Value = row[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The parts of a camel case name, so <c>OrderService</c> is found by <c>service</c> too.</summary>
    public static string Words(string name) => string.Join(' ', CamelHumps().Split(name).Where(w => w.Length > 0));

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[^\p{L}\p{N}]+")]
    private static partial Regex CamelHumps();

    private const string NodeColumns = "id, kind, name, full_name, project, namespace, visibility, signature, declaration, summary, file, line, is_external, is_stub";

    public IReadOnlyDictionary<string, string> Meta() =>
        Query(_connection, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(m => m.Item1, m => m.Item2);

    /// <summary>
    /// Nodes whose name, name parts, full name or summary contain words starting with the words of <paramref name="text"/>;
    /// exact name matches first, then defined nodes before stubs. <paramref name="kinds"/> limits the node kinds.
    /// </summary>
    public IReadOnlyList<GraphNode> Search(string text, IReadOnlyCollection<string>? kinds = null, int limit = 50)
    {
        var words = SearchWords().Matches(text).Select(m => $"\"{m.Value}\"*").ToList();
        if (words.Count == 0)
            return [];
        var kindFilter = kinds is { Count: > 0 } ? $"AND n.kind IN ({string.Join(", ", kinds.Select((_, i) => $"$kind{i}"))})" : "";
        return Query(_connection, $"""
            SELECT {string.Join(", ", NodeColumns.Split(", ").Select(c => "n." + c))}
            FROM search JOIN nodes n ON n.id = search.id
            WHERE search MATCH $query {kindFilter}
            ORDER BY lower(n.name) = lower($text) DESC, n.name LIKE $text || '%' DESC, n.is_stub, search.rank, n.full_name
            LIMIT $limit
            """, r => Resolve(ReadNode(r, 0)), command =>
        {
            command.Parameters.AddWithValue("$query", string.Join(' ', words));
            command.Parameters.AddWithValue("$text", text.Trim());
            command.Parameters.AddWithValue("$limit", limit);
            foreach (var (kind, i) in (kinds ?? []).Select((k, i) => (k, i)))
                command.Parameters.AddWithValue($"$kind{i}", kind);
        });
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex SearchWords();

    public GraphNode? Node(string id) =>
        Query(_connection, $"SELECT {NodeColumns} FROM nodes WHERE id = $id", r => Resolve(ReadNode(r, 0)),
            c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    /// <summary>The edges from and to the node, with the node at the other end.</summary>
    public IReadOnlyList<GraphRelation> Relations(string id)
    {
        var columns = string.Join(", ", NodeColumns.Split(", ").Select(c => "n." + c));
        GraphRelation Relation(SqliteDataReader r, bool outgoing)
        {
            var edge = new GraphEdge(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            var otherId = outgoing ? edge.Target : edge.Source;
            var other = r.IsDBNull(4) ? new GraphNode { Id = otherId, Kind = "", Name = otherId, FullName = otherId, IsStub = true } : Resolve(ReadNode(r, 4));
            return new GraphRelation(edge, outgoing, other);
        }
        var outgoing = Query(_connection, $"SELECT e.source, e.target, e.kind, e.label, {columns} FROM edges e LEFT JOIN nodes n ON n.id = e.target WHERE e.source = $id",
            r => Relation(r, true), c => c.Parameters.AddWithValue("$id", id));
        var incoming = Query(_connection, $"SELECT e.source, e.target, e.kind, e.label, {columns} FROM edges e LEFT JOIN nodes n ON n.id = e.source WHERE e.target = $id",
            r => Relation(r, false), c => c.Parameters.AddWithValue("$id", id));
        return [.. outgoing, .. incoming];
    }

    /// <summary>The documentation pages and diagrams of the node, with full paths.</summary>
    public IReadOnlyList<GraphDocument> Documents(string id) =>
        Query(_connection, "SELECT node, kind, path FROM documents WHERE node = $id ORDER BY kind, path",
            r => new GraphDocument(r.GetString(0), r.GetString(1), FullPath(r.GetString(2))), c => c.Parameters.AddWithValue("$id", id));

    private GraphNode Resolve(GraphNode node) => node.File is null ? node : node with { File = FullPath(node.File) };

    private string FullPath(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(_directory, relative));

    private static GraphNode ReadNode(SqliteDataReader r, int i) => new()
    {
        Id = r.GetString(i),
        Kind = r.GetString(i + 1),
        Name = r.GetString(i + 2),
        FullName = r.GetString(i + 3),
        Project = r.IsDBNull(i + 4) ? null : r.GetString(i + 4),
        Namespace = r.IsDBNull(i + 5) ? null : r.GetString(i + 5),
        Visibility = r.IsDBNull(i + 6) ? null : r.GetString(i + 6),
        Signature = r.IsDBNull(i + 7) ? null : r.GetString(i + 7),
        Declaration = r.IsDBNull(i + 8) ? null : r.GetString(i + 8),
        Summary = r.IsDBNull(i + 9) ? null : r.GetString(i + 9),
        File = r.IsDBNull(i + 10) ? null : r.GetString(i + 10),
        Line = r.IsDBNull(i + 11) ? null : r.GetInt32(i + 11),
        IsExternal = r.GetInt32(i + 12) != 0,
        IsStub = r.GetInt32(i + 13) != 0,
    };

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, Action<SqliteCommand>? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        parameters?.Invoke(command);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(read(reader));
        return result;
    }
}
