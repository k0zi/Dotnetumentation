namespace Dotnetumentation.Core.Graph;

/// <summary>
/// Searches one or more graph databases as one: the solution graph, or the project graphs when there is none. A node
/// found in several databases is the defined one, not a stub.
/// </summary>
public sealed class GraphIndex : IDisposable
{
    /// <summary>Folders that never hold documentation; skipping them keeps the scan fast.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "artifacts", "TestResults",
    };

    private GraphIndex(IReadOnlyList<GraphDatabase> databases) => Databases = databases;

    public IReadOnlyList<GraphDatabase> Databases { get; }

    public static GraphIndex Open(IEnumerable<string> paths)
    {
        var databases = new List<GraphDatabase>();
        try
        {
            foreach (var path in paths)
                databases.Add(GraphDatabase.Open(path));
            return new GraphIndex(databases);
        }
        catch
        {
            foreach (var database in databases)
                database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var database in Databases)
            database.Dispose();
    }

    /// <summary>
    /// The graph databases of a <c>.db</c> file, a solution or project file, a documentation folder or any folder: the
    /// solution graph (<c>Name.graph.db</c>) when there is one, otherwise every <c>graph.db</c> of the documentation folders below.
    /// </summary>
    public static IReadOnlyList<string> Locate(string path, string outputDirectoryName = GeneratorOptions.DefaultOutputDirectoryName)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return [path];
            var solutionGraph = Path.ChangeExtension(path, null) + GraphDatabase.SolutionFileSuffix;
            if (File.Exists(solutionGraph))
                return [solutionGraph];
            path = Path.GetDirectoryName(path)!;
        }
        if (!Directory.Exists(path))
            return [];

        var solutionGraphs = Directory.EnumerateFiles(path, "*" + GraphDatabase.SolutionFileSuffix).Order(StringComparer.Ordinal).ToList();
        if (solutionGraphs.Count > 0)
            return solutionGraphs;
        var own = Path.Combine(path, GraphDatabase.FileName);
        if (File.Exists(own))
            return [own];

        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(pending.Pop()).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Equals(outputDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(child, GraphDatabase.FileName)))
                        found.Add(Path.Combine(child, GraphDatabase.FileName));
                }
                else if (!SkippedFolders.Contains(name) && !name.StartsWith('.'))
                    pending.Push(child);
            }
        }
        return found.Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<GraphNode> Search(string text, IReadOnlyCollection<string>? kinds = null, int limit = 50)
    {
        if (Databases.Count == 1)
            return Databases[0].Search(text, kinds, limit);
        var trimmed = text.Trim();
        return Databases.SelectMany(d => d.Search(text, kinds, limit))
            .GroupBy(n => n.Id).Select(g => g.OrderBy(n => n.IsStub).First())
            .OrderByDescending(n => n.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.IsStub)
            .ThenBy(n => n.FullName, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public GraphNode? Node(string id) =>
        Databases.Select(d => d.Node(id)).OfType<GraphNode>().OrderBy(n => n.IsStub).FirstOrDefault();

    /// <summary>The relations of the node in every database; the other end is the defined node where one exists.</summary>
    public IReadOnlyList<GraphRelation> Relations(string id)
    {
        var relations = Databases.SelectMany(d => d.Relations(id)).DistinctBy(r => (r.Edge, r.IsOutgoing)).ToList();
        if (Databases.Count == 1)
            return relations;
        return relations.Select(r => r.Other.IsStub && Node(r.Other.Id) is { IsStub: false } defined ? r with { Other = defined } : r).ToList();
    }

    public IReadOnlyList<GraphDocument> Documents(string id) => Databases.SelectMany(d => d.Documents(id)).Distinct().ToList();
}
