using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;

sealed partial class TiaOpennessReader
{
    private sealed record HardwareRow(string Sheet, int RowNumber, Dictionary<string, string> Cells);
    private sealed record PreparedHardwareTemplate(string WorkbookPath, string WorkbookHash, HardwareRow[] Stations,
        HardwareRow[] Modules, HardwareRow[] Networks, HardwareRow[] IoMappings, DateTime ExpiresAtUtc);
    private readonly ConcurrentDictionary<string, PreparedHardwareTemplate> preparedHardwareTemplates = new(StringComparer.Ordinal);

    public object PrepareHardwareTemplate(string workbookPath)
    {
        var fullPath = Path.GetFullPath(workbookPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Hardware template workbook was not found.", fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hardware template must be an .xlsx file.");
        var info = new FileInfo(fullPath);
        if (info.Length is <= 0 or > 20_000_000) throw new InvalidOperationException("Hardware template must be between 1 byte and 20 MB.");
        var sheets = ReadXlsxSheets(fullPath);
        var required = new[] { "站点与CPU", "硬件模块", "网络连接", "IO映射" };
        foreach (var name in required)
            if (!sheets.ContainsKey(name)) throw new InvalidOperationException($"Required worksheet is missing: {name}");

        var stations = ReadHardwareRows("站点与CPU", sheets["站点与CPU"]);
        var modules = ReadHardwareRows("硬件模块", sheets["硬件模块"]);
        var networks = ReadHardwareRows("网络连接", sheets["网络连接"]);
        var io = ReadHardwareRows("IO映射", sheets["IO映射"]);
        var issues = ValidateHardwareRows(stations, modules, networks, io);
        if (issues.Count > 0) throw new InvalidOperationException("Hardware template validation failed: " + string.Join(" | ", issues.Take(20)));

        CleanupHardwareTemplates();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
        var changeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
        var expires = DateTime.UtcNow.AddMinutes(30);
        preparedHardwareTemplates[changeId] = new PreparedHardwareTemplate(fullPath, hash, stations, modules, networks, io, expires);
        return new
        {
            changeId, expiresAtUtc = expires, workbookPath = fullPath, workbookHash = hash,
            stations = stations.Length, modules = modules.Length, networks = networks.Length, ioMappings = io.Length,
            actions = stations.Concat(modules).Concat(networks).Concat(io).GroupBy(row => Value(row, "操作*")).ToDictionary(group => group.Key, group => group.Count()),
            confirmationRequired = "APPLY_HARDWARE_TEMPLATE", writeEnabled = IsWriteEnabled(), writePerformed = false,
            notes = new[] { "Only nonblank rows whose action is not 跳过 are included.", "Order numbers are normalized to the TIA TypeIdentifier form OrderNumber:<value>.", "Hardware compatibility and exact attribute availability are authoritatively checked by the installed TIA version during apply." }
        };
    }

    public object ApplyHardwareTemplate(string changeId, string confirmation)
    {
        if (!IsWriteEnabled()) throw new InvalidOperationException("Hardware creation is disabled. Start the bridge with write safeguards enabled.");
        if (!string.Equals(confirmation, "APPLY_HARDWARE_TEMPLATE", StringComparison.Ordinal))
            throw new InvalidOperationException("Explicit confirmation is required: APPLY_HARDWARE_TEMPLATE.");
        CleanupHardwareTemplates();
        if (!preparedHardwareTemplates.TryRemove(changeId, out var plan)) throw new InvalidOperationException("Prepared hardware template was not found, expired, or already consumed.");
        var currentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plan.WorkbookPath))).ToLowerInvariant();
        if (!string.Equals(currentHash, plan.WorkbookHash, StringComparison.Ordinal)) throw new InvalidOperationException("Workbook changed after preparation; prepare it again.");

        var results = new List<object>();
        var warnings = new List<string>();
        var createdDevices = new List<string>();
        var stationMap = plan.Stations.ToDictionary(row => Value(row, "站点ID*"), row => row, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in plan.Stations)
            {
                var action = NormalizeAction(Value(row, "操作*"));
                var stationName = Value(row, "站点名称*");
                var itemName = FirstNonblank(Value(row, "PLC名称"), stationName);
                if (action == "新建")
                {
                    results.Add(Execute("create-device", TypeIdentifier(Value(row, "CPU/接口模块订货号*"), Value(row, "固件版本")), stationName, itemName));
                    createdDevices.Add(stationName);
                }
                else if (action is not ("更新" or "克隆"))
                    throw new InvalidOperationException($"站点与CPU row {row.RowNumber}: unsupported action '{action}'.");
                ConfigureBestEffort(stationName, itemName, row, results, warnings,
                    ("PROFINET设备名称", "NameOfStation"), ("IP地址", "Address"), ("子网掩码", "SubnetMask"), ("网关", "RouterAddress"));
            }

            foreach (var row in plan.Modules)
            {
                var action = NormalizeAction(Value(row, "操作*"));
                var station = stationMap[Value(row, "站点ID*")];
                var deviceName = Value(station, "站点名称*");
                var parent = FirstNonblank(Value(row, "父模块名称"), Value(station, "PLC名称"), deviceName);
                if (action is "新建" or "克隆")
                    results.Add(Execute("plug-module", deviceName, parent, TypeIdentifier(Value(row, "订货号*"), Value(row, "固件版本")), Value(row, "模块名称*"), Value(row, "槽位*")));
                else if (action != "更新") throw new InvalidOperationException($"硬件模块 row {row.RowNumber}: unsupported action '{action}'.");
                ConfigureBestEffort(deviceName, Value(row, "模块名称*"), row, results, warnings,
                    ("输入起始地址", "StartAddress"), ("输出起始地址", "StartAddress"));
            }

            foreach (var row in plan.Networks)
            {
                var station = stationMap[Value(row, "站点ID*")];
                var deviceName = Value(station, "站点名称*");
                var target = FirstNonblank(Value(row, "接口名称*"), Value(station, "PLC名称"), deviceName);
                ConfigureBestEffort(deviceName, target, row, results, warnings,
                    ("PROFINET设备名称", "NameOfStation"), ("IP地址", "Address"), ("子网掩码", "SubnetMask"), ("网关", "RouterAddress"));
            }

            var overview = GetHardwareOverview(null, null);
            return new { ok = true, plan.WorkbookHash, createdDevices, operationCount = results.Count, results, warnings,
                ioMappings = plan.IoMappings.Select(row => row.Cells).ToArray(), hardwareOverview = overview,
                projectSaved = false, writePerformed = true };
        }
        catch (Exception applyException)
        {
            var rollbackErrors = new List<string>();
            foreach (var device in createdDevices.AsEnumerable().Reverse())
                try { Execute("delete-device", device); } catch (Exception ex) { rollbackErrors.Add(device + ": " + ex.GetBaseException().Message); }
            if (rollbackErrors.Count > 0) throw new AggregateException("Hardware apply failed and rollback was incomplete: " + string.Join(" | ", rollbackErrors), applyException);
            throw new InvalidOperationException("Hardware apply failed; devices created by this operation were deleted automatically.", applyException);
        }
    }

    private void ConfigureBestEffort(string device, string item, HardwareRow row, List<object> results, List<string> warnings,
        params (string Column, string Attribute)[] mappings)
    {
        foreach (var mapping in mappings)
        {
            var value = Value(row, mapping.Column);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try { results.Add(Execute("set-hardware-attribute", device, item, mapping.Attribute, value)); }
            catch (Exception ex) { warnings.Add($"{row.Sheet} row {row.RowNumber}, {item}.{mapping.Attribute}: {ex.GetBaseException().Message}"); }
        }
    }

    private static string TypeIdentifier(string orderNumber, string firmware)
    {
        var value = orderNumber.Trim();
        value = value.Contains(':') ? value : "OrderNumber:" + value;
        if (!string.IsNullOrWhiteSpace(firmware) && !value.Contains('/')) value += "/" + firmware.Trim();
        return value;
    }

    private static string NormalizeAction(string action) => action.Trim();
    private static string FirstNonblank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static string Value(HardwareRow row, string column) => row.Cells.TryGetValue(column, out var value) ? value.Trim() : "";

    private static List<string> ValidateHardwareRows(HardwareRow[] stations, HardwareRow[] modules, HardwareRow[] networks, HardwareRow[] io)
    {
        var issues = new List<string>();
        Require(stations, issues, "操作*", "站点ID*", "站点名称*", "CPU/接口模块订货号*");
        Require(modules, issues, "操作*", "站点ID*", "机架号*", "槽位*", "模块名称*", "订货号*");
        Require(networks, issues, "操作*", "连接ID*", "站点ID*", "接口名称*");
        Require(io, issues, "操作*", "站点ID*", "模块名称*", "符号名称*", "逻辑地址*");
        Duplicate(stations, issues, "站点ID*"); Duplicate(networks, issues, "连接ID*"); Duplicate(io, issues, "符号名称*"); Duplicate(io, issues, "逻辑地址*");
        var stationIds = stations.Select(row => Value(row, "站点ID*")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in modules.Concat(networks).Concat(io)) if (!stationIds.Contains(Value(row, "站点ID*"))) issues.Add($"{row.Sheet} row {row.RowNumber}: station ID does not exist.");
        return issues;
    }

    private static void Require(IEnumerable<HardwareRow> rows, List<string> issues, params string[] columns)
    {
        foreach (var row in rows) foreach (var column in columns)
            if (string.IsNullOrWhiteSpace(Value(row, column))) issues.Add($"{row.Sheet} row {row.RowNumber}: missing {column}.");
    }

    private static void Duplicate(IEnumerable<HardwareRow> rows, List<string> issues, string column)
    {
        foreach (var group in rows.GroupBy(row => Value(row, column), StringComparer.OrdinalIgnoreCase).Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
            issues.Add($"{group.First().Sheet}: duplicate {column} '{group.Key}'.");
    }

    private static HardwareRow[] ReadHardwareRows(string sheetName, SortedDictionary<int, Dictionary<int, string>> rows)
    {
        if (!rows.TryGetValue(4, out var headerRow)) throw new InvalidOperationException($"{sheetName}: header row 4 is missing.");
        var headers = headerRow.ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
        return rows.Where(pair => pair.Key >= 5 && pair.Value.TryGetValue(1, out var action) && !string.IsNullOrWhiteSpace(action) && !string.Equals(action.Trim(), "跳过", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new HardwareRow(sheetName, pair.Key, headers.Where(header => !string.IsNullOrWhiteSpace(header.Value))
                .ToDictionary(header => header.Value, header => pair.Value.TryGetValue(header.Key, out var value) ? value : "", StringComparer.OrdinalIgnoreCase))).ToArray();
    }

    private static Dictionary<string, SortedDictionary<int, Dictionary<int, string>>> ReadXlsxSheets(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var stream = sharedEntry.Open();
            var doc = XDocument.Load(stream);
            shared.AddRange(doc.Descendants().Where(element => element.Name.LocalName == "si")
                .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value))));
        }
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var rels = LoadXml(archive, "xl/_rels/workbook.xml.rels").Descendants().Where(element => element.Name.LocalName == "Relationship")
            .ToDictionary(element => (string?)element.Attribute("Id") ?? "", element => (string?)element.Attribute("Target") ?? "");
        var result = new Dictionary<string, SortedDictionary<int, Dictionary<int, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants().Where(element => element.Name.LocalName == "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "";
            var relationId = sheet.Attributes().First(attribute => attribute.Name.LocalName == "id").Value;
            var target = rels[relationId].Replace('\\', '/').TrimStart('/');
            if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) target = "xl/" + target;
            var document = LoadXml(archive, target);
            var rows = new SortedDictionary<int, Dictionary<int, string>>();
            foreach (var cell in document.Descendants().Where(element => element.Name.LocalName == "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? "";
                var rowNumber = int.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()));
                var column = ColumnNumber(new string(reference.TakeWhile(char.IsLetter).ToArray()));
                var type = (string?)cell.Attribute("t");
                var raw = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ??
                          string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
                var value = type == "s" && int.TryParse(raw, out var index) && index < shared.Count ? shared[index] : raw;
                if (!rows.TryGetValue(rowNumber, out var row)) rows[rowNumber] = row = new Dictionary<int, string>();
                row[column] = value;
            }
            result[name] = rows;
        }
        return result;
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException("Invalid xlsx package; missing " + path);
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static int ColumnNumber(string letters)
    {
        var result = 0;
        foreach (var character in letters.ToUpperInvariant()) result = result * 26 + character - 'A' + 1;
        return result;
    }

    private void CleanupHardwareTemplates()
    {
        foreach (var pair in preparedHardwareTemplates.Where(pair => pair.Value.ExpiresAtUtc <= DateTime.UtcNow).ToArray())
            preparedHardwareTemplates.TryRemove(pair.Key, out _);
    }
}
