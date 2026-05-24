// Translation of AssemblyDb.Core.pas

using Microsoft.Data.Sqlite;

namespace ManifestEnum;

/// <summary>
/// Base class for all DB modules. Delegates execution to the owning <see cref="AssemblyDbCore"/>.
/// </summary>
public abstract class AssemblyDbModule
{
    protected readonly AssemblyDbCore Core;
    protected AssemblyDbModule(AssemblyDbCore core) => Core = core;

    // Convenience wrappers
    protected void Exec(string sql)                      => Core.Exec(sql);
    protected int  Execute(string sql, params object?[] p) => Core.Execute(sql, p);
    protected SqliteDataReader Query(string sql, params object?[] p) => Core.Query(sql, p);
    protected long QueryScalarLong(string sql, params object?[] p)   => Core.QueryScalarLong(sql, p);

    internal virtual void CreateTables()   { }
    internal virtual void InitStatements() { }
    internal virtual void Close()          { }
}

/// <summary>
/// Core database wrapper — translation of <c>TAssemblyDbCore</c>.
/// Owns the SQLite connection and coordinates modules.
/// </summary>
public class AssemblyDbCore : IDisposable
{
    internal SqliteConnection? Connection;
    private readonly List<AssemblyDbModule> _modules = new();

    // ---- Lifecycle ----

    public bool Open(string filename)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = filename,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        };
        Connection = new SqliteConnection(csb.ToString());
        Connection.Open();

        Exec("PRAGMA cache_size=200000");
        Exec("PRAGMA synchronous=OFF");
        Exec("PRAGMA temp_store=2");

        OnCreateTables();
        OnInitStatements();
        return true;
    }

    public void Close()
    {
        foreach (var m in _modules) m.Close();
        Connection?.Close();
        Connection?.Dispose();
        Connection = null;
    }

    public void Dispose() => Close();

    // ---- Transactions ----
    public void BeginTransaction()  => Exec("BEGIN");
    public void CommitTransaction() => Exec("COMMIT");
    public void AbortTransaction()  => Exec("ROLLBACK");

    // ---- Command helpers ----
    // These create, bind, run, and dispose a command on each call.
    // For this workload the overhead is negligible and avoids the Reset()/Clear() complexity
    // that the raw C SQLite API requires.

    private SqliteCommand BuildCommand(string sql, object?[] args)
    {
        var cmd = Connection!.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        return cmd;
    }

    public void Exec(string sql)
    {
        using var cmd = Connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Execute a DML statement with positional parameters <c>$1</c>, <c>$2</c>, …</summary>
    public int Execute(string sql, params object?[] args)
    {
        using var cmd = BuildCommand(sql, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Open a reader. <b>Caller must dispose it.</b></summary>
    public SqliteDataReader Query(string sql, params object?[] args)
    {
        var cmd = BuildCommand(sql, args);
        return cmd.ExecuteReader(System.Data.CommandBehavior.CloseConnection & 0); // leaves connection open
    }

    public long QueryScalarLong(string sql, params object?[] args)
    {
        using var cmd = BuildCommand(sql, args);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    }

    public long LastInsertRowId()
        => QueryScalarLong("SELECT last_insert_rowid()");

    // ---- Module registration ----
    protected T AddModule<T>(T module) where T : AssemblyDbModule
    {
        _modules.Add(module);
        return module;
    }

    protected virtual void OnCreateTables()
    {
        foreach (var m in _modules) m.CreateTables();
    }

    protected virtual void OnInitStatements()
    {
        foreach (var m in _modules) m.InitStatements();
    }
}
