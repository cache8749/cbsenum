// Translation of AssemblyDb.Registry.pas

using Microsoft.Data.Sqlite;

namespace ManifestEnum;

public struct RegistryValueData
{
    public long   Id            { get; set; }
    public long   Assembly      { get; set; }
    public long   Key           { get; set; }
    public string Name          { get; set; }
    public uint   ValueType     { get; set; }
    public string Value         { get; set; }
    public string OperationHint { get; set; }
    public bool   Owner         { get; set; }
}

public class AssemblyRegistry : AssemblyDbModule
{
    // Key identity cache: (parent, name) → id
    private readonly Dictionary<(long Parent, string Name), long> _keyCache = new();

    public AssemblyRegistry(AssemblyDbCore core) : base(core) { }

    internal override void CreateTables()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS registryKeys (
            id INTEGER PRIMARY KEY,
            parentId INTEGER NOT NULL,
            keyName TEXT NOT NULL COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(parentId,keyName)
        )");
        Exec(@"CREATE TABLE IF NOT EXISTS registryKeyReferences (
            assemblyId INTEGER NOT NULL,
            keyId INTEGER NOT NULL,
            owner BOOLEAN,
            CONSTRAINT identity UNIQUE(assemblyId,keyId)
        )");
        Exec(@"CREATE TABLE IF NOT EXISTS registryValues (
            assemblyId INTEGER NOT NULL,
            keyId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            valueType INTEGER NOT NULL,
            value TEXT NOT NULL COLLATE NOCASE,
            operationHint TEXT NOT NULL COLLATE NOCASE,
            owner BOOLEAN,
            CONSTRAINT identity UNIQUE(assemblyId,keyId,name)
        )");
    }

    internal override void InitStatements()
    {
        _keyCache.Clear();
    }

    // ---- Key helpers ----

    public long AddKey(string name, long parent = 0)
    {
        var cacheKey = (parent, name.ToLowerInvariant());
        if (_keyCache.TryGetValue(cacheKey, out long cached)) return cached;

        Execute("INSERT OR IGNORE INTO registryKeys (parentId,keyName) VALUES ($1,$2)", parent, name);

        long id = QueryScalarLong("SELECT id FROM registryKeys WHERE parentId=$1 AND keyName=$2", parent, name);
        if (id == 0) throw new InvalidOperationException("AddKey: find failed");

        _keyCache[cacheKey] = id;
        return id;
    }

    public long AddKeyPath(string path)
    {
        long parent = 0;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            parent = AddKey(part, parent);
        return parent;
    }

    public void AddKeyReference(long assembly, long keyId, bool owner)
    {
        Execute(
            "INSERT OR IGNORE INTO registryKeyReferences (assemblyId,keyId,owner) VALUES ($1,$2,$3)",
            assembly, keyId, owner ? 1 : 0);
    }

    public long AddKey(long assembly, string path, bool owner)
    {
        long id = AddKeyPath(path);
        if (id != 0) AddKeyReference(assembly, id, owner);
        return id;
    }

    public void AddValue(long assembly, RegistryValueData data)
    {
        Execute(
            "INSERT OR IGNORE INTO registryValues (assemblyId,keyId,name,valueType,value,operationHint,owner) VALUES ($1,$2,$3,$4,$5,$6,$7)",
            assembly, data.Key, data.Name, data.ValueType, data.Value, data.OperationHint, data.Owner ? 1 : 0);
    }

    // ---- Query helpers ----

    public string GetKeyPath(long keyId)
    {
        var parts = new Stack<string>();
        while (keyId > 0)
        {
            using var rdr = Query("SELECT parentId,keyName FROM registryKeys WHERE id=$1", keyId);
            if (!rdr.Read()) break;
            keyId = rdr.GetInt64(0);
            parts.Push(rdr.GetString(1));
        }
        return string.Join('\\', parts);
    }

    public List<RegistryValueData> GetAssemblyValues(long assemblyId)
    {
        var result = new List<RegistryValueData>();
        using var rdr = Query("SELECT rowid, * FROM registryValues WHERE assemblyId=$1", assemblyId);
        while (rdr.Read())
            result.Add(new RegistryValueData
            {
                Id            = rdr.GetInt64(0),
                Assembly      = rdr.GetInt64(1),
                Key           = rdr.GetInt64(2),
                Name          = rdr.GetString(3),
                ValueType     = (uint)rdr.GetInt64(4),
                Value         = rdr.GetString(5),
                OperationHint = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                Owner         = !rdr.IsDBNull(7) && rdr.GetBoolean(7),
            });
        return result;
    }
}
