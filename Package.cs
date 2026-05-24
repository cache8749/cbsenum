// Translation of CBSEnum_Main.pas (data model portion) + PackageGroup logic

namespace CBSEnum;

public class Package
{
    public string Name { get; set; } = "";        // full package name
    public string DisplayName { get; set; } = ""; // display name
    public string Variation { get; set; } = "";   // base / WOW64
    public int CbsVisibility { get; set; }
    public int DefaultCbsVisibility { get; set; } // preserved in DefVis key
}

public class PackageGroup
{
    public string Name { get; set; } = "";
    public List<PackageGroup> Subgroups { get; } = new();
    public List<Package> Packages { get; } = new();

    // Internal routing: eat one name-part and recurse into the right subgroup.
    public void AddPackage(string packageName, Package package)
    {
        int posTilde = packageName.IndexOf('~');

        while (true)
        {
            int posMinus = packageName.IndexOf('-');
            // No more dashes, or the tilde comes first → this is the leaf
            if (posMinus < 0 || (posTilde >= 0 && posTilde < posMinus))
                break;

            string groupName = packageName[..posMinus];
            packageName = packageName[(posMinus + 1)..];
            posTilde = posTilde >= 0 ? posTilde - (posMinus + 1) : -1;

            if (groupName.Equals("WOW64", StringComparison.OrdinalIgnoreCase))
            {
                package.Variation = "WOW64";
                packageName += " (WOW64)";
                continue; // chew another part
            }

            PackageGroup group = NeedSubgroup(groupName);
            group.AddPackage(packageName, package);
            return;
        }

        // Strip trailing "Package~..." prefix that most names carry
        if (posTilde > 0 && packageName[..posTilde].Equals("Package", StringComparison.OrdinalIgnoreCase))
            packageName = packageName[posTilde..];

        package.DisplayName = packageName;
        Packages.Add(package);
    }

    // Public entry point – creates the Package object and routes it
    public Package AddPackage(string fullPackageName)
    {
        var pkg = new Package { Name = fullPackageName };
        AddPackage(fullPackageName, pkg);
        return pkg;
    }

    public PackageGroup? FindSubgroup(string groupName) =>
        Subgroups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

    public PackageGroup NeedSubgroup(string groupName)
    {
        var g = FindSubgroup(groupName);
        if (g is null)
        {
            g = new PackageGroup { Name = groupName };
            Subgroups.Add(g);
        }
        return g;
    }

    // Collapse single-package sub-trees to reduce tree depth
    public void CompactNames()
    {
        for (int i = Subgroups.Count - 1; i >= 0; i--)
        {
            Subgroups[i].CompactNames();

            if (Subgroups[i].Packages.Count == 1 && Subgroups[i].Subgroups.Count == 0)
            {
                var subPkg = Subgroups[i].Packages[0];
                subPkg.DisplayName = Subgroups[i].Name + "-" + subPkg.DisplayName;
                Packages.Add(subPkg);
                Subgroups.RemoveAt(i);
            }
        }

        if (Subgroups.Count == 1 && Packages.Count == 0)
        {
            var group = Subgroups[0];
            Subgroups.Clear();
            foreach (var sg in group.Subgroups) Subgroups.Add(sg);
            foreach (var pkg in group.Packages) Packages.Add(pkg);
            Name = Name + "-" + group.Name;
        }
    }

    public List<Package> SelectMatching(string mask)
    {
        var result = new List<Package>();
        SelectMatching(mask.ToLowerInvariant(), result);
        return result;
    }

    public void SelectMatching(string lowerMask, List<Package> result)
    {
        foreach (var pkg in Packages)
            if (WildcardMatching.Match(pkg.Name.ToLowerInvariant(), lowerMask)
                && !result.Contains(pkg))
                result.Add(pkg);

        foreach (var sub in Subgroups)
            sub.SelectMatching(lowerMask, result);
    }
}
