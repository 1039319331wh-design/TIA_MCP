using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace TiaOpennessWorker
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            try
            {
                var command = args.Length == 0 ? "status" : args[0];
                var reader = new TiaReader();
                object data;
                switch (command)
                {
                    case "status": data = reader.GetStatus(); break;
                    case "projects": data = reader.ListProjects(); break;
                    case "devices": data = reader.ListDevices(); break;
                    case "blocks": data = reader.ListBlocks(); break;
                    case "tag-tables": data = reader.ListTagTables(); break;
                    case "export-tag-table":
                        if (args.Length < 3) throw new ArgumentException("export-tag-table requires PLC name and tag table name.");
                        data = reader.ExportTagTable(args[1], args[2]);
                        break;
                    case "export-block":
                        if (args.Length < 3) throw new ArgumentException("export-block requires PLC name and block name.");
                        data = reader.ExportBlock(args[1], args[2], args.Length > 3 ? args[3] : null);
                        break;
                    case "import-block":
                        if (args.Length < 5) throw new ArgumentException("import-block requires PLC name, block name, group path, and XML file path.");
                        data = reader.ImportBlock(args[1], args[2], args[3], args[4]);
                        break;
                    case "compile-plc":
                        if (args.Length < 2) throw new ArgumentException("compile-plc requires PLC name.");
                        data = reader.CompilePlc(args[1]);
                        break;
                    case "inspect-import":
                        if (args.Length < 4) throw new ArgumentException("inspect-import requires PLC name, block name, and group path.");
                        data = reader.InspectImport(args[1], args[2], args[3]);
                        break;
                    case "inspect-external-sources":
                        if (args.Length < 2) throw new ArgumentException("inspect-external-sources requires PLC name.");
                        data = reader.InspectExternalSources(args[1]);
                        break;
                    case "import-scl-source":
                        if (args.Length < 4) throw new ArgumentException("import-scl-source requires PLC name, source name, and SCL file path.");
                        data = reader.ImportSclSource(args[1], args[2], args[3]);
                        break;
                    case "delete-block":
                        if (args.Length < 4) throw new ArgumentException("delete-block requires PLC name, block name, and group path.");
                        data = reader.DeleteBlock(args[1], args[2], args[3]);
                        break;
                    case "generate-block-source":
                        if (args.Length < 5) throw new ArgumentException("generate-block-source requires PLC name, block name, group path, and output file path.");
                        data = reader.GenerateBlockSource(args[1], args[2], args[3], args[4]);
                        break;
                    case "save-project":
                        if (args.Length < 2) throw new ArgumentException("save-project requires exact project name.");
                        data = reader.SaveProject(args[1]);
                        break;
                    default: throw new ArgumentException("Unknown command: " + command);
                }
                Console.Out.Write(json.Serialize(new Dictionary<string, object> { { "ok", true }, { "data", data } }));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Out.Write(json.Serialize(new Dictionary<string, object> {
                    { "ok", false }, { "error", ex.GetBaseException().Message }, { "type", ex.GetBaseException().GetType().FullName }
                }));
                return 1;
            }
        }
    }

    internal sealed class TiaReader
    {
        private Assembly assembly;
        private string loadedPath;

        public object GetStatus()
        {
            EnsureAssembly();
            var processes = GetProcesses();
            return Row("mode", "read-only", "tiaVersion", DetectVersion(loadedPath), "opennessDll", loadedPath, "processCount", processes.Count);
        }

        public object ListProjects()
        {
            EnsureAssembly();
            var rows = new List<object>();
            var processes = GetProcesses();
            for (var i = 0; i < processes.Count; i++)
            {
                var portal = Invoke(processes[i], "Attach");
                var project = portal == null ? null : First(Get(portal, "Projects"));
                rows.Add(Row("index", i, "processId", Get(processes[i], "Id"), "project", project == null ? null : DescribeProject(project)));
            }
            return rows;
        }

        public object ListDevices()
        {
            return WithProject(project =>
            {
                var rows = new List<object>();
                foreach (var device in Enumerate(Get(project, "Devices"))) FlattenDevice(device, null, rows);
                return rows;
            });
        }

        public object ListBlocks()
        {
            return WithProject(project =>
            {
                var rows = new List<object>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems"))) WalkDeviceItemForBlocks(item, rows);
                return rows;
            });
        }

        public object ExportBlock(string plcName, string blockName, string groupPath)
        {
            return WithProject(project =>
            {
                var matches = new List<BlockMatch>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        FindBlocks(item, plcName, blockName, groupPath, matches);
                if (matches.Count == 0) throw new InvalidOperationException("Block not found: " + plcName + "/" + blockName);
                if (matches.Count > 1)
                    throw new InvalidOperationException("Multiple blocks matched. Specify group path. Matches: " +
                        string.Join(", ", matches.Select(m => m.Group + "/" + m.Name)));

                var match = matches[0];
                var tempPath = Path.Combine(Path.GetTempPath(), "tia-block-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    var optionType = assembly.GetType("Siemens.Engineering.ExportOptions");
                    if (optionType == null) throw new InvalidOperationException("ExportOptions type not found.");
                    var method = match.Block.GetType().GetMethod("Export", new[] { typeof(FileInfo), optionType });
                    if (method == null) throw new MissingMethodException(match.Block.GetType().FullName, "Export(FileInfo, ExportOptions)");
                    method.Invoke(match.Block, new[] { (object)new FileInfo(tempPath), Enum.Parse(optionType, "WithDefaults") });
                    return Row("plc", match.Plc, "group", match.Group, "name", match.Name,
                        "type", match.Block.GetType().Name, "xml", File.ReadAllText(tempPath));
                }
                finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
            });
        }

        public object ExportTagTable(string plcName, string tableName)
        {
            return WithProject(project =>
            {
                object software = null;
                foreach (var device in Enumerate(Get(project, "Devices")))
                {
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                    {
                        software = FindSoftware(item, plcName);
                        if (software != null) break;
                    }
                    if (software != null) break;
                }
                if (software == null) throw new InvalidOperationException("PLC software not found: " + plcName);
                var root = Get(software, "TagTableGroup");
                if (root == null) throw new InvalidOperationException("PLC tag-table root was not found.");
                var matches = new List<object>();
                FindTagTables(root, tableName, matches);
                if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one tag table named '" + tableName + "', found " + matches.Count + ".");

                var tempPath = Path.Combine(Path.GetTempPath(), "tia-tag-table-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    var optionType = assembly.GetType("Siemens.Engineering.ExportOptions");
                    if (optionType == null) throw new InvalidOperationException("ExportOptions type not found.");
                    var method = matches[0].GetType().GetMethod("Export", new[] { typeof(FileInfo), optionType });
                    if (method == null) throw new MissingMethodException(matches[0].GetType().FullName, "Export(FileInfo, ExportOptions)");
                    method.Invoke(matches[0], new[] { (object)new FileInfo(tempPath), Enum.Parse(optionType, "WithDefaults") });
                    return Row("plc", plcName, "table", tableName, "xml", File.ReadAllText(tempPath));
                }
                finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
            });
        }

        public object ListTagTables()
        {
            return WithProject(project =>
            {
                var rows = new List<object>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        WalkDeviceItemForTagTables(item, rows);
                return rows;
            });
        }

        private void WalkDeviceItemForTagTables(object item, List<object> rows)
        {
            var serviceType = assembly.GetType("Siemens.Engineering.HW.Features.SoftwareContainer");
            if (serviceType != null)
            {
                var container = GetService(item, serviceType);
                var software = container == null ? null : Get(container, "Software");
                var root = software == null ? null : Get(software, "TagTableGroup");
                if (root != null)
                    WalkTagTableGroup(root, Convert.ToString(Get(software, "Name")) ?? Convert.ToString(Get(item, "Name")) ?? "PLC", "", rows);
            }
            foreach (var child in Enumerate(Get(item, "DeviceItems"))) WalkDeviceItemForTagTables(child, rows);
        }

        private static void WalkTagTableGroup(object group, string plc, string parent, List<object> rows)
        {
            var groupName = Convert.ToString(Get(group, "Name")) ?? "PLC tags";
            var path = string.IsNullOrEmpty(parent) ? groupName : parent + "/" + groupName;
            foreach (var table in Enumerate(Get(group, "TagTables")))
                rows.Add(Row("plc", plc, "group", path, "name", Get(table, "Name")));
            foreach (var child in Enumerate(Get(group, "Groups"))) WalkTagTableGroup(child, plc, path, rows);
        }

        private static void FindTagTables(object group, string tableName, List<object> matches)
        {
            foreach (var table in Enumerate(Get(group, "TagTables")))
                if (string.Equals(Convert.ToString(Get(table, "Name")), tableName, StringComparison.OrdinalIgnoreCase)) matches.Add(table);
            foreach (var child in Enumerate(Get(group, "Groups"))) FindTagTables(child, tableName, matches);
        }

        public object ImportBlock(string plcName, string blockName, string groupPath, string xmlPath)
        {
            if (!File.Exists(xmlPath)) throw new FileNotFoundException("Import XML file not found.", xmlPath);
            return WithProject(project =>
            {
                var matches = new List<BlockMatch>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        FindBlocks(item, plcName, blockName, groupPath, matches);
                if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one target block, found " + matches.Count + ".");

                var composition = Get(matches[0].GroupObject, "Blocks");
                if (composition == null) throw new InvalidOperationException("Target block composition was not found.");
                var optionType = assembly.GetType("Siemens.Engineering.ImportOptions");
                if (optionType == null) throw new InvalidOperationException("ImportOptions type not found.");
                var method = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), optionType });
                if (method == null) throw new MissingMethodException(composition.GetType().FullName, "Import(FileInfo, ImportOptions)");
                var imported = method.Invoke(composition, new[] { (object)new FileInfo(xmlPath), Enum.Parse(optionType, "Override") });
                return Row("plc", plcName, "group", matches[0].Group, "name", blockName,
                    "importedCount", Enumerate(imported).Count());
            });
        }

        public object CompilePlc(string plcName)
        {
            return WithProject(project =>
            {
                object software = null;
                foreach (var device in Enumerate(Get(project, "Devices")))
                {
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                    {
                        software = FindSoftware(item, plcName);
                        if (software != null) break;
                    }
                    if (software != null) break;
                }
                if (software == null) throw new InvalidOperationException("PLC software not found: " + plcName);
                var compilableType = assembly.GetType("Siemens.Engineering.Compiler.ICompilable");
                if (compilableType == null) throw new InvalidOperationException("ICompilable type not found.");
                var compilable = GetService(software, compilableType);
                if (compilable == null) throw new InvalidOperationException("PLC software does not provide ICompilable: " + plcName);
                var result = Invoke(compilable, "Compile");
                if (result == null) throw new InvalidOperationException("Compile returned no result.");
                var messages = new List<object>();
                CollectCompilerMessages(Get(result, "Messages"), messages, 500);
                return Row("plc", plcName, "state", Convert.ToString(Get(result, "State")),
                    "errorCount", Get(result, "ErrorCount"), "warningCount", Get(result, "WarningCount"),
                    "messages", messages);
            });
        }

        public object InspectImport(string plcName, string blockName, string groupPath)
        {
            return WithProject(project =>
            {
                var matches = new List<BlockMatch>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        FindBlocks(item, plcName, blockName, groupPath, matches);
                if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one target block, found " + matches.Count + ".");
                var composition = Get(matches[0].GroupObject, "Blocks");
                if (composition == null) throw new InvalidOperationException("Target block composition was not found.");
                var methods = composition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name == "Import")
                    .Select(method => method.ToString()).ToArray();
                return Row("compositionType", composition.GetType().FullName, "methods", methods);
            });
        }

        public object InspectExternalSources(string plcName)
        {
            return WithProject(project =>
            {
                var software = FindPlcSoftware(project, plcName);
                var group = Get(software, "ExternalSourceGroup");
                var sources = group == null ? null : Get(group, "ExternalSources");
                if (sources == null) throw new InvalidOperationException("PLC external-source composition was not found.");
                return Row("plc", plcName, "compositionType", sources.GetType().FullName,
                    "groupType", group.GetType().FullName,
                    "groupGenerateMethods", group.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(method => method.Name.IndexOf("Generate", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(method => method.ToString()).ToArray(),
                    "createMethods", sources.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(method => method.Name == "CreateFromFile").Select(method => method.ToString()).ToArray(),
                    "generateMethods", sources.GetType().Assembly.GetType("Siemens.Engineering.SW.ExternalSources.PlcExternalSource")
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(method => method.Name == "GenerateBlocksFromSource")
                        .Select(method => method.ToString()).ToArray(),
                    "generateBlockOptions", Enum.GetNames(sources.GetType().Assembly.GetType("Siemens.Engineering.SW.ExternalSources.GenerateBlockOption")));
            });
        }

        public object ImportSclSource(string plcName, string sourceName, string sourcePath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("SCL source file not found.", sourcePath);
            return WithProject(project =>
            {
                var software = FindPlcSoftware(project, plcName);
                var group = Get(software, "ExternalSourceGroup");
                var sources = group == null ? null : Get(group, "ExternalSources");
                if (sources == null) throw new InvalidOperationException("PLC external-source composition was not found.");
                var create = sources.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "CreateFromFile" && method.GetParameters().Length == 2);
                if (create == null) throw new MissingMethodException(sources.GetType().FullName, "CreateFromFile");
                var fileArgument = create.GetParameters()[1].ParameterType == typeof(string)
                    ? (object)Path.GetFullPath(sourcePath) : new FileInfo(sourcePath);
                var created = create.Invoke(sources, new object[] { sourceName, fileArgument });
                if (created == null) throw new InvalidOperationException("TIA did not return the created external source.");
                var generate = created.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "GenerateBlocksFromSource" && method.GetParameters().Length == 1);
                object result;
                if (generate != null)
                {
                    var optionType = generate.GetParameters()[0].ParameterType;
                    result = generate.Invoke(created, new[] { Enum.Parse(optionType, "None") });
                }
                else
                {
                    generate = created.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(method => method.Name == "GenerateBlocksFromSource" && method.GetParameters().Length == 0);
                    if (generate == null) throw new MissingMethodException(created.GetType().FullName, "GenerateBlocksFromSource");
                    result = generate.Invoke(created, null);
                }
                return Row("plc", plcName, "sourceName", sourceName, "sourcePath", sourcePath,
                    "sourceType", created.GetType().FullName, "generatedResult", Convert.ToString(result));
            });
        }

        public object DeleteBlock(string plcName, string blockName, string groupPath)
        {
            return WithProject(project =>
            {
                var matches = new List<BlockMatch>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        FindBlocks(item, plcName, blockName, groupPath, matches);
                if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one target block, found " + matches.Count + ".");
                Invoke(matches[0].Block, "Delete");
                return Row("plc", plcName, "group", matches[0].Group, "name", blockName, "deleted", true);
            });
        }

        public object GenerateBlockSource(string plcName, string blockName, string groupPath, string outputPath)
        {
            return WithProject(project =>
            {
                var matches = new List<BlockMatch>();
                foreach (var device in Enumerate(Get(project, "Devices")))
                    foreach (var item in Enumerate(Get(device, "DeviceItems")))
                        FindBlocks(item, plcName, blockName, groupPath, matches);
                if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one source block, found " + matches.Count + ".");
                var software = FindPlcSoftware(project, plcName);
                var sourceGroup = Get(software, "ExternalSourceGroup");
                if (sourceGroup == null) throw new InvalidOperationException("PLC external-source group was not found.");
                var generate = sourceGroup.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "GenerateSource" && method.GetParameters().Length == 2);
                if (generate == null) throw new MissingMethodException(sourceGroup.GetType().FullName, "GenerateSource");
                var itemType = generate.GetParameters()[0].ParameterType.GetGenericArguments()[0];
                var items = Array.CreateInstance(itemType, 1);
                items.SetValue(matches[0].Block, 0);
                var fullPath = Path.GetFullPath(outputPath);
                generate.Invoke(sourceGroup, new object[] { items, new FileInfo(fullPath) });
                return Row("plc", plcName, "group", matches[0].Group, "name", blockName,
                    "outputPath", fullPath, "characters", new FileInfo(fullPath).Length);
            });
        }

        private object FindPlcSoftware(object project, string plcName)
        {
            foreach (var device in Enumerate(Get(project, "Devices")))
                foreach (var item in Enumerate(Get(device, "DeviceItems")))
                {
                    var software = FindSoftware(item, plcName);
                    if (software != null) return software;
                }
            throw new InvalidOperationException("PLC software not found: " + plcName);
        }

        public object SaveProject(string projectName)
        {
            EnsureAssembly();
            foreach (var process in GetProcesses())
            {
                var portal = Invoke(process, "Attach");
                foreach (var project in Enumerate(portal == null ? null : Get(portal, "Projects")))
                {
                    var name = Convert.ToString(Get(project, "Name"));
                    if (!string.Equals(name, projectName, StringComparison.Ordinal)) continue;
                    var path = Convert.ToString(Get(project, "Path"));
                    Invoke(project, "Save");
                    return Row("name", name, "path", path, "isModified", Get(project, "IsModified"));
                }
            }
            throw new InvalidOperationException("Open project not found: " + projectName);
        }

        private object FindSoftware(object item, string plcName)
        {
            var serviceType = assembly.GetType("Siemens.Engineering.HW.Features.SoftwareContainer");
            if (serviceType != null)
            {
                var container = GetService(item, serviceType);
                var software = container == null ? null : Get(container, "Software");
                if (software != null && string.Equals(Convert.ToString(Get(software, "Name")), plcName, StringComparison.OrdinalIgnoreCase))
                    return software;
            }
            foreach (var child in Enumerate(Get(item, "DeviceItems")))
            {
                var found = FindSoftware(child, plcName);
                if (found != null) return found;
            }
            return null;
        }

        private static void CollectCompilerMessages(object value, List<object> rows, int limit)
        {
            foreach (var message in Enumerate(value))
            {
                if (rows.Count >= limit) return;
                rows.Add(Row("state", Convert.ToString(Get(message, "State")),
                    "description", Convert.ToString(Get(message, "Description")),
                    "path", Convert.ToString(Get(message, "Path")),
                    "errorCount", Get(message, "ErrorCount"), "warningCount", Get(message, "WarningCount")));
                CollectCompilerMessages(Get(message, "Messages"), rows, limit);
            }
        }

        private T WithProject<T>(Func<object, T> action)
        {
            EnsureAssembly();
            foreach (var process in GetProcesses())
            {
                var portal = Invoke(process, "Attach");
                var project = portal == null ? null : First(Get(portal, "Projects"));
                if (project != null) return action(project);
            }
            throw new InvalidOperationException("No open TIA Portal project was found.");
        }

        private List<object> GetProcesses()
        {
            var type = assembly.GetType("Siemens.Engineering.TiaPortal");
            if (type == null) throw new InvalidOperationException("TiaPortal type not found.");
            var method = type.GetMethod("GetProcesses", BindingFlags.Public | BindingFlags.Static);
            return Enumerate(method == null ? null : method.Invoke(null, null)).ToList();
        }

        private void EnsureAssembly()
        {
            if (assembly != null) return;
            var configured = Environment.GetEnvironmentVariable("TIA_OPENNESS_DLL");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
            for (var version = 21; version >= 16; version--)
            {
                var root = @"C:\Program Files\Siemens\Automation\Portal V" + version + @"\PublicAPI";
                if (!Directory.Exists(root)) continue;
                var files = Directory.GetFiles(root, "Siemens.Engineering.dll", SearchOption.AllDirectories);
                candidates.AddRange(files.Where(path => string.Equals(
                    new FileInfo(path).Directory == null ? null : new FileInfo(path).Directory.Name,
                    "V" + version, StringComparison.OrdinalIgnoreCase)));
                candidates.AddRange(files.Where(path => !string.Equals(
                    new FileInfo(path).Directory == null ? null : new FileInfo(path).Directory.Name,
                    "V" + version, StringComparison.OrdinalIgnoreCase)).OrderByDescending(path => path));
            }
            loadedPath = candidates.FirstOrDefault(File.Exists);
            if (loadedPath == null) throw new FileNotFoundException("Siemens.Engineering.dll not found. Set TIA_OPENNESS_DLL.");
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            assembly = Assembly.LoadFrom(Path.GetFullPath(loadedPath));
        }

        private static string DetectVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            for (var version = 21; version >= 16; version--)
                if (path.IndexOf("Portal V" + version, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("PublicAPI\\V" + version, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "V" + version;
            return "unknown";
        }

        private Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            var directory = Path.GetDirectoryName(loadedPath);
            var candidate = Path.Combine(directory ?? "", new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }

        private static object DescribeProject(object project)
        {
            return Row("name", Get(project, "Name"), "path", Convert.ToString(Get(project, "Path")), "isModified", Get(project, "IsModified"));
        }

        private static void FlattenDevice(object device, string parent, List<object> rows)
        {
            var name = Convert.ToString(Get(device, "Name")) ?? "<unnamed>";
            rows.Add(Row("kind", "device", "name", name, "parent", parent, "type", Convert.ToString(Get(device, "TypeIdentifier"))));
            foreach (var item in Enumerate(Get(device, "DeviceItems"))) FlattenDeviceItem(item, name, rows);
        }

        private static void FlattenDeviceItem(object item, string parent, List<object> rows)
        {
            var name = Convert.ToString(Get(item, "Name")) ?? "<unnamed>";
            rows.Add(Row("kind", "deviceItem", "name", name, "parent", parent, "type", Convert.ToString(Get(item, "TypeIdentifier"))));
            foreach (var child in Enumerate(Get(item, "DeviceItems"))) FlattenDeviceItem(child, name, rows);
        }

        private void WalkDeviceItemForBlocks(object item, List<object> rows)
        {
            var serviceType = assembly.GetType("Siemens.Engineering.HW.Features.SoftwareContainer");
            if (serviceType != null)
            {
                var container = GetService(item, serviceType);
                var software = container == null ? null : Get(container, "Software");
                var group = software == null ? null : Get(software, "BlockGroup");
                if (group != null) WalkBlockGroup(group, Convert.ToString(Get(software, "Name")) ?? Convert.ToString(Get(item, "Name")) ?? "PLC", "", rows);
            }
            foreach (var child in Enumerate(Get(item, "DeviceItems"))) WalkDeviceItemForBlocks(child, rows);
        }

        private void FindBlocks(object item, string requestedPlc, string requestedName, string requestedGroup, List<BlockMatch> matches)
        {
            var serviceType = assembly.GetType("Siemens.Engineering.HW.Features.SoftwareContainer");
            if (serviceType != null)
            {
                var container = GetService(item, serviceType);
                var software = container == null ? null : Get(container, "Software");
                var group = software == null ? null : Get(software, "BlockGroup");
                var plc = software == null ? null : Convert.ToString(Get(software, "Name"));
                if (group != null && string.Equals(plc, requestedPlc, StringComparison.OrdinalIgnoreCase))
                    FindBlocksInGroup(group, plc, "", requestedName, requestedGroup, matches);
            }
            foreach (var child in Enumerate(Get(item, "DeviceItems"))) FindBlocks(child, requestedPlc, requestedName, requestedGroup, matches);
        }

        private static void FindBlocksInGroup(object group, string plc, string parent, string requestedName,
            string requestedGroup, List<BlockMatch> matches)
        {
            var groupName = Convert.ToString(Get(group, "Name")) ?? "Program blocks";
            var path = string.IsNullOrEmpty(parent) ? groupName : parent + "/" + groupName;
            foreach (var block in Enumerate(Get(group, "Blocks")))
            {
                var name = Convert.ToString(Get(block, "Name"));
                if (string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(requestedGroup) || string.Equals(path, requestedGroup, StringComparison.OrdinalIgnoreCase)))
                    matches.Add(new BlockMatch { Plc = plc, Group = path, Name = name, Block = block, GroupObject = group });
            }
            foreach (var child in Enumerate(Get(group, "Groups")))
                FindBlocksInGroup(child, plc, path, requestedName, requestedGroup, matches);
        }

        private static void WalkBlockGroup(object group, string plc, string parent, List<object> rows)
        {
            var groupName = Convert.ToString(Get(group, "Name")) ?? "Program blocks";
            var path = string.IsNullOrEmpty(parent) ? groupName : parent + "/" + groupName;
            foreach (var block in Enumerate(Get(group, "Blocks"))) rows.Add(Row(
                "plc", plc, "group", path, "name", Get(block, "Name"), "number", Get(block, "Number"),
                "type", block.GetType().Name, "programmingLanguage", Convert.ToString(Get(block, "ProgrammingLanguage")),
                "isKnowHowProtected", Get(block, "IsKnowHowProtected")));
            foreach (var child in Enumerate(Get(group, "Groups"))) WalkBlockGroup(child, plc, path, rows);
        }

        private object GetService(object target, Type serviceType)
        {
            var provider = target as IServiceProvider;
            if (provider != null) return provider.GetService(serviceType);
            foreach (var type in assembly.GetTypes())
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "GetService" && m.IsGenericMethodDefinition))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(target.GetType())) continue;
                    try { return method.MakeGenericMethod(serviceType).Invoke(null, new[] { target }); } catch { }
                }
            return null;
        }

        private static Dictionary<string, object> Row(params object[] values)
        {
            var result = new Dictionary<string, object>();
            for (var i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1];
            return result;
        }

        private static object Get(object target, string property) { return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null); }
        private static object Invoke(object target, string method) { return target.GetType().GetMethod(method, Type.EmptyTypes)?.Invoke(target, null); }
        private static object First(object value) { return Enumerate(value).FirstOrDefault(); }
        private static IEnumerable<object> Enumerate(object value)
        {
            var sequence = value as IEnumerable;
            if (sequence == null) yield break;
            foreach (var item in sequence) if (item != null) yield return item;
        }

        private sealed class BlockMatch
        {
            public string Plc;
            public string Group;
            public string Name;
            public object Block;
            public object GroupObject;
        }
    }
}
