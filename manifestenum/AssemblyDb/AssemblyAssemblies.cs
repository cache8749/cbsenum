// Translation of AssemblyDb.Assemblies.pas

using Microsoft.Data.Sqlite;

namespace ManifestEnum;

public class AssemblyAssemblies : AssemblyDbModule
{
    // In-process cache: identity → rowid (mirrors FIdCache in the Delphi original)
    private readonly Dictionary<AssemblyIdentity, long> _idCache = new();

    public AssemblyAssemblies(AssemblyDbCore core) : base(core) { }

    internal override void CreateTables()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS assemblies (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE,
            type TEXT NOT NULL COLLATE NOCASE,
            language TEXT NOT NULL COLLATE NOCASE,
            buildType TEXT NOT NULL COLLATE NOCASE,
            processorArchitecture TEXT NOT NULL COLLATE NOCASE,
            version TEXT NOT NULL COLLATE NOCASE,
            publicKeyToken TEXT NOT NULL,
            versionScope TEXT NOT NULL COLLATE NOCASE,
            manifestName TEXT COLLATE NOCASE,
            isDeployment BOOL,
            state BOOL,
            CONSTRAINT identity UNIQUE(name,type,language,buildType,processorArchitecture,version,publicKeyToken,versionScope)
        )");
    }

    internal override void InitStatements()
    {
        _idCache.Clear();
    }

    // ---- Public API ----

    /// <summary>Insert-or-update an assembly, returning its rowid.</summary>
    public long AddAssembly(AssemblyIdentity identity, string manifestName, bool isDeployment, AssemblyState state)
    {
        long id = NeedAssembly(identity);
        Execute(
            "UPDATE assemblies SET manifestName=$1, isDeployment=$2, state=$3 WHERE id=$4",
            manifestName, isDeployment ? 1 : 0, (int)state, id);
        return id;
    }

    /// <summary>Find-or-create an assembly by identity, returning its rowid.</summary>
    public long NeedAssembly(AssemblyIdentity identity)
    {
        if (_idCache.TryGetValue(identity, out long cached)) return cached;

        // Touch (INSERT OR IGNORE)
        Execute(
            "INSERT OR IGNORE INTO assemblies " +
            "(name,type,language,buildType,processorArchitecture,version,publicKeyToken,versionScope) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8)",
            identity.Name, identity.Type, identity.Language, identity.BuildType,
            identity.ProcessorArchitecture, identity.Version, identity.PublicKeyToken, identity.VersionScope);

        // Find ID (always — last_insert_rowid is unreliable when IGNORE fires)
        long id = QueryScalarLong(
            "SELECT id FROM assemblies WHERE " +
            "name=$1 AND type=$2 AND language=$3 AND buildType=$4 " +
            "AND processorArchitecture=$5 AND version=$6 AND publicKeyToken=$7 AND versionScope=$8",
            identity.Name, identity.Type, identity.Language, identity.BuildType,
            identity.ProcessorArchitecture, identity.Version, identity.PublicKeyToken, identity.VersionScope);

        if (id == 0) throw new InvalidOperationException("NeedAssembly: find failed");
        _idCache[identity] = id;
        return id;
    }

    public AssemblyData GetAssembly(long assemblyId)
    {
        using var rdr = Query("SELECT * FROM assemblies WHERE id=$1", assemblyId);
        if (!rdr.Read()) throw new InvalidOperationException($"Assembly {assemblyId} not found");
        return ReadRow(rdr);
    }

    public Dictionary<long, AssemblyData> GetAllAssemblies()
    {
        var result = new Dictionary<long, AssemblyData>();
        QueryAssemblies("SELECT * FROM assemblies", result);
        return result;
    }

    public void QueryAssemblies(string sql, Dictionary<long, AssemblyData> list)
    {
        using var rdr = Query(sql);
        while (rdr.Read())
        {
            var d = ReadRow(rdr);
            if (!list.ContainsKey(d.Id)) list[d.Id] = d;
        }
    }

    public void GetAssemblyValues(long assemblyId, Dictionary<long, AssemblyData> list)
    {
        using var rdr = Query("SELECT * FROM assemblies WHERE id=$1", assemblyId);
        while (rdr.Read())
        {
            var d = ReadRow(rdr);
            if (!list.ContainsKey(d.Id)) list[d.Id] = d;
        }
    }

    // ---- Row reader ----

    internal static AssemblyData ReadRow(SqliteDataReader r) => new()
    {
        Id           = r.GetInt64(0),
        Identity     = new AssemblyIdentity
        {
            Name                  = r.GetString(1),
            Type                  = r.GetString(2),
            Language              = r.GetString(3),
            BuildType             = r.GetString(4),
            ProcessorArchitecture = r.GetString(5),
            Version               = r.GetString(6),
            PublicKeyToken        = r.GetString(7),
            VersionScope          = r.GetString(8),
        },
        ManifestName = r.IsDBNull(9)  ? "" : r.GetString(9),
        IsDeployment = r.IsDBNull(10) ? false : r.GetBoolean(10),
        State        = r.IsDBNull(11) ? AssemblyState.Missing : (AssemblyState)r.GetInt32(11),
    };
}
