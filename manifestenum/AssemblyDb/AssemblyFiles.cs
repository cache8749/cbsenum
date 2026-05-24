// Translation of AssemblyDb.Files.pas

using Microsoft.Data.Sqlite;

namespace ManifestEnum;

public struct FileEntryData
{
    public long   Id          { get; set; }
    public long   Assembly    { get; set; }
    public long   Folder      { get; set; }
    public string Name        { get; set; }
    public string SourceName  { get; set; }
    public string SourcePath  { get; set; }
    public string ImportPath  { get; set; }
}

public struct FolderReferenceData
{
    public bool Owner { get; set; }
}

public class AssemblyFiles : AssemblyDbModule
{
    public AssemblyFiles(AssemblyDbCore core) : base(core) { }

    internal override void CreateTables()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS folders (
            id INTEGER PRIMARY KEY,
            parentId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(parentId, name)
        )");
        Exec(@"CREATE TABLE IF NOT EXISTS folderReferences (
            assemblyId INTEGER NOT NULL,
            folderId INTEGER NOT NULL,
            owner BOOLEAN,
            CONSTRAINT identity UNIQUE(assemblyId,folderId)
        )");
        Exec(@"CREATE TABLE IF NOT EXISTS files (
            assemblyId INTEGER NOT NULL,
            folderId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            sourceName TEXT COLLATE NOCASE,
            sourcePath TEXT COLLATE NOCASE,
            importPath TEXT COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(assemblyId,folderId,name)
        )");
    }

    // ---- Folder helpers ----

    public long AddFolder(string name, long parent = 0)
    {
        Execute("INSERT OR IGNORE INTO folders (parentId,name) VALUES ($1,$2)", parent, name);
        long id = QueryScalarLong("SELECT id FROM folders WHERE parentId=$1 AND name=$2", parent, name);
        if (id == 0) throw new InvalidOperationException("AddFolder: find failed");
        return id;
    }

    public long AddFolderPath(string path)
    {
        long parent = 0;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            parent = AddFolder(part, parent);
        return parent;
    }

    public void AddFolderReference(long assembly, long folder, FolderReferenceData data)
    {
        Execute(
            "INSERT OR IGNORE INTO folderReferences (assemblyId,folderId,owner) VALUES ($1,$2,$3)",
            assembly, folder, data.Owner ? 1 : 0);
    }

    public long AddFolder(long assembly, string path, FolderReferenceData data)
    {
        long id = AddFolderPath(path);
        if (id != 0) AddFolderReference(assembly, id, data);
        return id;
    }

    public void AddFile(FileEntryData file)
    {
        Execute(
            "INSERT OR IGNORE INTO files (assemblyId,folderId,name,sourceName,sourcePath,importPath) VALUES ($1,$2,$3,$4,$5,$6)",
            file.Assembly, file.Folder, file.Name, file.SourceName, file.SourcePath, file.ImportPath);
    }

    // ---- Query helpers ----

    public string GetFolderPath(long folderId)
    {
        var parts = new Stack<string>();
        while (folderId > 0)
        {
            using var rdr = Query("SELECT parentId,name FROM folders WHERE id=$1", folderId);
            if (!rdr.Read()) break;
            folderId = rdr.GetInt64(0);
            parts.Push(rdr.GetString(1));
        }
        return string.Join('\\', parts);
    }

    public string GetFileFullDestinationName(FileEntryData file) =>
        file.Folder == 0 ? file.Name : GetFolderPath(file.Folder) + '\\' + file.Name;

    /// <summary>Returns the folders referenced by <paramref name="assemblyId"/>,
    /// keyed by folder-id.
    /// Note: fixes a bug in the original where column 1 was read from a single-column SELECT.</summary>
    public Dictionary<long, FolderReferenceData> GetAssemblyFolders(long assemblyId)
    {
        var result = new Dictionary<long, FolderReferenceData>();
        using var rdr = Query("SELECT folderId, owner FROM folderReferences WHERE assemblyId=$1", assemblyId);
        while (rdr.Read())
            result[rdr.GetInt64(0)] = new FolderReferenceData { Owner = !rdr.IsDBNull(1) && rdr.GetBoolean(1) };
        return result;
    }

    public List<FileEntryData> GetAssemblyFiles(long assemblyId)
    {
        var result = new List<FileEntryData>();
        using var rdr = Query("SELECT rowid, * FROM files WHERE assemblyId=$1", assemblyId);
        while (rdr.Read()) result.Add(ReadFileRow(rdr));
        return result;
    }

    private static FileEntryData ReadFileRow(SqliteDataReader r) => new()
    {
        Id         = r.GetInt64(0),
        Assembly   = r.GetInt64(1),
        Folder     = r.GetInt64(2),
        Name       = r.GetString(3),
        SourceName = r.IsDBNull(4) ? "" : r.GetString(4),
        SourcePath = r.IsDBNull(5) ? "" : r.GetString(5),
        ImportPath = r.IsDBNull(6) ? "" : r.GetString(6),
    };
}
