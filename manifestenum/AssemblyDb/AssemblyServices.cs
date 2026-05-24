// Translation of AssemblyDb.Services.pas

using Microsoft.Data.Sqlite;

namespace ManifestEnum;

public struct ServiceEntryData
{
    public long   AssemblyId          { get; set; }
    public string Name                { get; set; }
    public string DisplayName         { get; set; }
    public string ErrorControl        { get; set; }
    public string ImagePath           { get; set; }
    public string Start               { get; set; }
    public string Type                { get; set; }
    public string Description         { get; set; }
    public string ObjectName          { get; set; }
    public string SidType             { get; set; }
    public string RequiredPrivileges  { get; set; }
}

public class AssemblyServices : AssemblyDbModule
{
    public AssemblyServices(AssemblyDbCore core) : base(core) { }

    internal override void CreateTables()
    {
        Exec(@"CREATE TABLE IF NOT EXISTS services (
            id INTEGER PRIMARY KEY,
            assemblyId INTEGER NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            displayName TEXT COLLATE NOCASE,
            errorControl TEXT COLLATE NOCASE,
            imagePath TEXT COLLATE NOCASE,
            start TEXT COLLATE NOCASE,
            type TEXT COLLATE NOCASE,
            description TEXT COLLATE NOCASE,
            objectName TEXT COLLATE NOCASE,
            sidType TEXT COLLATE NOCASE,
            requiredPrivileges TEXT COLLATE NOCASE,
            CONSTRAINT identity UNIQUE(assemblyId,name)
        )");
    }

    public long AddService(ServiceEntryData data)
    {
        Execute("INSERT OR IGNORE INTO services (assemblyId,name) VALUES ($1,$2)",
            data.AssemblyId, data.Name);

        long id = QueryScalarLong("SELECT id FROM services WHERE assemblyId=$1 AND name=$2",
            data.AssemblyId, data.Name);
        if (id == 0) throw new InvalidOperationException("AddService: find failed");

        UpdateService(id, data);
        return id;
    }

    public void UpdateService(long serviceId, ServiceEntryData data)
    {
        Execute(
            "UPDATE services SET displayName=$1, errorControl=$2, imagePath=$3, start=$4, " +
            "type=$5, description=$6, objectName=$7, sidType=$8, requiredPrivileges=$9 WHERE id=$10",
            data.DisplayName, data.ErrorControl, data.ImagePath, data.Start,
            data.Type, data.Description, data.ObjectName, data.SidType,
            data.RequiredPrivileges, serviceId);
    }

    public Dictionary<long, ServiceEntryData> GetAssemblyServices(long assemblyId)
    {
        var result = new Dictionary<long, ServiceEntryData>();
        using var rdr = Query("SELECT * FROM services WHERE assemblyId=$1", assemblyId);
        while (rdr.Read())
        {
            long id = rdr.GetInt64(0);
            result[id] = new ServiceEntryData
            {
                AssemblyId         = rdr.GetInt64(1),
                Name               = rdr.GetString(2),
                DisplayName        = rdr.IsDBNull(3)  ? "" : rdr.GetString(3),
                ErrorControl       = rdr.IsDBNull(4)  ? "" : rdr.GetString(4),
                ImagePath          = rdr.IsDBNull(5)  ? "" : rdr.GetString(5),
                Start              = rdr.IsDBNull(6)  ? "" : rdr.GetString(6),
                Type               = rdr.IsDBNull(7)  ? "" : rdr.GetString(7),
                Description        = rdr.IsDBNull(8)  ? "" : rdr.GetString(8),
                ObjectName         = rdr.IsDBNull(9)  ? "" : rdr.GetString(9),
                SidType            = rdr.IsDBNull(10) ? "" : rdr.GetString(10),
                RequiredPrivileges = rdr.IsDBNull(11) ? "" : rdr.GetString(11),
            };
        }
        return result;
    }
}
