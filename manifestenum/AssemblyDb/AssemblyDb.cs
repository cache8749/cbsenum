// Translation of AssemblyDb.pas — top-level database class

namespace ManifestEnum;

/// <summary>
/// Top-level assembly database — translation of <c>TAssemblyDb</c>.
/// Owns and exposes all DB modules.
/// </summary>
public class AssemblyDb : AssemblyDbCore
{
    // ---- Modules (public, matches the Delphi field names) ----
    public readonly AssemblyAssemblies Assemblies;
    public readonly AssemblyFiles      Files;
    public readonly AssemblyRegistry   Registry;
    public readonly AssemblyServices   Services;

    public AssemblyDb()
    {
        // Modules must be created BEFORE Open() so they register for CreateTables/InitStatements
        Assemblies = AddModule(new AssemblyAssemblies(this));
        Files      = AddModule(new AssemblyFiles(this));
        Registry   = AddModule(new AssemblyRegistry(this));
        Services   = AddModule(new AssemblyServices(this));
    }

    protected override void OnCreateTables()
    {
        base.OnCreateTables();

        Exec(@"CREATE TABLE IF NOT EXISTS dependencies (
            assemblyId INTEGER NOT NULL,
            discoverable BOOLEAN,
            resourceType TEXT COLLATE NOCASE,
            dependentAssemblyId INTEGER NOT NULL,
            dependencyType TEXT COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(assemblyId,dependentAssemblyId)
        )");

        Exec(@"CREATE TABLE IF NOT EXISTS taskFolders (
            id INTEGER PRIMARY KEY,
            parentId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(parentId,name)
        )");

        Exec(@"CREATE TABLE IF NOT EXISTS tasks (
            assemblyId INTEGER NOT NULL,
            folderId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(folderId,name)
        )");
    }

    // ---- Dependencies ----

    public void AddDependency(long assemblyId, bool discoverable, string resourceType,
        long dependentAssemblyId, string dependencyType)
    {
        Execute(
            "INSERT OR REPLACE INTO dependencies " +
            "(assemblyId,discoverable,resourceType,dependentAssemblyId,dependencyType) " +
            "VALUES ($1,$2,$3,$4,$5)",
            assemblyId, discoverable ? 1 : 0, resourceType, dependentAssemblyId, dependencyType);
    }

    public Dictionary<long, AssemblyData> GetDependencies(long assemblyId)
    {
        var result = new Dictionary<long, AssemblyData>();
        Assemblies.QueryAssemblies(
            "SELECT * FROM assemblies WHERE id IN " +
            $"(SELECT dependentAssemblyId FROM dependencies WHERE assemblyId={assemblyId})",
            result);
        return result;
    }

    public Dictionary<long, AssemblyData> GetDependents(long assemblyId)
    {
        var result = new Dictionary<long, AssemblyData>();
        Assemblies.QueryAssemblies(
            "SELECT * FROM assemblies WHERE id IN " +
            $"(SELECT assemblyId FROM dependencies WHERE dependentAssemblyId={assemblyId})",
            result);
        return result;
    }

    // ---- Tasks ----

    public long AddTaskFolder(string name, long parent = 0)
    {
        Execute("INSERT OR IGNORE INTO taskFolders (parentId,name) VALUES ($1,$2)", parent, name);
        long id = QueryScalarLong("SELECT id FROM taskFolders WHERE parentId=$1 AND name=$2", parent, name);
        if (id == 0) throw new InvalidOperationException("AddTaskFolder: find failed");
        return id;
    }

    public string GetTaskFolderPath(long folderId)
    {
        var parts = new Stack<string>();
        while (folderId > 0)
        {
            using var rdr = Query("SELECT parentId,name FROM taskFolders WHERE id=$1", folderId);
            if (!rdr.Read()) break;
            folderId = rdr.GetInt64(0);
            parts.Push(rdr.GetString(1));
        }
        return string.Join('\\', parts);
    }

    public List<(long AssemblyId, long FolderId, string Name)> GetAssemblyTasks(long assemblyId)
    {
        var result = new List<(long, long, string)>();
        using var rdr = Query("SELECT assemblyId,folderId,name FROM tasks WHERE assemblyId=$1", assemblyId);
        while (rdr.Read())
            result.Add((rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetString(2)));
        return result;
    }
}
