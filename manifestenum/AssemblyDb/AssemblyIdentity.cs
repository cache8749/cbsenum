// Translation of AssemblyDb.Assemblies.pas — data types

namespace ManifestEnum;

public enum AssemblyState
{
    Missing   = 0,   // known only as a reference; not in the SxS store
    Present   = 1,   // in the store but not deployed
    Installed = 2,   // deployed
}

/// <summary>The 8-field identity that uniquely identifies a WinSxS assembly.</summary>
public struct AssemblyIdentity : IEquatable<AssemblyIdentity>
{
    public string Name                 { get; set; }
    public string Type                 { get; set; }
    public string Language             { get; set; }
    public string BuildType            { get; set; }
    public string ProcessorArchitecture{ get; set; }
    public string Version              { get; set; }
    public string PublicKeyToken       { get; set; }
    public string VersionScope         { get; set; }

    public AssemblyIdentity() { Name = Type = Language = BuildType = ProcessorArchitecture = Version = PublicKeyToken = VersionScope = ""; }

    // ---- string representations ----

    public override string ToString() =>
        $"{Name}-{Language}-{BuildType}-{ProcessorArchitecture}-{Version}-{PublicKeyToken}";

    /// <summary>SxS strong name: <c>Name,type="…",version="…",…</c></summary>
    public string ToStrongName()
    {
        var sb = new System.Text.StringBuilder(Name);
        if (Type.Length > 0)                       sb.Append($",type=\"{Type}\"");
        if (Version.Length > 0)                    sb.Append($",version=\"{Version}\"");
        if (PublicKeyToken.Length > 0)             sb.Append($",publicKeyToken=\"{PublicKeyToken}\"");
        if (ProcessorArchitecture.Length > 0)      sb.Append($",processorArchitecture=\"{ProcessorArchitecture}\"");
        if (Language is { Length: > 0 } l
            && !l.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            && l != "*")
            sb.Append($",language=\"{Language}\"");
        if (VersionScope.Length > 0)               sb.Append($",versionScope=\"{VersionScope}\"");
        return sb.ToString();
    }

    /// <summary>NET-style strong name: <c>Name, Culture=…, Version=…,…</c></summary>
    public string ToStrongNameNETStyle()
    {
        var sb = new System.Text.StringBuilder(Name);
        if (Language is { Length: > 0 } l
            && !l.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            && l != "*")
            sb.Append($", Culture={Language}");
        else
            sb.Append(", Culture=Neutral");
        if (Version.Length > 0)               sb.Append($", Version={Version}");
        if (PublicKeyToken.Length > 0)        sb.Append($", PublicKeyToken={PublicKeyToken}");
        if (ProcessorArchitecture.Length > 0) sb.Append($", ProcessorArchitecture={ProcessorArchitecture}");
        if (VersionScope.Length > 0)          sb.Append($", versionScope={VersionScope}");
        return sb.ToString();
    }

    public AssemblyIdentity ToLowercase() => new()
    {
        Name                  = Name.ToLowerInvariant(),
        Type                  = Type.ToLowerInvariant(),
        Language              = Language.ToLowerInvariant(),
        BuildType             = BuildType.ToLowerInvariant(),
        ProcessorArchitecture = ProcessorArchitecture.ToLowerInvariant(),
        Version               = Version.ToLowerInvariant(),
        PublicKeyToken        = PublicKeyToken.ToLowerInvariant(),
        VersionScope          = VersionScope.ToLowerInvariant(),
    };

    // ---- equality — uses OrdinalIgnoreCase to match SQLite NOCASE columns ----
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public bool Equals(AssemblyIdentity o) =>
        Cmp.Equals(Name, o.Name) && Cmp.Equals(Type, o.Type) &&
        Cmp.Equals(Language, o.Language) && Cmp.Equals(BuildType, o.BuildType) &&
        Cmp.Equals(ProcessorArchitecture, o.ProcessorArchitecture) &&
        Cmp.Equals(Version, o.Version) && Cmp.Equals(PublicKeyToken, o.PublicKeyToken) &&
        Cmp.Equals(VersionScope, o.VersionScope);

    public override bool Equals(object? obj) => obj is AssemblyIdentity o && Equals(o);

    public override int GetHashCode() =>
        HashCode.Combine(
            Cmp.GetHashCode(Name), Cmp.GetHashCode(Type),
            Cmp.GetHashCode(Language), Cmp.GetHashCode(BuildType),
            Cmp.GetHashCode(ProcessorArchitecture), Cmp.GetHashCode(Version),
            Cmp.GetHashCode(PublicKeyToken), Cmp.GetHashCode(VersionScope));
}

public struct AssemblyData
{
    public long             Id                   { get; set; }
    public AssemblyIdentity Identity             { get; set; }
    public string           ManifestName         { get; set; }
    public bool             IsDeployment         { get; set; }
    public AssemblyState    State                { get; set; }
}
