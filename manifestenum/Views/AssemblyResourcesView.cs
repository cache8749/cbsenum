// Translation of AssemblyResourcesView.pas + DelayLoadTree.pas
//
// In the Delphi original TAssemblyResourcesForm inherits TDelayLoadTree (a TForm) and is
// docked into a tab page with ManualDock. In WinForms the cleanest equivalent is a UserControl
// that can be dropped into any container. The delay-load pattern maps to TreeView's BeforeExpand.

using System.ComponentModel;

namespace ManifestEnum;

/// <summary>
/// Shows the WinSxS resources associated with a set of assemblies (files, registry values,
/// tasks, services, and optionally dependency assemblies).
/// Drop onto a Panel or TabPage; set <see cref="Db"/> then call <see cref="Reload"/>.
/// </summary>
public sealed class AssemblyResourcesView : UserControl
{
    // ---- Node types (mirrors TNodeType) ----
    private enum NodeKind { Assembly, Folder, File, RegistryValue, Task, Service }

    private sealed class NodeTag
    {
        public NodeKind Kind       { get; init; }
        public string   Name       { get; init; } = "";
        public long     AssemblyId { get; init; }
        public long     ResourceId { get; init; }
        public bool     Touched    { get; set; }
    }

    // ---- Controls ----
    private readonly TreeView _tree;

    // ---- State ----
    private AssemblyDb? _db;
    private readonly List<long> _assemblies = new();
    private bool _showDependencies;
    private bool _flatTree;

    // ---- Properties ----

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AssemblyDb? Db
    {
        get => _db;
        set { _db = value; if (IsHandleCreated) Reload(); }
    }

    /// <summary>The assembly IDs to display. Populate before calling <see cref="Reload"/>.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<long> Assemblies => _assemblies;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowDependencies
    {
        get => _showDependencies;
        set { if (_showDependencies != value) { _showDependencies = value; Reload(); } }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FlatTree
    {
        get => _flatTree;
        set { if (_flatTree != value) { _flatTree = value; Reload(); } }
    }

    // ---- Constructor ----

    public AssemblyResourcesView()
    {
        _tree = new TreeView
        {
            Dock       = DockStyle.Fill,
            HideSelection = false,
            ShowLines  = true,
            Font       = new Font("Segoe UI", 9f),
        };
        _tree.BeforeExpand  += OnBeforeExpand;
        _tree.AfterSelect   += (_, _) => { };   // reserved for future use

        Controls.Add(_tree);
        Dock = DockStyle.Fill;
    }

    // ---- Public ----

    public void Clear() => _tree.Nodes.Clear();

    public void Reload()
    {
        Clear();
        if (_db is null || _assemblies.Count == 0) return;

        _tree.BeginUpdate();
        try
        {
            // Mirror of DelayLoad(nil) — populate the root
            if (_assemblies.Count > 1)
            {
                // Multiple roots: show one assembly node per entry
                foreach (long id in _assemblies)
                {
                    var data = _db.Assemblies.GetAssembly(id);
                    var node = AddAssemblyNode(null, data);
                    Touch(node);            // immediately expand one level
                }
            }
            else
            {
                // Single assembly: show its resources directly at root
                PopulateAssemblyResources(null, _assemblies[0]);
            }
        }
        finally { _tree.EndUpdate(); }
    }

    // ---- Delay-load: expand on demand ----

    private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is not NodeTag tag || tag.Touched) return;
        _tree.BeginUpdate();
        try { Touch(e.Node); }
        finally { _tree.EndUpdate(); }
    }

    /// <summary>Populate a node's children if not already done.</summary>
    private void Touch(TreeNode? node)
    {
        if (node is null)
        {
            // Root expansion — already handled in Reload()
            return;
        }

        if (node.Tag is not NodeTag tag || tag.Touched) return;
        tag.Touched = true;

        // Remove the placeholder child that made the expand arrow appear
        if (node.Nodes.Count == 1 && node.Nodes[0].Tag is null)
            node.Nodes.Clear();

        if (tag.Kind == NodeKind.Assembly)
            PopulateAssemblyResources(node, tag.AssemblyId);
    }

    // ---- Population ----

    private void PopulateAssemblyResources(TreeNode? parent, long assemblyId)
    {
        if (_db is null) return;

        var targetParent = _flatTree ? null : parent;

        // Folders
        foreach (var (folderId, folderRef) in _db.Files.GetAssemblyFolders(assemblyId))
            AddFolderNode(targetParent, folderId, folderRef);

        // Files
        foreach (var file in _db.Files.GetAssemblyFiles(assemblyId))
            AddFileNode(targetParent, file);

        // Registry values
        foreach (var val in _db.Registry.GetAssemblyValues(assemblyId))
            AddRegistryValueNode(targetParent, val);

        // Tasks
        foreach (var (_, folderId, name) in _db.GetAssemblyTasks(assemblyId))
            AddTaskNode(targetParent, folderId, name);

        // Services
        foreach (var svc in _db.Services.GetAssemblyServices(assemblyId).Values)
            AddServiceNode(targetParent, svc);

        // Dependencies (optional)
        if (_showDependencies)
            foreach (var dep in _db.GetDependencies(assemblyId).Values)
                AddAssemblyNode(targetParent, dep);
    }

    // ---- Node factories ----

    private TreeNode AddAssemblyNode(TreeNode? parent, AssemblyData data)
    {
        var node = NewNode(parent, data.Identity.ToString(), NodeKind.Assembly, data.Id, 0);
        // Add a placeholder child so the expand arrow shows; Touch() will replace it
        node.Nodes.Add(new TreeNode("…"));
        return node;
    }

    private TreeNode AddFolderNode(TreeNode? parent, long folderId, FolderReferenceData _)
    {
        string path = _db!.Files.GetFolderPath(folderId);
        return NewNode(parent, $"[Dir] {path}", NodeKind.Folder, 0, folderId, leafNode: true);
    }

    private TreeNode AddFileNode(TreeNode? parent, FileEntryData file)
    {
        string fullName = _db!.Files.GetFileFullDestinationName(file);
        return NewNode(parent, $"[File] {fullName}", NodeKind.File, 0, file.Id, leafNode: true);
    }

    private TreeNode AddRegistryValueNode(TreeNode? parent, RegistryValueData val)
    {
        string keyPath = _db!.Registry.GetKeyPath(val.Key);
        string text = $"[Reg] {keyPath}\\{val.Name} = {val.Value}";
        return NewNode(parent, text, NodeKind.RegistryValue, 0, val.Id, leafNode: true);
    }

    private TreeNode AddTaskNode(TreeNode? parent, long folderId, string name)
    {
        string path = _db!.GetTaskFolderPath(folderId);
        string text = $"[Task] {path}\\{name}";
        return NewNode(parent, text, NodeKind.Task, 0, 0, leafNode: true);
    }

    private TreeNode AddServiceNode(TreeNode? parent, ServiceEntryData svc)
        => NewNode(parent, $"[Service] {svc.Name}", NodeKind.Service, 0, 0, leafNode: true);

    private TreeNode NewNode(TreeNode? parent, string text, NodeKind kind,
        long assemblyId, long resourceId, bool leafNode = false)
    {
        var node = new TreeNode(text)
        {
            Tag = new NodeTag
            {
                Kind       = kind,
                Name       = text,
                AssemblyId = assemblyId,
                ResourceId = resourceId,
                Touched    = leafNode,   // leaves need no further loading
            }
        };

        if (parent is null)
            _tree.Nodes.Add(node);
        else
            parent.Nodes.Add(node);

        return node;
    }
}
