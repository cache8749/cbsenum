// Translation of CBSEnum_Main.pas

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32;
using ManifestEnum;

namespace CBSEnum;

public sealed partial class MainForm : Form
{
    // -------------------------------------------------------------------------
    // Registry constants
    // -------------------------------------------------------------------------
    private const string CbsPackagesKey        = Constants.CbsKey + @"\Packages";
    private const string CbsPackagesPendingKey  = Constants.CbsKey + @"\PackagesPending";

    // -------------------------------------------------------------------------
    // Data model
    // -------------------------------------------------------------------------
    private PackageGroup? _packages;
    private int _totalPackages;
    private int _visiblePackages;

    // Node tag data
    private sealed class NodeData
    {
        public string DisplayName { get; set; } = "";
        public Package? Package { get; set; }
    }

    // -------------------------------------------------------------------------
    // Controls (declared here; built in InitializeComponent)
    // -------------------------------------------------------------------------
    private TreeView _tvPackages = null!;
    private CheckBox _cbShowWow64 = null!;
    private CheckBox _cbShowKb = null!;
    private CheckBox _cbShowHidden = null!;
    private TextBox _edtFilter = null!;
    private RadioButton _rbGroupEachPart = null!;
    private RadioButton _rbGroupDistinctParts = null!;
    private RadioButton _rbGroupFlat = null!;
    private TabControl _pageInfo = null!;
    private TabPage _tsInfo = null!;
    private TabPage _tsResources = null!;
    private Label _lblDescription = null!;
    private ListBox _lbUpdates = null!;
    private ContextMenuStrip _contextMenu = null!;
    private readonly JobProcessorForm _jobProcessorForm = new();

    // ---- Assembly database (ManifestEnum) ----
    private AssemblyDb?              _db;
    private AssemblyResourcesView?   _resourcesView;

    // Multi-select support (WinForms TreeView doesn't have this built-in)
    private readonly HashSet<TreeNode> _selectedNodes = new();
    private TreeNode? _anchorNode;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public MainForm()
    {
        InitializeComponent();
        Shown += (_, _) => Reload();
        FormClosed += (_, _) => { _db?.Dispose(); };
    }

    // -------------------------------------------------------------------------
    // Reload: read packages from registry and rebuild tree
    // -------------------------------------------------------------------------
    public void Reload()
    {
        _packages = new PackageGroup();
        _totalPackages = 0;
        _visiblePackages = 0;
        _tvPackages.Nodes.Clear();
        _selectedNodes.Clear();

        try
        {
            using var reg = Registry.LocalMachine.OpenSubKey(CbsPackagesKey, writable: false)
                ?? throw new Exception(
                    "Cannot open registry key for packages. Perhaps you are not running as administrator, "
                    + "or the Windows version is incompatible.");

            var subkeys = reg.GetSubKeyNames();
            _totalPackages = subkeys.Length;

            foreach (string name in subkeys)
            {
                Package pkg;
                if (_rbGroupFlat.Checked)
                {
                    pkg = new Package { Name = name, DisplayName = name };
                    _packages.Packages.Add(pkg);
                }
                else
                {
                    pkg = _packages.AddPackage(name);
                }

                using var pkgKey = reg.OpenSubKey(name, writable: false);
                if (pkgKey is null) continue;

                pkg.CbsVisibility = SafeReadInt(pkgKey, "Visibility", 1);
                pkg.DefaultCbsVisibility = SafeReadInt(pkgKey, "DefVis", pkg.CbsVisibility);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_rbGroupDistinctParts.Checked)
            _packages.CompactNames();

        _tvPackages.BeginUpdate();
        try
        {
            ReloadPackageTree(_packages, null);
        }
        finally
        {
            _tvPackages.EndUpdate();
        }

        UpdateNodeVisibility();
    }

    private static int SafeReadInt(RegistryKey key, string valueName, int fallback)
    {
        try { return (int)(key.GetValue(valueName) ?? fallback); }
        catch { return fallback; }
    }

    // -------------------------------------------------------------------------
    // Tree building
    // -------------------------------------------------------------------------
    private void ReloadPackageTree(PackageGroup group, TreeNode? parentNode)
    {
        foreach (var sub in group.Subgroups)
        {
            var node = AddNode(parentNode, sub.Name, null);
            ReloadPackageTree(sub, node);
        }
        foreach (var pkg in group.Packages)
            AddNode(parentNode, pkg.DisplayName, pkg);
    }

    private TreeNode AddNode(TreeNode? parent, string displayName, Package? pkg)
    {
        var node = new TreeNode(displayName)
        {
            Tag = new NodeData { DisplayName = displayName, Package = pkg }
        };
        if (parent is null)
            _tvPackages.Nodes.Add(node);
        else
            parent.Nodes.Add(node);
        return node;
    }

    // -------------------------------------------------------------------------
    // Visibility logic
    // -------------------------------------------------------------------------
    private void UpdateNodeVisibility()
    {
        _tvPackages.BeginUpdate();
        try
        {
            // 1. Reset all IsVisible
            ForEachNode(_tvPackages.Nodes, n => NodeDataOf(n).Package = NodeDataOf(n).Package); // keep data intact
            // 2. Mark visible packages + their ancestors
            ForEachNode(_tvPackages.Nodes, n =>
            {
                if (NodeDataOf(n).Package is not null && IsPackageNodeVisible(n))
                {
                    n.ForeColor = NodeForeColor(n);
                    MarkAncestorsVisible(n);
                }
                else if (NodeDataOf(n).Package is null)
                {
                    n.ForeColor = SystemColors.WindowText;
                }
            });
            // 3. Apply visibility (WinForms TreeView doesn't support invisible nodes,
            //    so we rebuild with only visible nodes)
            RebuildVisibleTree();
        }
        finally
        {
            _tvPackages.EndUpdate();
        }

        CountVisiblePackages();
        UpdateFormCaption();
    }

    // Because WinForms TreeView has no per-node visibility, we track which nodes
    // should show via a HashSet and rebuild the tree after each filter change.
    private readonly HashSet<TreeNode> _visibleNodeSet = new();

    private void MarkAncestorsVisible(TreeNode node)
    {
        _visibleNodeSet.Add(node);
        var p = node.Parent;
        while (p is not null) { _visibleNodeSet.Add(p); p = p.Parent; }
    }

    private void RebuildVisibleTree()
    {
        // Re-populate _visibleNodeSet
        _visibleNodeSet.Clear();
        ForEachNode(_tvPackages.Nodes, n =>
        {
            if (NodeDataOf(n).Package is not null && IsPackageNodeVisible(n))
                MarkAncestorsVisible(n);
        });

        // Prune nodes not in the visible set
        PruneHiddenNodes(_tvPackages.Nodes);
    }

    private void PruneHiddenNodes(TreeNodeCollection nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (!_visibleNodeSet.Contains(n))
            {
                // Stash the node data so we can re-add it on next Reload()
                // Actually we just remove; next Reload() rebuilds from _packages
                nodes.RemoveAt(i);
            }
            else
            {
                PruneHiddenNodes(n.Nodes);
            }
        }
    }

    private bool IsPackageNodeVisible(TreeNode node)
    {
        var data = NodeDataOf(node);
        if (data.Package is null) return true; // group nodes shown if any child is shown

        if (!_cbShowHidden.Checked && data.Package.CbsVisibility != 1)
            return false;
        if (!_cbShowWow64.Checked && data.Package.Variation.Equals("WOW64", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!_cbShowKb.Checked && data.DisplayName.StartsWith("Package_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_edtFilter.Text.Length > 0)
        {
            bool nameMatch = data.DisplayName.Contains(_edtFilter.Text, StringComparison.OrdinalIgnoreCase)
                          || data.Package.Name.Contains(_edtFilter.Text, StringComparison.OrdinalIgnoreCase);
            if (!nameMatch) return false;
        }
        return true;
    }

    private static Color NodeForeColor(TreeNode node)
    {
        var pkg = NodeDataOf(node).Package;
        if (pkg is null) return SystemColors.WindowText;
        return pkg.CbsVisibility == 1 ? Color.Blue : Color.FromArgb(135, 153, 255);
    }

    private void CountVisiblePackages()
    {
        _visiblePackages = 0;
        ForEachNode(_tvPackages.Nodes, n =>
        {
            if (NodeDataOf(n).Package is not null) _visiblePackages++;
        });
    }

    private void UpdateFormCaption() =>
        Text = $"{_visiblePackages} packages ({_totalPackages} total) — CBSEnum";

    // -------------------------------------------------------------------------
    // Package selection helpers
    // -------------------------------------------------------------------------
    private static NodeData NodeDataOf(TreeNode node) => (NodeData)node.Tag!;

    private List<Package> GetAllPackages()
    {
        var result = new List<Package>();
        ForEachNode(_tvPackages.Nodes, n =>
        {
            if (NodeDataOf(n).Package is { } pkg && !result.Contains(pkg))
                result.Add(pkg);
        });
        return result;
    }

    private List<Package> GetSelectedPackages()
    {
        var result = new List<Package>();
        foreach (var sel in _selectedNodes)
            CollectPackagesUnder(sel, result);
        return result;
    }

    private static void CollectPackagesUnder(TreeNode node, List<Package> result)
    {
        if (NodeDataOf(node).Package is { } pkg && !result.Contains(pkg))
            result.Add(pkg);
        foreach (TreeNode child in node.Nodes)
            CollectPackagesUnder(child, result);
    }

    private string[] GetSelectedPackageNames() =>
        GetSelectedPackages().Select(p => p.Name).ToArray();

    private static void ForEachNode(TreeNodeCollection nodes, Action<TreeNode> action)
    {
        foreach (TreeNode node in nodes)
        {
            action(node);
            ForEachNode(node.Nodes, action);
        }
    }

    // -------------------------------------------------------------------------
    // CBS visibility writing
    // -------------------------------------------------------------------------
    // visibility: 1=visible, 2=invisible, -1=restore to default
    private void SetCbsVisibility(IEnumerable<Package> packages, int visibility)
    {
        SetCbsVisibilityInKey(CbsPackagesKey, packages, visibility);
        SetCbsVisibilityInKey(CbsPackagesPendingKey, packages, visibility);
        UpdateNodeVisibility();
        _tvPackages.Invalidate();
    }

    private void SetCbsVisibilityInKey(string keyPath, IEnumerable<Package> packages, int visibility)
    {
        using var reg = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        if (reg is null) return;

        foreach (var pkg in packages)
        {
            if (visibility >= 0 && pkg.CbsVisibility == visibility) continue;
            if (visibility < 0 && pkg.CbsVisibility == pkg.DefaultCbsVisibility) continue;

            using var pkgKey = reg.OpenSubKey(pkg.Name, writable: true);
            if (pkgKey is null) continue;

            // Preserve DefaultCbsVisibility once (like install_wim_tweak does)
            try
            {
                int existing = (int)(pkgKey.GetValue("DefVis") ?? throw new InvalidOperationException());
                pkg.DefaultCbsVisibility = existing;
            }
            catch
            {
                pkgKey.SetValue("DefVis", pkg.DefaultCbsVisibility, RegistryValueKind.DWord);
            }

            pkg.CbsVisibility = visibility >= 0 ? visibility : pkg.DefaultCbsVisibility;

            try
            {
                pkgKey.SetValue("Visibility", pkg.CbsVisibility, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                var answer = MessageBox.Show(
                    $"Cannot process package {pkg.Name}:\n{ex.Message}\nContinue with other packages?",
                    "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (answer != DialogResult.Yes) break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // DISM uninstall
    // -------------------------------------------------------------------------
    private void DismUninstall(IEnumerable<string> packageNames)
    {
        string argList = string.Join("", packageNames.Select(n => $" /PackageName={n}"));
        var proc = OsUtils.StartProcess(
            Path.Combine(OsUtils.GetSystemDir(), "dism.exe"),
            "dism.exe /Online /Remove-Package" + argList);

        proc.WaitForExit();
        uint exitCode = (uint)proc.ExitCode;

        switch (exitCode)
        {
            case 0:
            case Constants.ERROR_SUCCESS_REBOOT_REQUIRED:
                break;
            case Constants.CBS_E_INVALID_PACKAGE:
                MessageBox.Show(
                    "Uninstall says there's no such package. Perhaps refresh?\n"
                    + "Or maybe you have forgotten to make packages visible. "
                    + "This also sometimes happens when the package is marked for deletion until reboot.",
                    "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
            default:
                MessageBox.Show(
                    $"Uninstall seems to have failed with error code {exitCode}",
                    "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Save package list
    // -------------------------------------------------------------------------
    private void SavePackageList(string[] names)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(dlg.FileName, names);
    }

    // -------------------------------------------------------------------------
    // Info tab: parse .mum file
    // -------------------------------------------------------------------------
    private void LoadInfoTab()
    {
        _lblDescription.Text = "";
        _lbUpdates.Items.Clear();

        var node = _tvPackages.SelectedNode;
        if (node is null) return;
        var data = NodeDataOf(node);
        if (data.Package is null) return;

        string mumPath = Path.Combine(OsUtils.GetWindowsDir(), "servicing", "Packages",
            data.Package.Name + ".mum");
        if (!File.Exists(mumPath)) return;

        try
        {
            var doc = new XmlDocument();
            doc.Load(mumPath);

            var assembly = doc["assembly"];
            if (assembly is null) return;
            var package = assembly["package"];

            string desc = "";
            desc += XmlAttr(assembly, "name");
            if (desc.Length > 0) desc += "\n";
            desc += XmlAttr(assembly, "description");

            if (desc.Trim().Length == 0 && package is not null)
            {
                desc += XmlAttr(package, "name");
                if (desc.Length > 0) desc += "\n";
                desc += XmlAttr(package, "description");
            }

            if (desc.Trim().Length == 0)
                desc = data.Package.Name;

            string copyright = XmlAttr(assembly, "copyright");
            if (copyright.Length == 0 && package is not null)
                copyright = XmlAttr(package, "copyright");

            _lblDescription.Text = desc.Trim();
            if (copyright.Length > 0)
                _lblDescription.Text += "\n" + copyright;

            if (package is not null)
            {
                foreach (XmlNode updateNode in package.ChildNodes)
                {
                    if (updateNode.Name != "update") continue;
                    string label = XmlAttr(updateNode, "displayName");
                    if (label.Length > 0) label += " ";
                    label += XmlAttr(updateNode, "description");
                    if (label.Trim().Length == 0) label = XmlAttr(updateNode, "name");
                    _lbUpdates.Items.Add(label.Trim());
                }
            }
        }
        catch { /* best-effort */ }
    }

    private static string XmlAttr(XmlNode node, string name) =>
        node.Attributes?[name]?.Value ?? "";

    // -------------------------------------------------------------------------
    // Resources tab: load assemblies referenced from the selected package's .mum
    // (translation of tsResourcesEnter in CBSEnum_Main.pas)
    // -------------------------------------------------------------------------
    private void LoadResourcesTab()
    {
        if (_resourcesView is null || _db is null) return;

        _resourcesView.Assemblies.Clear();

        var node = _tvPackages.SelectedNode;
        if (node is null) return;
        var data = NodeDataOf(node);
        if (data.Package is null) return;

        string mumPath = Path.Combine(OsUtils.GetWindowsDir(), "servicing", "Packages",
            data.Package.Name + ".mum");
        if (!File.Exists(mumPath)) { _resourcesView.Reload(); return; }

        try
        {
            var doc = new XmlDocument();
            doc.Load(mumPath);
            foreach (XmlNode identityNode in ListPackageAssemblyIdentities(doc))
            {
                var identity = ReadAssemblyIdentity(identityNode);
                long id = _db.Assemblies.NeedAssembly(identity);
                _resourcesView.Assemblies.Add(id);
            }
        }
        catch { /* best-effort */ }

        _resourcesView.Reload();
    }

    /// <summary>Finds all assemblyIdentity nodes inside update/component elements.</summary>
    private static List<XmlNode> ListPackageAssemblyIdentities(XmlDocument doc)
    {
        var result = new List<XmlNode>();
        var assembly = doc["assembly"];
        if (assembly is null) return result;
        var package = assembly["package"];
        if (package is null) return result;

        foreach (XmlNode update in package.ChildNodes)
        {
            if (update.Name != "update") continue;
            foreach (XmlNode component in update.ChildNodes)
            {
                if (component.Name != "component") continue;
                var idNode = component["assemblyIdentity"];
                if (idNode is not null) result.Add(idNode);
            }
        }
        return result;
    }

    private static AssemblyIdentity ReadAssemblyIdentity(XmlNode n) => new()
    {
        Name                  = XmlAttr(n, "name"),
        Type                  = XmlAttr(n, "type"),
        Language              = XmlAttr(n, "language"),
        BuildType             = XmlAttr(n, "buildType"),
        ProcessorArchitecture = XmlAttr(n, "processorArchitecture"),
        Version               = XmlAttr(n, "version"),
        PublicKeyToken        = XmlAttr(n, "publicKeyToken"),
        VersionScope          = XmlAttr(n, "versionScope"),
    };

    // -------------------------------------------------------------------------
    // Context menu state
    // -------------------------------------------------------------------------
    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var packages = GetSelectedPackages();
        bool any = packages.Count > 0;
        bool single = packages.Count == 1;

        _miUninstall.Visible        = single;
        _miUninstallAll.Visible     = packages.Count > 1;
        _miCopyNames.Visible        = any;
        _miCopyCommands.Visible     = any;

        bool hasVisible   = packages.Any(p => p.CbsVisibility == 1);
        bool hasInvisible = packages.Any(p => p.CbsVisibility != 1);
        bool hasChanged   = packages.Any(p => p.DefaultCbsVisibility != p.CbsVisibility);

        _miMakeVisible.Visible             = hasInvisible;
        _miMakeInvisible.Visible           = hasVisible;
        _miRestoreDefaultVisibility.Visible= hasChanged;
        _miVisibility.Visible              = _miMakeVisible.Visible
                                          || _miMakeInvisible.Visible
                                          || _miRestoreDefaultVisibility.Visible;
    }

    // -------------------------------------------------------------------------
    // Multi-select tree helpers
    // -------------------------------------------------------------------------
    private void OnNodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        bool ctrl  = (ModifierKeys & Keys.Control) != 0;
        bool shift = (ModifierKeys & Keys.Shift)   != 0;

        if (e.Button == MouseButtons.Right)
        {
            // Right-click: if the clicked node is not in selection, replace selection
            if (!_selectedNodes.Contains(e.Node))
            {
                _selectedNodes.Clear();
                _selectedNodes.Add(e.Node);
                _anchorNode = e.Node;
            }
        }
        else if (ctrl)
        {
            if (!_selectedNodes.Remove(e.Node))
                _selectedNodes.Add(e.Node);
            _anchorNode = e.Node;
        }
        else if (shift && _anchorNode is not null)
        {
            // Range select: select everything between anchor and clicked node
            var allVisible = new List<TreeNode>();
            ForEachNode(_tvPackages.Nodes, n => allVisible.Add(n));
            int a = allVisible.IndexOf(_anchorNode);
            int b = allVisible.IndexOf(e.Node);
            if (a > b) (a, b) = (b, a);
            _selectedNodes.Clear();
            for (int i = a; i <= b; i++) _selectedNodes.Add(allVisible[i]);
        }
        else
        {
            _selectedNodes.Clear();
            _selectedNodes.Add(e.Node);
            _anchorNode = e.Node;
        }

        RefreshNodeColors();
        _tvPackages.SelectedNode = e.Node; // keep TreeView's own indicator on the last-clicked node
    }

    private void RefreshNodeColors()
    {
        ForEachNode(_tvPackages.Nodes, n =>
        {
            bool sel = _selectedNodes.Contains(n);
            n.BackColor = sel ? SystemColors.Highlight : Color.Transparent;
            n.ForeColor = sel ? SystemColors.HighlightText : NodeForeColor(n);
        });
    }

    // -------------------------------------------------------------------------
    // Menu item context fields (populated by InitializeComponent)
    // -------------------------------------------------------------------------
    private ToolStripMenuItem _miUninstall      = null!;
    private ToolStripMenuItem _miUninstallAll   = null!;
    private ToolStripMenuItem _miCopyNames      = null!;
    private ToolStripMenuItem _miCopyCommands   = null!;
    private ToolStripMenuItem _miVisibility     = null!;
    private ToolStripMenuItem _miMakeVisible    = null!;
    private ToolStripMenuItem _miMakeInvisible  = null!;
    private ToolStripMenuItem _miRestoreDefaultVisibility = null!;

    // -------------------------------------------------------------------------
    // InitializeComponent (manual, equivalent to .dfm)
    // -------------------------------------------------------------------------
    private void InitializeComponent()
    {
        Text            = "CBSEnum";
        Size            = new Size(1000, 700);
        MinimumSize     = new Size(600, 400);
        StartPosition   = FormStartPosition.CenterScreen;

        // ---- Left panel: tree + filter ----
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 500 };

        // Top options bar
        var topBar = new FlowLayoutPanel
        {
            Dock      = DockStyle.Top,
            Height    = 28,
            FlowDirection = FlowDirection.LeftToRight,
            Padding   = new Padding(2),
        };

        _cbShowWow64  = new CheckBox { Text = "WOW64",   Checked = false, AutoSize = true };
        _cbShowKb     = new CheckBox { Text = "KB pkgs", Checked = false, AutoSize = true };
        _cbShowHidden = new CheckBox { Text = "Hidden",  Checked = false, AutoSize = true };
        _edtFilter    = new TextBox  { Width = 120, PlaceholderText = "Filter..." };

        _cbShowWow64.CheckedChanged  += (_, _) => UpdateNodeVisibility();
        _cbShowKb.CheckedChanged     += (_, _) => UpdateNodeVisibility();
        _cbShowHidden.CheckedChanged += (_, _) => UpdateNodeVisibility();
        _edtFilter.TextChanged       += (_, _) => UpdateNodeVisibility();
        _edtFilter.KeyDown           += (_, e) => { if (e.KeyCode == Keys.Escape) _edtFilter.Text = ""; };

        topBar.Controls.AddRange(new Control[]
            { _cbShowWow64, _cbShowKb, _cbShowHidden,
              new Label { Text = "Filter:", AutoSize = true, Padding = new Padding(4, 5, 0, 0) },
              _edtFilter });

        // Grouping radio buttons
        var groupBar = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 24,
            FlowDirection = FlowDirection.LeftToRight,
            Padding       = new Padding(2),
        };
        _rbGroupEachPart      = new RadioButton { Text = "Each part",      AutoSize = true, Checked = true };
        _rbGroupDistinctParts = new RadioButton { Text = "Distinct parts", AutoSize = true };
        _rbGroupFlat          = new RadioButton { Text = "Flat",           AutoSize = true };

        _rbGroupEachPart.CheckedChanged      += (_, _) => { if (_rbGroupEachPart.Checked)      Reload(); };
        _rbGroupDistinctParts.CheckedChanged += (_, _) => { if (_rbGroupDistinctParts.Checked) Reload(); };
        _rbGroupFlat.CheckedChanged          += (_, _) => { if (_rbGroupFlat.Checked)          Reload(); };

        groupBar.Controls.AddRange(new Control[]
            { new Label { Text = "Group:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) },
              _rbGroupEachPart, _rbGroupDistinctParts, _rbGroupFlat });

        // Tree view
        _tvPackages = new TreeView
        {
            Dock            = DockStyle.Fill,
            HideSelection   = false,
            FullRowSelect   = true,
            BorderStyle     = BorderStyle.FixedSingle,
        };
        _tvPackages.NodeMouseClick  += OnNodeMouseClick;
        _tvPackages.AfterSelect += (_, _) =>
        {
            if (_pageInfo.SelectedTab == _tsInfo)           LoadInfoTab();
            else if (_pageInfo.SelectedTab == _tsResources) LoadResourcesTab();
        };

        leftPanel.Controls.Add(_tvPackages);
        leftPanel.Controls.Add(groupBar);
        leftPanel.Controls.Add(topBar);

        // ---- Right panel: info tabs ----
        _tsInfo = new TabPage("Info");
        _lblDescription = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = false,
            Height    = 100,
            Text      = "",
        };
        _lbUpdates = new ListBox { Dock = DockStyle.Fill };
        _tsInfo.Controls.Add(_lbUpdates);
        _tsInfo.Controls.Add(_lblDescription);

        _tsResources = new TabPage("Resources");
        // Open the assembly database (created next to the exe, same as the Delphi original)
        string dbPath = Path.Combine(OsUtils.AppFolder(), "assembly.db");
        try
        {
            _db = new AssemblyDb();
            _db.Open(dbPath);
            _resourcesView = new AssemblyResourcesView
            {
                Db               = _db,
                ShowDependencies = true,
                Dock             = DockStyle.Fill,
            };
            _tsResources.Controls.Add(_resourcesView);
        }
        catch
        {
            _tsResources.Controls.Add(new Label
            {
                Text      = $"Could not open assembly database:\n{dbPath}\nRun 'Rebuild assembly database' from the menu first.",
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            });
        }

        _pageInfo = new TabControl { Dock = DockStyle.Fill };
        _pageInfo.TabPages.Add(_tsInfo);
        _pageInfo.TabPages.Add(_tsResources);
        _pageInfo.SelectedIndexChanged += (_, _) =>
        {
            if (_pageInfo.SelectedTab == _tsInfo)      LoadInfoTab();
            else if (_pageInfo.SelectedTab == _tsResources) LoadResourcesTab();
        };

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_pageInfo);

        // ---- Context menu ----
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Opening += OnContextMenuOpening;

        _miUninstall     = new ToolStripMenuItem("Uninstall");
        _miUninstallAll  = new ToolStripMenuItem("Uninstall all selected");
        _miCopyNames     = new ToolStripMenuItem("Copy package names");
        _miCopyCommands  = new ToolStripMenuItem("Copy uninstallation commands");
        _miMakeVisible   = new ToolStripMenuItem("Make visible");
        _miMakeInvisible = new ToolStripMenuItem("Make invisible");
        _miRestoreDefaultVisibility = new ToolStripMenuItem("Restore default visibility");
        _miVisibility    = new ToolStripMenuItem("Visibility",
            null,
            _miMakeVisible, _miMakeInvisible, _miRestoreDefaultVisibility);

        var miReload = new ToolStripMenuItem("Reload");
        miReload.Click += (_, _) => Reload();

        _miUninstall.Click    += OnUninstall;
        _miUninstallAll.Click += OnUninstallAll;
        _miCopyNames.Click    += OnCopyPackageNames;
        _miCopyCommands.Click += OnCopyUninstallationCommands;
        _miMakeVisible.Click  += (_, _) => SetCbsVisibility(GetSelectedPackages(), 1);
        _miMakeInvisible.Click+= (_, _) => SetCbsVisibility(GetSelectedPackages(), 2);
        _miRestoreDefaultVisibility.Click += (_, _) => SetCbsVisibility(GetSelectedPackages(), -1);

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _miUninstall, _miUninstallAll,
            new ToolStripSeparator(),
            _miCopyNames, _miCopyCommands,
            new ToolStripSeparator(),
            _miVisibility,
            new ToolStripSeparator(),
            miReload,
        });
        _tvPackages.ContextMenuStrip = _contextMenu;

        // ---- Main menu ----
        var menu = new MenuStrip();

        // File
        var miFile         = new ToolStripMenuItem("&File");
        var miSaveAll      = new ToolStripMenuItem("Save package list...");
        var miSaveSelected = new ToolStripMenuItem("Save selected package list...");
        var miExit         = new ToolStripMenuItem("E&xit");
        miSaveAll.Click      += (_, _) => SavePackageList(GetAllPackages().Select(p => p.Name).ToArray());
        miSaveSelected.Click += (_, _) => SavePackageList(GetSelectedPackageNames());
        miExit.Click         += (_, _) => Close();
        miFile.DropDownItems.AddRange(new ToolStripItem[]
            { miSaveAll, miSaveSelected, new ToolStripSeparator(), miExit });

        // Edit
        var miEdit           = new ToolStripMenuItem("&Edit");
        var miTakeOwnership  = new ToolStripMenuItem("Take Ownership...");
        var miDecoupleAll    = new ToolStripMenuItem("Decouple all packages...");
        var miDecoupleSelected = new ToolStripMenuItem("Decouple selected packages...");
        var miMakeAllVisible = new ToolStripMenuItem("Make all visible");
        var miMakeAllInvisible = new ToolStripMenuItem("Make all invisible");
        var miRestoreAllVis  = new ToolStripMenuItem("Restore all default visibility");
        var miUninstallByList = new ToolStripMenuItem("Uninstall by list...");

        miTakeOwnership.Click   += (_, _) => LaunchJob("Taking ownership...", new TakeOwnershipJob());
        miDecoupleAll.Click     += (_, _) => LaunchJob("Decoupling...", new DecouplePackagesJob(null));
        miDecoupleSelected.Click+= (_, _) =>
        {
            var pkgs = GetSelectedPackageNames();
            if (pkgs.Length == 0) return;
            LaunchJob("Decoupling...", new DecouplePackagesJob(pkgs));
        };
        miMakeAllVisible.Click  += (_, _) => SetCbsVisibility(GetAllPackages(), 1);
        miMakeAllInvisible.Click+= (_, _) => SetCbsVisibility(GetAllPackages(), 2);
        miRestoreAllVis.Click   += (_, _) => SetCbsVisibility(GetAllPackages(), -1);
        miUninstallByList.Click += OnUninstallByList;

        miEdit.DropDownItems.AddRange(new ToolStripItem[]
        {
            miTakeOwnership,
            new ToolStripSeparator(),
            miDecoupleAll, miDecoupleSelected,
            new ToolStripSeparator(),
            miMakeAllVisible, miMakeAllInvisible, miRestoreAllVis,
            new ToolStripSeparator(),
            miUninstallByList,
        });

        // Service
        var miService      = new ToolStripMenuItem("&Service");
        var miDiskCleanup  = new ToolStripMenuItem("Disk Cleanup");
        var miOptFeatures  = new ToolStripMenuItem("Optional Features");
        var miDismCleanup  = new ToolStripMenuItem("DISM Component Cleanup");
        var miOpenRegistry = new ToolStripMenuItem("Open CBS Registry");

        miDiskCleanup.Click  += (_, _) => OsUtils.StartProcess(Path.Combine(OsUtils.GetSystemDir(), "cleanmgr.exe"), "cleanmgr.exe");
        miOptFeatures.Click  += (_, _) => OsUtils.StartProcess(Path.Combine(OsUtils.GetSystemDir(), "OptionalFeatures.exe"), "OptionalFeatures.exe");
        miDismCleanup.Click  += (_, _) => OsUtils.StartProcess(
            Path.Combine(OsUtils.GetSystemDir(), "dism.exe"),
            "dism.exe /Online /Cleanup-Image /StartComponentCleanup");
        miOpenRegistry.Click += (_, _) =>
            OsUtils.RegeditOpenAndNavigate(
                @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Component Based Servicing");

        miService.DropDownItems.AddRange(new ToolStripItem[]
            { miDiskCleanup, miOptFeatures, miDismCleanup, new ToolStripSeparator(), miOpenRegistry });

        menu.Items.AddRange(new ToolStripItem[] { miFile, miEdit, miService });
        MainMenuStrip = menu;

        // ---- Splitter layout ----
        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };

        Controls.Add(rightPanel);
        Controls.Add(splitter);
        Controls.Add(leftPanel);
        Controls.Add(menu);
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------
    private void OnUninstall(object? sender, EventArgs e)
    {
        var names = GetSelectedPackageNames();
        if (names.Length != 1) return;
        if (MessageBox.Show(
                $"Do you really want to uninstall\n{names[0]}?\n\n"
                + "After uninstalling, it will be impossible to install again without repairing Windows.",
                "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        DismUninstall(names);
        Reload();
    }

    private void OnUninstallAll(object? sender, EventArgs e)
    {
        var names = GetSelectedPackageNames();
        if (names.Length == 0) return;

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);

        string confirmText = names.Length == 1
            ? $"Do you really want to uninstall\n{names[0]}?"
            : $"Do you really want to uninstall {names.Length} packages?\n{string.Join("\n", names)}";
        confirmText += "\n\nAfter uninstalling, it will be impossible to install again without repairing Windows.";

        if (MessageBox.Show(confirmText, "Confirm uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        DismUninstall(names);
        Reload();
    }

    private void OnCopyPackageNames(object? sender, EventArgs e)
    {
        var names = GetSelectedPackageNames();
        if (names.Length == 0) return;
        Clipboard.SetText(string.Join("\r\n", names));
    }

    private void OnCopyUninstallationCommands(object? sender, EventArgs e)
    {
        var names = GetSelectedPackageNames();
        if (names.Length == 0) return;
        string cmd = "dism.exe /Online /Remove-Package"
            + string.Join("", names.Select(n => $" /PackageName={n}"));
        Clipboard.SetText(cmd);
    }

    private void OnUninstallByList(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title  = "Open uninstall list",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        if (_packages is null) return;

        var matched = new List<Package>();
        foreach (string rawLine in File.ReadAllLines(dlg.FileName))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            int hashPos = line.IndexOf('#');
            if (hashPos >= 0) line = line[..hashPos].Trim();
            if (line.Length == 0) continue;

            _packages.SelectMatching(line, matched);
        }

        var packageNames = matched.Select(p => p.Name).ToArray();
        if (packageNames.Length == 0)
        {
            MessageBox.Show("Nothing to remove.", "Uninstall by list", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                $"{packageNames.Length} packages are going to be removed. Do you really want to do this?",
                "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        DismUninstall(packageNames);
        Reload();
    }

    private void LaunchJob(string caption, ProcessingThread job)
    {
        _jobProcessorForm.Text = caption;
        _jobProcessorForm.Show(this);
        _jobProcessorForm.Process(job);
    }
}
