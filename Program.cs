using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
// The bridge is commonly launched as a non-elevated background process. Avoid the
// Windows Event Log provider because a denied Event Log write can mask the real
// startup failure (for example, an occupied HTTP port).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
var listenUrl = Environment.GetEnvironmentVariable("TIA_MCP_URL") ?? "http://127.0.0.1:5111";
var writeRequested = string.Equals(Environment.GetEnvironmentVariable("TIA_ENABLE_WRITE"), "true", StringComparison.OrdinalIgnoreCase);
var saveRequested = string.Equals(Environment.GetEnvironmentVariable("TIA_ENABLE_SAVE"), "true", StringComparison.OrdinalIgnoreCase);
if (writeRequested && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIA_MCP_TOKEN")))
    throw new InvalidOperationException("TIA_MCP_TOKEN is required when TIA_ENABLE_WRITE=true.");
if (saveRequested && !writeRequested)
    throw new InvalidOperationException("TIA_ENABLE_SAVE=true requires TIA_ENABLE_WRITE=true.");
builder.WebHost.UseUrls(listenUrl);
builder.Services.AddSingleton<TiaOpennessReader>();
builder.Services.AddSingleton<LocalSecretStore>();
builder.Services.AddHttpClient<OpenAiChatService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var expected = Environment.GetEnvironmentVariable("TIA_MCP_TOKEN");
    if (!string.IsNullOrWhiteSpace(expected))
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!string.Equals(supplied, $"Bearer {expected}", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid bearer token." });
            return;
        }
    }
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = ex.GetBaseException().Message });
    }
});

app.MapGet("/health", (TiaOpennessReader tia) => Results.Ok(tia.GetStatus()));
app.MapGet("/api/diagnostics", (TiaOpennessReader tia) => Results.Ok(tia.GetDiagnostics()));
app.MapGet("/api/config", (LocalSecretStore secrets) => Results.Ok(new
{
    apiConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) || secrets.HasOpenAiKey,
    model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? secrets.Model ?? "gpt-5.6",
    secretSource = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) ? "environment" : secrets.HasOpenAiKey ? "windows-dpapi" : "none",
    writeEnabled = writeRequested,
    saveEnabled = saveRequested
}));
app.MapPost("/api/settings/openai", (HttpContext context, OpenAiSettingsRequest request, LocalSecretStore secrets) =>
{
    if (!System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.ApiKey) || !request.ApiKey.StartsWith("sk-", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "A valid OpenAI API key is required." });
    secrets.SaveOpenAi(request.ApiKey.Trim(), string.IsNullOrWhiteSpace(request.Model) ? "gpt-5.6" : request.Model.Trim());
    return Results.Ok(new { ok = true, model = secrets.Model, secretSource = "windows-dpapi" });
});
app.MapGet("/api/projects", (TiaOpennessReader tia) => Results.Ok(tia.ListProjects()));
app.MapGet("/api/devices", (TiaOpennessReader tia, string? kind, string? nameContains, int? offset, int? limit) =>
    Results.Ok(tia.ListDevices(kind, nameContains, offset ?? 0, limit ?? 500)));
app.MapGet("/api/blocks", (TiaOpennessReader tia, string? plc, string? type, string? groupContains,
    string? nameContains, int? offset, int? limit) =>
    Results.Ok(tia.ListBlocks(plc, type, groupContains, nameContains, offset ?? 0, limit ?? 500)));
app.MapGet("/api/blocks/export", (TiaOpennessReader tia, string plc, string name, string? group) =>
    Results.Ok(tia.ExportBlock(plc, name, group)));
app.MapGet("/api/tag-tables", (TiaOpennessReader tia, string? plc, string? groupContains, string? nameContains, int? offset, int? limit) =>
    Results.Ok(tia.ListTagTables(plc, groupContains, nameContains, offset ?? 0, limit ?? 500)));
app.MapGet("/api/tag-tables/export", (TiaOpennessReader tia, string plc, string name) =>
    Results.Ok(tia.ExportTagTable(plc, name)));
app.MapGet("/api/tag-tables/search", (TiaOpennessReader tia, string plc, string name, string query, int? limit) =>
    Results.Ok(tia.SearchTagTable(plc, name, query, limit ?? 100)));
app.MapGet("/api/plcs/{plc}/overview", (TiaOpennessReader tia, string plc) =>
    Results.Ok(tia.GetPlcOverview(plc)));
app.MapGet("/api/tag-tables/overview", (TiaOpennessReader tia, string plc, string name, int? offset, int? limit) =>
    Results.Ok(tia.GetTagTableOverview(plc, name, offset ?? 0, limit ?? 200)));
app.MapGet("/api/blocks/interface", (TiaOpennessReader tia, string plc, string name, string? group) =>
    Results.Ok(tia.GetBlockInterface(plc, name, group)));
app.MapGet("/api/blocks/search-all", (TiaOpennessReader tia, string plc, string query, string? type, string? groupContains, int? maxBlocks, int? limit) =>
    Results.Ok(tia.SearchPlcBlocks(plc, query, type, groupContains, maxBlocks ?? 100, limit ?? 100)));
app.MapGet("/api/plcs/{plc}/dependencies", (TiaOpennessReader tia, string plc, int? maxBlocks) =>
    Results.Ok(tia.GetBlockDependencies(plc, maxBlocks ?? 200)));
app.MapGet("/api/hardware/overview", (TiaOpennessReader tia, string? nameContains, string? typeContains) =>
    Results.Ok(tia.GetHardwareOverview(nameContains, typeContains)));
app.MapPost("/api/snapshots", (TiaOpennessReader tia, ProjectSnapshotRequest request) =>
    Results.Ok(tia.CreateProjectSnapshot(request.Plc, request.MaxBlocks ?? 500, request.MaxTagTables ?? 200)));
app.MapGet("/api/snapshots/{snapshotId}/compare", (TiaOpennessReader tia, string snapshotId) =>
    Results.Ok(tia.CompareProjectSnapshot(snapshotId)));
app.MapGet("/api/data-blocks", (TiaOpennessReader tia, string plc, string? groupContains, string? nameContains, int? offset, int? limit) =>
    Results.Ok(tia.ListDataBlocks(plc, groupContains, nameContains, offset ?? 0, limit ?? 500)));
app.MapGet("/api/data-blocks/overview", (TiaOpennessReader tia, string plc, string name, string? group, int? offset, int? limit) =>
    Results.Ok(tia.GetDataBlockOverview(plc, name, group, offset ?? 0, limit ?? 500)));
app.MapGet("/api/blocks/networks", (TiaOpennessReader tia, string plc, string name, string? group, int? offset, int? limit) =>
    Results.Ok(tia.GetBlockNetworks(plc, name, group, offset ?? 0, limit ?? 100)));
app.MapGet("/api/blocks/references", (TiaOpennessReader tia, string plc, string name, string? group) =>
    Results.Ok(tia.GetBlockReferences(plc, name, group)));
app.MapGet("/api/plcs/{plc}/io-audit", (TiaOpennessReader tia, string plc, int? maxTagTables, int? issueLimit) =>
    Results.Ok(tia.AuditPlcIo(plc, maxTagTables ?? 200, issueLimit ?? 500)));
app.MapGet("/api/plcs/{plc}/symbol-usage", (TiaOpennessReader tia, string plc, int? maxBlocks, int? maxTagTables, int? issueLimit) =>
    Results.Ok(tia.AuditSymbolUsage(plc, maxBlocks ?? 200, maxTagTables ?? 200, issueLimit ?? 500)));
app.MapPost("/api/blocks/preview", (TiaOpennessReader tia, BlockChangeRequest request) =>
    Results.Ok(tia.PreviewBlockChange(request.Plc, request.Name, request.Group, request.BaselineHash, request.Xml)));
app.MapPost("/api/blocks/apply", (TiaOpennessReader tia, ApplyBlockChangeRequest request) =>
    Results.Ok(tia.ApplyBlockChange(request.Plc, request.Name, request.Group, request.BaselineHash, request.Xml, request.ApplyToken)));
app.MapPost("/api/projects/save", (TiaOpennessReader tia, SaveProjectRequest request) =>
    Results.Ok(tia.SaveProject(request.ProjectName, request.Plc, request.Name, request.Group, request.ExpectedBlockHash, request.SaveToken)));
app.MapPost("/api/chat", async (ChatRequest request, OpenAiChatService chat, CancellationToken cancellationToken) =>
    Results.Ok(await chat.SendAsync(request, cancellationToken)));

app.MapPost("/mcp", async (HttpContext context, TiaOpennessReader tia) =>
{
    JsonObject? request;
    try { request = await JsonNode.ParseAsync(context.Request.Body) as JsonObject; }
    catch (JsonException ex) { return Results.Json(RpcError(null, -32700, ex.Message)); }

    var id = request?["id"]?.DeepClone();
    var method = request?["method"]?.GetValue<string>();
    object result;
    try
    {
        result = method switch
        {
            "initialize" => new
            {
                protocolVersion = request?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "tia-openness-bridge", version = "0.2.0" }
            },
            "ping" => new { },
            "tools/list" => new { tools = ToolDefinitions() },
            "tools/call" => CallTool(request?["params"] as JsonObject, tia),
            "notifications/initialized" => new { },
            _ => throw new RpcException(-32601, $"Unknown method: {method}")
        };
    }
    catch (RpcException ex) { return Results.Json(RpcError(id, ex.Code, ex.Message)); }
    catch (Exception ex) { return Results.Json(RpcError(id, -32603, ex.GetBaseException().Message)); }

    if (id is null) return Results.NoContent();
    return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = JsonSerializer.SerializeToNode(result) });
});

app.Run();

static object CallTool(JsonObject? parameters, TiaOpennessReader tia)
{
    var name = parameters?["name"]?.GetValue<string>();
    var arguments = parameters?["arguments"] as JsonObject;
    object data = name switch
    {
        "tia_status" => tia.GetStatus(),
        "tia_diagnostics" => tia.GetDiagnostics(),
        "tia_list_change_history" => tia.ListChangeHistory(IntArg(arguments, "limit", 20)),
        "tia_list_projects" => tia.ListProjects(),
        "tia_list_devices" => tia.ListDevices(
            StringArg(arguments, "kind"), StringArg(arguments, "nameContains"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 500)),
        "tia_list_blocks" => tia.ListBlocks(
            StringArg(arguments, "plc"), StringArg(arguments, "type"),
            StringArg(arguments, "groupContains"), StringArg(arguments, "nameContains"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 500)),
        "tia_list_tag_tables" => tia.ListTagTables(
            StringArg(arguments, "plc"), StringArg(arguments, "groupContains"), StringArg(arguments, "nameContains"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 500)),
        "tia_export_tag_table" => tia.ExportTagTable(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name")),
        "tia_search_tag_table" => tia.SearchTagTable(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"),
            RequiredStringArg(arguments, "query"), IntArg(arguments, "limit", 100)),
        "tia_get_plc_overview" => tia.GetPlcOverview(RequiredStringArg(arguments, "plc")),
        "tia_get_tag_table_overview" => tia.GetTagTableOverview(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 200)),
        "tia_get_block_interface" => tia.GetBlockInterface(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group")),
        "tia_search_plc_blocks" => tia.SearchPlcBlocks(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "query"),
            StringArg(arguments, "type"), StringArg(arguments, "groupContains"),
            IntArg(arguments, "maxBlocks", 100), IntArg(arguments, "limit", 100)),
        "tia_get_block_dependencies" => tia.GetBlockDependencies(
            RequiredStringArg(arguments, "plc"), IntArg(arguments, "maxBlocks", 200)),
        "tia_get_hardware_overview" => tia.GetHardwareOverview(
            StringArg(arguments, "nameContains"), StringArg(arguments, "typeContains")),
        "tia_create_project_snapshot" => tia.CreateProjectSnapshot(
            StringArg(arguments, "plc"), IntArg(arguments, "maxBlocks", 500), IntArg(arguments, "maxTagTables", 200)),
        "tia_compare_project_snapshot" => tia.CompareProjectSnapshot(RequiredStringArg(arguments, "snapshotId")),
        "tia_list_data_blocks" => tia.ListDataBlocks(
            RequiredStringArg(arguments, "plc"), StringArg(arguments, "groupContains"), StringArg(arguments, "nameContains"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 500)),
        "tia_get_data_block_overview" => tia.GetDataBlockOverview(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 500)),
        "tia_get_block_networks" => tia.GetBlockNetworks(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            IntArg(arguments, "offset", 0), IntArg(arguments, "limit", 100)),
        "tia_get_block_references" => tia.GetBlockReferences(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group")),
        "tia_audit_plc_io" => tia.AuditPlcIo(
            RequiredStringArg(arguments, "plc"), IntArg(arguments, "maxTagTables", 200), IntArg(arguments, "issueLimit", 500)),
        "tia_audit_symbol_usage" => tia.AuditSymbolUsage(
            RequiredStringArg(arguments, "plc"), IntArg(arguments, "maxBlocks", 200),
            IntArg(arguments, "maxTagTables", 200), IntArg(arguments, "issueLimit", 500)),
        "tia_get_block_overview" => tia.GetBlockOverview(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group")),
        "tia_search_block_text" => tia.SearchBlockText(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            RequiredStringArg(arguments, "query"), IntArg(arguments, "limit", 50)),
        "tia_export_block" => tia.ExportBlock(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group")),
        "tia_preview_block_change" => tia.PreviewBlockChange(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            RequiredStringArg(arguments, "baselineHash"), RequiredStringArg(arguments, "xml")),
        "tia_prepare_text_replacement" => tia.PrepareTextReplacement(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            RequiredStringArg(arguments, "find"), RequiredStringArg(arguments, "replace"),
            IntArg(arguments, "expectedOccurrences", 1), BoolArg(arguments, "includeXml", false)),
        "tia_apply_prepared_change" => tia.ApplyPreparedChange(
            RequiredStringArg(arguments, "changeId"), RequiredStringArg(arguments, "confirmation")),
        "tia_apply_block_change" => tia.ApplyBlockChange(
            RequiredStringArg(arguments, "plc"), RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            RequiredStringArg(arguments, "baselineHash"), RequiredStringArg(arguments, "xml"),
            RequiredStringArg(arguments, "applyToken")),
        "tia_compile_plc" => tia.CompilePlc(RequiredStringArg(arguments, "plc")),
        "tia_save_project" => tia.SaveProject(
            RequiredStringArg(arguments, "projectName"), RequiredStringArg(arguments, "plc"),
            RequiredStringArg(arguments, "name"), StringArg(arguments, "group"),
            RequiredStringArg(arguments, "expectedBlockHash"), RequiredStringArg(arguments, "saveToken")),
        _ => throw new RpcException(-32602, $"Unknown tool: {name}")
    };
    return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }) } } };
}

static object[] ToolDefinitions() =>
[
    Tool("tia_status", "Check the TIA Openness DLL and running TIA Portal processes."),
    Tool("tia_diagnostics", "Run a non-invasive dependency check for the bridge, worker, TIA Openness installations, user-group permission, authentication, and write safeguards."),
    Tool("tia_list_change_history", "List recent block-change journals, including successful imports and rollback stages.", new
    {
        limit = IntegerProperty("Maximum journal entries to return (1-100).", 1, 100)
    }),
    Tool("tia_list_projects", "List projects opened in running TIA Portal instances."),
    Tool("tia_list_devices", "List devices and device items in the first open project, with optional filtering and pagination.", new
    {
        kind = StringProperty("Filter by kind: device or deviceItem."),
        nameContains = StringProperty("Case-insensitive name substring."),
        offset = IntegerProperty("Number of matching rows to skip.", 0, null),
        limit = IntegerProperty("Maximum rows to return (1-1000).", 1, 1000)
    }),
    Tool("tia_list_blocks", "List PLC blocks in the first open project, including nested block groups, with optional filtering and pagination.", new
    {
        plc = StringProperty("Exact PLC name."),
        type = StringProperty("Exact block type, such as OB, FB, FC, GlobalDB, or InstanceDB."),
        groupContains = StringProperty("Case-insensitive block-group path substring."),
        nameContains = StringProperty("Case-insensitive block-name substring."),
        offset = IntegerProperty("Number of matching rows to skip.", 0, null),
        limit = IntegerProperty("Maximum rows to return (1-1000).", 1, 1000)
    }),
    Tool("tia_list_tag_tables", "List PLC tag tables in the first open project, including nested group paths, with optional filtering and pagination.", new
    {
        plc = StringProperty("Filter by exact PLC software name."),
        groupContains = StringProperty("Filter by tag-table group path substring."),
        nameContains = StringProperty("Filter by tag-table name substring."),
        offset = IntegerProperty("Number of matching rows to skip.", 0, null),
        limit = IntegerProperty("Maximum rows to return (1-1000).", 1, 1000)
    }),
    Tool("tia_export_tag_table", "Export one PLC tag table as read-only TIA Portal XML and return a stable SHA-256 hash.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact tag-table name; names must be unique within the PLC.")
    }, ["plc", "name"]),
    Tool("tia_search_tag_table", "Search names, logical addresses, data types, and comments in one PLC tag table without returning its full XML.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact tag-table name."),
        query = StringProperty("Case-insensitive text to search for."),
        limit = IntegerProperty("Maximum matching XML elements to return (1-500).", 1, 500)
    }, ["plc", "name", "query"]),
    Tool("tia_get_plc_overview", "Return a compact PLC inventory with block type/language counts, group paths, and tag-table summaries.", new
    {
        plc = StringProperty("Exact PLC software name.")
    }, ["plc"]),
    Tool("tia_get_tag_table_overview", "Parse one PLC tag table into compact structured tags with name, logical address, data type, and comments.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact tag-table name."),
        offset = IntegerProperty("Number of tag entries to skip.", 0, null),
        limit = IntegerProperty("Maximum tag entries to return (1-1000).", 1, 1000)
    }, ["plc", "name"]),
    Tool("tia_get_block_interface", "Parse one PLC block interface into Input, Output, InOut, Static, Temp, Constant, and Return members.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous.")
    }, ["plc", "name"]),
    Tool("tia_search_plc_blocks", "Search a symbol, comment, or call-related text across multiple blocks in one PLC with bounded scanning.", new
    {
        plc = StringProperty("Exact PLC software name."),
        query = StringProperty("Case-insensitive text to search for."),
        type = StringProperty("Optional exact block type filter, such as OB, FB, FC, GlobalDB, or InstanceDB."),
        groupContains = StringProperty("Optional block-group path substring."),
        maxBlocks = IntegerProperty("Maximum blocks to export and scan (1-500).", 1, 500),
        limit = IntegerProperty("Maximum matching blocks to return (1-500).", 1, 500)
    }, ["plc", "query"]),
    Tool("tia_get_block_dependencies", "Build a read-only PLC block dependency graph by matching LAD/FBD call parts to known block names.", new
    {
        plc = StringProperty("Exact PLC software name."),
        maxBlocks = IntegerProperty("Maximum blocks to export and analyze (1-500).", 1, 500)
    }, ["plc"]),
    Tool("tia_get_hardware_overview", "Summarize the open project's hardware hierarchy and module TypeIdentifiers. I/O addresses remain sourced from PLC tag tables.", new
    {
        nameContains = StringProperty("Optional case-insensitive device or module name substring."),
        typeContains = StringProperty("Optional case-insensitive TypeIdentifier substring.")
    }),
    Tool("tia_create_project_snapshot", "Create a temporary read-only structural and hash snapshot of blocks and tag tables for one PLC or the first PLC found.", new
    {
        plc = StringProperty("Optional exact PLC software name. If omitted, the first discovered PLC is selected."),
        maxBlocks = IntegerProperty("Maximum blocks to snapshot (1-500).", 1, 500),
        maxTagTables = IntegerProperty("Maximum tag tables to snapshot (1-200).", 1, 200)
    }),
    Tool("tia_compare_project_snapshot", "Compare the current TIA project against a temporary snapshot and report added, removed, and changed objects.", new
    {
        snapshotId = StringProperty("Opaque snapshot ID returned by tia_create_project_snapshot.")
    }, ["snapshotId"]),
    Tool("tia_list_data_blocks", "List GlobalDB and InstanceDB blocks in one PLC with optional group/name filtering and pagination.", new
    {
        plc = StringProperty("Exact PLC software name."),
        groupContains = StringProperty("Optional block-group path substring."),
        nameContains = StringProperty("Optional data-block name substring."),
        offset = IntegerProperty("Number of matching rows to skip.", 0, null),
        limit = IntegerProperty("Maximum rows to return (1-1000).", 1, 1000)
    }, ["plc"]),
    Tool("tia_get_data_block_overview", "Parse a GlobalDB or InstanceDB into flattened member paths, data types, attributes, initial values, and comments.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact data-block name."),
        group = StringProperty("Exact block-group path. Optional unless the name is ambiguous."),
        offset = IntegerProperty("Number of flattened members to skip.", 0, null),
        limit = IntegerProperty("Maximum flattened members to return (1-2000).", 1, 2000)
    }, ["plc", "name"]),
    Tool("tia_get_block_networks", "Return LAD/FBD/SCL compile units as network-level summaries with titles, comments, symbols, calls, and instruction parts.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the name is ambiguous."),
        offset = IntegerProperty("Number of networks to skip.", 0, null),
        limit = IntegerProperty("Maximum networks to return (1-500).", 1, 500)
    }, ["plc", "name"]),
    Tool("tia_get_block_references", "Return a compact unique reference summary for one block, including symbols, scopes, calls, instances, constants, and instructions.", new
    {
        plc = StringProperty("Exact PLC software name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the name is ambiguous.")
    }, ["plc", "name"]),
    Tool("tia_audit_plc_io", "Audit all PLC tag tables for address/name conflicts, missing types/comments, and address-area distribution without modifying TIA.", new
    {
        plc = StringProperty("Exact PLC software name."),
        maxTagTables = IntegerProperty("Maximum tag tables to export and audit (1-200).", 1, 200),
        issueLimit = IntegerProperty("Maximum issues returned per issue category (1-2000).", 1, 2000)
    }, ["plc"]),
    Tool("tia_audit_symbol_usage", "Cross-check PLC tag definitions against bounded block exports to find possibly unused tags and unresolved global symbol references.", new
    {
        plc = StringProperty("Exact PLC software name."),
        maxBlocks = IntegerProperty("Maximum blocks to export and scan (1-500).", 1, 500),
        maxTagTables = IntegerProperty("Maximum tag tables to export (1-200).", 1, 200),
        issueLimit = IntegerProperty("Maximum rows returned per finding category (1-2000).", 1, 2000)
    }, ["plc"]),
    Tool("tia_get_block_overview", "Return a compact PLC block overview with hash, language, compile units, and readable network text.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous.")
    }, ["plc", "name"]),
    Tool("tia_search_block_text", "Search readable text nodes in one PLC block and return their exact XML paths without returning the full XML.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous."),
        query = StringProperty("Case-insensitive text substring."),
        limit = IntegerProperty("Maximum matches to return (1-200).", 1, 200)
    }, ["plc", "name", "query"]),
    Tool("tia_export_block", "Export one PLC block as read-only TIA Portal XML for inspection and change planning.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous.")
    }, ["plc", "name"]),
    Tool("tia_preview_block_change", "Validate proposed PLC block XML and preview a change without importing or modifying the project.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous."),
        baselineHash = StringProperty("Baseline SHA-256 returned by tia_export_block."),
        xml = StringProperty("Complete proposed TIA Portal block XML.")
    }, ["plc", "name", "baselineHash", "xml"]),
    Tool("tia_prepare_text_replacement", "Prepare and preview an exact text replacement in one exported PLC block. Returns complete proposed XML but never imports it.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path. Optional unless the block name is ambiguous."),
        find = StringProperty("Exact case-sensitive text to replace."),
        replace = StringProperty("Replacement text."),
        expectedOccurrences = IntegerProperty("Required exact match count. Defaults to 1 and prevents unintended broad replacements.", 1, 100),
        includeXml = BooleanProperty("Include complete proposed XML in the response. Defaults to false to keep Codex context compact.")
    }, ["plc", "name", "find", "replace"]),
    Tool("tia_apply_prepared_change", "Apply one server-cached prepared change after explicit confirmation. Requires write mode and consumes the change ID once.", new
    {
        changeId = StringProperty("Opaque change ID returned by tia_prepare_text_replacement."),
        confirmation = StringProperty("Must be exactly APPLY_PREPARED_CHANGE.")
    }, ["changeId", "confirmation"]),
    Tool("tia_apply_block_change", "Import a previously previewed PLC block XML. Disabled unless server-side write safeguards are explicitly configured.", new
    {
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path."),
        baselineHash = StringProperty("Baseline SHA-256 returned by tia_export_block."),
        xml = StringProperty("Complete proposed TIA Portal block XML that passed preview."),
        applyToken = StringProperty("Change-specific one-time token returned by preview when writes are enabled.")
    }, ["plc", "name", "baselineHash", "xml", "applyToken"]),
    Tool("tia_compile_plc", "Compile one PLC and return structured diagnostics. Available only when write operations are explicitly enabled.", new
    {
        plc = StringProperty("Exact PLC name.")
    }, ["plc"]),
    Tool("tia_save_project", "Persist a successfully applied and verified block change to the TIA project. Disabled unless server-side save safeguards are explicitly configured.", new
    {
        projectName = StringProperty("Exact open TIA project name returned by apply."),
        plc = StringProperty("Exact PLC name."),
        name = StringProperty("Exact block name."),
        group = StringProperty("Exact block-group path."),
        expectedBlockHash = StringProperty("Applied block hash returned by apply."),
        saveToken = StringProperty("Change-specific one-time save token returned by apply.")
    }, ["projectName", "plc", "name", "expectedBlockHash", "saveToken"])
];

static object Tool(string name, string description, object? properties = null, string[]? required = null)
{
    var schema = new Dictionary<string, object>
    {
        ["type"] = "object", ["properties"] = properties ?? new { }, ["additionalProperties"] = false
    };
    if (required is { Length: > 0 }) schema["required"] = required;
    return new { name, description, inputSchema = schema };
}

static object StringProperty(string description) => new { type = "string", description };
static object IntegerProperty(string description, int minimum, int? maximum)
{
    var schema = new Dictionary<string, object> { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum };
    if (maximum.HasValue) schema["maximum"] = maximum.Value;
    return schema;
}
static object BooleanProperty(string description) => new { type = "boolean", description };
static string? StringArg(JsonObject? arguments, string name) => arguments?[name]?.GetValue<string>();
static string RequiredStringArg(JsonObject? arguments, string name) =>
    !string.IsNullOrWhiteSpace(StringArg(arguments, name)) ? StringArg(arguments, name)! : throw new RpcException(-32602, $"Missing required argument: {name}");
static int IntArg(JsonObject? arguments, string name, int fallback) => arguments?[name]?.GetValue<int>() ?? fallback;
static bool BoolArg(JsonObject? arguments, string name, bool fallback) => arguments?[name]?.GetValue<bool>() ?? fallback;

static JsonObject RpcError(JsonNode? id, int code, string message) => new()
{
    ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
};

sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }

sealed class TiaOpennessReader
{
    private sealed record PreparedChange(string Plc, string Name, string? Group, string BaselineHash, string ProposedXml, DateTime ExpiresAtUtc);
    private sealed record ProjectSnapshot(string ProjectName, string Plc, DateTime CreatedAtUtc, DateTime ExpiresAtUtc,
        Dictionary<string, SnapshotItem> Blocks, Dictionary<string, SnapshotItem> TagTables);
    private sealed record SnapshotItem(string Kind, string Name, string? Group, string? Type, string Hash);
    private sealed record DiagnosticCheck(string Id, bool Ok, string Message, object? Value);
    private readonly ConcurrentDictionary<string, byte> consumedApplyTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> consumedSaveTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PreparedChange> preparedChanges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectSnapshot> projectSnapshots = new(StringComparer.Ordinal);
    public object GetStatus()
    {
        try
        {
            var status = Execute("status") as JsonObject ?? new JsonObject();
            status["ok"] = true;
            status["mode"] = IsSaveEnabled() ? "save-enabled" : IsWriteEnabled() ? "write-enabled" : "read-only";
            status["writeEnabled"] = IsWriteEnabled();
            status["saveEnabled"] = IsSaveEnabled();
            return status;
        }
        catch (Exception ex) { return new { ok = false, mode = IsWriteEnabled() ? "write-enabled" : "read-only", error = ex.GetBaseException().Message }; }
    }

    public object GetDiagnostics()
    {
        var configuredDll = Environment.GetEnvironmentVariable("TIA_OPENNESS_DLL");
        var workerCandidates = WorkerPathCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var workerPath = workerCandidates.FirstOrDefault(File.Exists);
        var opennessInstallations = new List<object>();
        var discoveredDlls = new List<string>();
        for (var version = 21; version >= 16; version--)
        {
            var root = $@"C:\Program Files\Siemens\Automation\Portal V{version}\PublicAPI";
            string[] dlls;
            try { dlls = Directory.Exists(root) ? Directory.GetFiles(root, "Siemens.Engineering.dll", SearchOption.AllDirectories) : []; }
            catch { dlls = []; }
            dlls = dlls.OrderByDescending(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), $"V{version}", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            discoveredDlls.AddRange(dlls);
            opennessInstallations.Add(new { version = $"V{version}", installed = dlls.Length > 0, dlls });
        }

        bool? opennessGroupMember = null;
        string? groupCheckError = null;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            opennessGroupMember = principal.IsInRole("Siemens TIA Openness");
        }
        catch (Exception ex) { groupCheckError = ex.GetBaseException().Message; }

        var selectedDll = !string.IsNullOrWhiteSpace(configuredDll) && File.Exists(configuredDll)
            ? Path.GetFullPath(configuredDll)
            : discoveredDlls.FirstOrDefault();
        var tokenConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIA_MCP_TOKEN"));
        var writeSecret = Environment.GetEnvironmentVariable("TIA_WRITE_SECRET");
        var checks = new DiagnosticCheck[]
        {
            new("worker", workerPath is not null, workerPath is null ? "TiaOpennessWorker.exe was not found." : "Worker executable found.", workerPath),
            new("openness-dll", selectedDll is not null, selectedDll is null ? "No Siemens.Engineering.dll was found for V16-V21." : "TIA Openness API found.", selectedDll),
            new("openness-group", opennessGroupMember == true, groupCheckError ?? (opennessGroupMember == true ? "Current user belongs to Siemens TIA Openness." : "Current user is not in Siemens TIA Openness; add it and sign out/in."), opennessGroupMember),
            new("authentication", tokenConfigured || IsLoopbackOnly(), tokenConfigured ? "Bearer authentication is configured." : IsLoopbackOnly() ? "No token configured; listener is loopback-only." : "A non-loopback listener requires TIA_MCP_TOKEN.", tokenConfigured),
            new("write-safeguards", !IsWriteEnabled() || (tokenConfigured && !string.IsNullOrWhiteSpace(writeSecret) && writeSecret.Length >= 32), !IsWriteEnabled() ? "Write mode is disabled (safe default)." : "Write mode requires bearer authentication and a 32+ character write secret.", IsWriteEnabled())
        };
        return new
        {
            ok = checks.All(check => check.Ok),
            generatedAtUtc = DateTime.UtcNow,
            mode = IsSaveEnabled() ? "save-enabled" : IsWriteEnabled() ? "write-enabled" : "read-only",
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            configuredDll,
            workerCandidates,
            opennessInstallations,
            checks
        };
    }

    public object ListChangeHistory(int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        var backupDirectory = Environment.GetEnvironmentVariable("TIA_BACKUP_DIRECTORY")
            ?? Path.Combine(AppContext.BaseDirectory, "backups");
        if (!Directory.Exists(backupDirectory)) return new { backupDirectory, entries = Array.Empty<object>() };

        var entries = new List<object>();
        foreach (var path in Directory.EnumerateFiles(backupDirectory, "*.journal.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(limit))
        {
            try
            {
                var journal = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                entries.Add(new
                {
                    operationId = journal?["operationId"]?.GetValue<string>(),
                    timestampUtc = journal?["timestampUtc"]?.GetValue<DateTime>(),
                    stage = journal?["stage"]?.GetValue<string>(),
                    plc = journal?["plc"]?.GetValue<string>(),
                    group = journal?["group"]?.GetValue<string>(),
                    name = journal?["name"]?.GetValue<string>(),
                    baselineHash = journal?["baselineHash"]?.GetValue<string>(),
                    proposedHash = journal?["proposedHash"]?.GetValue<string>(),
                    error = journal?["error"]?.GetValue<string>(),
                    journalPath = path
                });
            }
            catch (Exception ex)
            {
                entries.Add(new { stage = "unreadable", error = ex.GetBaseException().Message, journalPath = path });
            }
        }
        return new { backupDirectory, entries };
    }

    public object ListProjects() => Execute("projects");

    public JsonArray ListDevices(string? kind = null, string? nameContains = null, int offset = 0, int limit = 500) =>
        Filter(Execute("devices"), row =>
            MatchesExact(row, "kind", kind) && MatchesContains(row, "name", nameContains), offset, limit);

    public JsonArray ListBlocks(string? plc = null, string? type = null, string? groupContains = null,
        string? nameContains = null, int offset = 0, int limit = 500) =>
        Filter(Execute("blocks"), row =>
            MatchesExact(row, "plc", plc) && MatchesExact(row, "type", type) &&
            MatchesContains(row, "group", groupContains) && MatchesContains(row, "name", nameContains), offset, limit);

    public JsonArray ListTagTables(string? plc = null, string? groupContains = null,
        string? nameContains = null, int offset = 0, int limit = 500) =>
        Filter(Execute("tag-tables"), row =>
            MatchesExact(row, "plc", plc) && MatchesContains(row, "group", groupContains) &&
            MatchesContains(row, "name", nameContains), offset, limit);

    public JsonObject ExportTagTable(string plc, string name)
    {
        var result = Execute("export-tag-table", plc, name) as JsonObject
            ?? throw new InvalidOperationException("Worker returned an invalid tag-table export.");
        var xml = result["xml"]?.GetValue<string>() ?? throw new InvalidOperationException("Worker tag-table export did not contain XML.");
        result["baselineHash"] = ComputeBlockHash(xml);
        return result;
    }

    public object SearchTagTable(string plc, string name, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Search query must not be empty.");
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        var export = ExportTagTable(plc, name);
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var allTags = ParseTagEntries(document).ToArray();
        var matches = allTags.Where(tag => tag.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit)
            .Select(tag => tag.Value).ToArray();
        return new
        {
            plc, name,
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            query, count = matches.Length, totalMatching = allTags.Count(tag => tag.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)),
            truncated = matches.Length == limit, matches
        };
    }

    public object GetTagTableOverview(string plc, string name, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        var export = ExportTagTable(plc, name);
        var tags = ParseTagEntries(ParseXml(export["xml"]!.GetValue<string>())).Select(tag => tag.Value).ToArray();
        return new
        {
            plc, name,
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            total = tags.Length, offset, limit,
            returned = Math.Min(limit, Math.Max(0, tags.Length - offset)),
            tags = tags.Skip(offset).Take(limit).ToArray()
        };
    }

    public object GetPlcOverview(string plc)
    {
        var blocks = ListBlocks(plc: plc, limit: 1000).OfType<JsonObject>().ToArray();
        var tagTables = ListTagTables(plc: plc, limit: 1000).OfType<JsonObject>().ToArray();
        if (blocks.Length == 0 && tagTables.Length == 0)
            throw new InvalidOperationException($"PLC software not found or contains no visible blocks/tag tables: {plc}");
        return new
        {
            plc,
            blocks = new
            {
                total = blocks.Length,
                byType = CountBy(blocks, "type"),
                byLanguage = CountBy(blocks, "programmingLanguage"),
                groups = blocks.Select(row => row["group"]?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            tagTables = new
            {
                total = tagTables.Length,
                groups = tagTables.Select(row => row["group"]?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                tables = tagTables.Select(row => new { group = row["group"]?.GetValue<string>(), name = row["name"]?.GetValue<string>() }).ToArray()
            }
        };
    }

    private sealed record TagValue(string? Name, string? LogicalAddress, string? DataType, string[] Comments);
    private sealed record ParsedTag(TagValue Value, string SearchText);

    private static IEnumerable<ParsedTag> ParseTagEntries(XDocument document)
    {
        foreach (var element in document.Descendants().Where(element =>
                     element.Name.LocalName is "SW.Tags.PlcTag" or "PlcTag"))
        {
            var attributes = element.Descendants().Where(child => child.Name.LocalName == "AttributeList").FirstOrDefault() ?? element;
            string? Read(string localName) => attributes.Descendants().FirstOrDefault(child => child.Name.LocalName == localName)?.Value.Trim();
            var comments = element.Descendants().Where(child => child.Name.LocalName is "Text" or "Comment")
                .Select(child => child.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var tagName = Read("Name") ?? element.Attribute("Name")?.Value;
            var address = Read("LogicalAddress") ?? Read("Address");
            var dataType = Read("DataTypeName") ?? Read("DataType");
            var value = new TagValue(tagName, address, dataType, comments);
            yield return new ParsedTag(value, string.Join("\n", new[] { tagName, address, dataType }.Where(text => !string.IsNullOrWhiteSpace(text)).Concat(comments)));
        }
    }

    public object AuditPlcIo(string plc, int maxTagTables, int issueLimit)
    {
        if (maxTagTables is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxTagTables), "MaxTagTables must be between 1 and 200.");
        if (issueLimit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(issueLimit), "IssueLimit must be between 1 and 2000.");
        var tables = ListTagTables(plc: plc, limit: maxTagTables).OfType<JsonObject>().ToArray();
        var tags = new List<AuditedTag>();
        foreach (var table in tables)
        {
            var tableName = table["name"]?.GetValue<string>();
            var tableGroup = table["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tableName)) continue;
            var parsed = ParseTagEntries(ParseXml(ExportTagTable(plc, tableName)["xml"]!.GetValue<string>())).Take(5000);
            tags.AddRange(parsed.Select(tag => new AuditedTag(tableName, tableGroup, tag.Value.Name, tag.Value.LogicalAddress,
                tag.Value.DataType, tag.Value.Comments, NormalizeAddress(tag.Value.LogicalAddress), ClassifyAddress(tag.Value.LogicalAddress))));
        }
        var duplicateAddresses = tags.Where(tag => !string.IsNullOrWhiteSpace(tag.NormalizedAddress))
            .GroupBy(tag => tag.NormalizedAddress!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Take(issueLimit).Select(group => new { address = group.Key, tags = group.Select(DescribeAuditedTag).ToArray() }).ToArray();
        var duplicateNames = tags.Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Name!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Take(issueLimit).Select(group => new { name = group.Key, tags = group.Select(DescribeAuditedTag).ToArray() }).ToArray();
        var missingTypes = tags.Where(tag => string.IsNullOrWhiteSpace(tag.DataType)).Take(issueLimit).Select(DescribeAuditedTag).ToArray();
        var missingComments = tags.Where(tag => tag.Comments.Length == 0).Take(issueLimit).Select(DescribeAuditedTag).ToArray();
        return new
        {
            plc, auditedTagTables = tables.Length, truncatedTables = tables.Length == maxTagTables, totalTags = tags.Count,
            byAddressArea = tags.GroupBy(tag => tag.AddressArea, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            issueCounts = new
            {
                duplicateAddressGroups = duplicateAddresses.Length,
                duplicateNameGroups = duplicateNames.Length,
                missingTypes = missingTypes.Length,
                missingComments = missingComments.Length
            },
            duplicateAddresses, duplicateNames, missingTypes, missingComments,
            notes = new[]
            {
                "Duplicate addresses are exact normalized textual matches; overlapping bit/byte/word ranges are not inferred.",
                "Tags without configured logical addresses are excluded from address-conflict checks."
            }
        };
    }

    private sealed record AuditedTag(string Table, string? Group, string? Name, string? Address, string? DataType,
        string[] Comments, string? NormalizedAddress, string AddressArea);
    private static object DescribeAuditedTag(AuditedTag tag) => new
    {
        table = tag.Table, group = tag.Group, name = tag.Name, logicalAddress = tag.Address,
        dataType = tag.DataType, comments = tag.Comments, addressArea = tag.AddressArea
    };
    private static string? NormalizeAddress(string? address) => string.IsNullOrWhiteSpace(address)
        ? null : string.Concat(address.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    private static string ClassifyAddress(string? address)
    {
        var value = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(value)) return "unassigned";
        value = value.TrimStart('%');
        if (value.StartsWith("DB", StringComparison.OrdinalIgnoreCase)) return "data-block";
        if (value.StartsWith("I", StringComparison.OrdinalIgnoreCase) || value.StartsWith("E", StringComparison.OrdinalIgnoreCase)) return "input";
        if (value.StartsWith("Q", StringComparison.OrdinalIgnoreCase) || value.StartsWith("A", StringComparison.OrdinalIgnoreCase)) return "output";
        if (value.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return "memory";
        if (value.StartsWith("T", StringComparison.OrdinalIgnoreCase)) return "timer";
        if (value.StartsWith("C", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Z", StringComparison.OrdinalIgnoreCase)) return "counter";
        return "other";
    }

    public object AuditSymbolUsage(string plc, int maxBlocks, int maxTagTables, int issueLimit)
    {
        if (maxBlocks is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxBlocks), "MaxBlocks must be between 1 and 500.");
        if (maxTagTables is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxTagTables), "MaxTagTables must be between 1 and 200.");
        if (issueLimit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(issueLimit), "IssueLimit must be between 1 and 2000.");
        var definitions = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        var tables = ListTagTables(plc: plc, limit: maxTagTables).OfType<JsonObject>().ToArray();
        foreach (var table in tables)
        {
            var tableName = table["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tableName)) continue;
            foreach (var tag in ParseTagEntries(ParseXml(ExportTagTable(plc, tableName)["xml"]!.GetValue<string>())).Take(5000))
            {
                if (string.IsNullOrWhiteSpace(tag.Value.Name)) continue;
                if (!definitions.TryGetValue(tag.Value.Name, out var rows)) definitions[tag.Value.Name] = rows = [];
                rows.Add(new { table = tableName, group = table["group"]?.GetValue<string>(), tag = tag.Value });
            }
        }

        var directReferences = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var qualifiedReferences = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var blocks = ListBlocks(plc: plc, limit: maxBlocks).OfType<JsonObject>().ToArray();
        foreach (var block in blocks)
        {
            var blockName = block["name"]?.GetValue<string>();
            var group = block["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(blockName)) continue;
            var document = ParseXml(ExportBlock(plc, blockName, group)["xml"]!.GetValue<string>());
            foreach (var access in document.Descendants().Where(element => element.Name.LocalName == "Access" &&
                         string.Equals(element.Attribute("Scope")?.Value, "GlobalVariable", StringComparison.OrdinalIgnoreCase)))
            {
                var symbol = JoinComponents(access.Descendants().FirstOrDefault(element => element.Name.LocalName == "Symbol"));
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                var target = symbol.Contains('.') ? qualifiedReferences : directReferences;
                if (!target.TryGetValue(symbol, out var usedBy)) target[symbol] = usedBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedBy.Add(blockName);
            }
        }

        var unusedDefinitions = definitions.Keys.Where(name => !directReferences.ContainsKey(name)).Take(issueLimit)
            .Select(name => new { name, definitions = definitions[name] }).ToArray();
        var unresolvedDirect = directReferences.Keys.Where(name => !definitions.ContainsKey(name)).Take(issueLimit)
            .Select(name => new { symbol = name, usedByBlocks = directReferences[name].OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray();
        var resolved = directReferences.Keys.Where(definitions.ContainsKey).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new { symbol = name, referenceCount = directReferences[name].Count, usedByBlocks = directReferences[name].OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray();
        var qualified = qualifiedReferences.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(issueLimit)
            .Select(pair => new { symbolPath = pair.Key, usedByBlocks = pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(), classification = "qualified-or-db-member-review" }).ToArray();
        return new
        {
            plc, auditedTagTables = tables.Length, scannedBlocks = blocks.Length,
            truncatedTagTables = tables.Length == maxTagTables, truncatedBlocks = blocks.Length == maxBlocks,
            definitionCount = definitions.Count, directReferenceCount = directReferences.Count, qualifiedReferenceCount = qualifiedReferences.Count,
            possiblyUnusedDefinitions = unusedDefinitions,
            unresolvedDirectGlobalSymbols = unresolvedDirect,
            resolvedDirectGlobalSymbols = resolved,
            qualifiedReferences = qualified,
            notes = new[]
            {
                "Possibly unused means no direct GlobalVariable reference was found in the scanned blocks; HMI, alarms, recipes, indirect access, external systems, and unscanned blocks may still use it.",
                "Qualified symbol paths are reported for review and are not treated as missing PLC tags because they commonly represent DB or structured members."
            }
        };
    }

    private static Dictionary<string, int> CountBy(IEnumerable<JsonObject> rows, string property) =>
        rows.Select(row => row[property]?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public JsonObject ExportBlock(string plc, string name, string? group = null)
    {
        var result = Execute("export-block", plc, name, group ?? "") as JsonObject
            ?? throw new InvalidOperationException("Worker returned an invalid block export.");
        var xml = result["xml"]?.GetValue<string>() ?? throw new InvalidOperationException("Worker export did not contain XML.");
        result["baselineHash"] = ComputeBlockHash(xml);
        return result;
    }

    public object GetBlockOverview(string plc, string name, string? group)
    {
        var export = ExportBlock(plc, name, group);
        var xml = export["xml"]!.GetValue<string>();
        var document = ParseXml(xml);
        var compileUnits = document.Descendants().Where(element => element.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal))
            .Select((unit, index) => new
            {
                index,
                programmingLanguage = unit.Descendants().FirstOrDefault(element => element.Name.LocalName == "ProgrammingLanguage")?.Value,
                texts = unit.Descendants().Where(element => element.Name.LocalName == "Text")
                    .Select(element => element.Value.Trim()).Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal).Take(20).ToArray()
            }).ToArray();
        return new
        {
            plc,
            group = export["group"]?.GetValue<string>(),
            name,
            type = export["type"]?.GetValue<string>(),
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            characters = xml.Length,
            compileUnitCount = compileUnits.Length,
            programmingLanguages = document.Descendants().Where(element => element.Name.LocalName == "ProgrammingLanguage")
                .Select(element => element.Value).Distinct(StringComparer.Ordinal).ToArray(),
            compileUnits
        };
    }

    public object GetBlockInterface(string plc, string name, string? group)
    {
        var export = ExportBlock(plc, name, group);
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var sections = document.Descendants().Where(element => element.Name.LocalName == "Section")
            .Select(section => new
            {
                name = section.Attribute("Name")?.Value ?? "Unknown",
                members = section.Elements().Where(element => element.Name.LocalName == "Member")
                    .Select(ParseInterfaceMember).ToArray()
            }).ToArray();
        return new
        {
            plc,
            group = export["group"]?.GetValue<string>(),
            name,
            type = export["type"]?.GetValue<string>(),
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            totalMembers = sections.Sum(section => section.members.Length),
            sections
        };
    }

    public JsonArray ListDataBlocks(string plc, string? groupContains, string? nameContains, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(plc)) throw new InvalidOperationException("PLC name must not be empty.");
        return Filter(Execute("blocks"), row =>
            MatchesExact(row, "plc", plc) && IsDataBlockType(row["type"]?.GetValue<string>()) &&
            MatchesContains(row, "group", groupContains) && MatchesContains(row, "name", nameContains), offset, limit);
    }

    public object GetDataBlockOverview(string plc, string name, string? group, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
        if (limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 2000.");
        var export = ExportBlock(plc, name, group);
        var type = export["type"]?.GetValue<string>();
        if (!IsDataBlockType(type)) throw new InvalidOperationException($"Block '{name}' is not a GlobalDB or InstanceDB (actual type: {type ?? "unknown"}).");
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var members = new List<object>();
        foreach (var section in document.Descendants().Where(element => element.Name.LocalName == "Section"))
        {
            var sectionName = section.Attribute("Name")?.Value ?? "Static";
            foreach (var member in section.Elements().Where(element => element.Name.LocalName == "Member"))
                FlattenDataBlockMember(member, sectionName, "", 0, members);
        }
        var instanceOf = document.Descendants().FirstOrDefault(element => element.Name.LocalName is "InstanceOfName" or "InstanceOf")?.Value.Trim();
        return new
        {
            plc,
            group = export["group"]?.GetValue<string>(),
            name, type, instanceOf,
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            totalMembers = members.Count, offset, limit,
            returned = Math.Min(limit, Math.Max(0, members.Count - offset)),
            members = members.Skip(offset).Take(limit).ToArray()
        };
    }

    private static bool IsDataBlockType(string? type) =>
        string.Equals(type, "GlobalDB", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "InstanceDB", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(type) && (type.EndsWith("DB", StringComparison.OrdinalIgnoreCase) ||
         type.Contains("DataBlock", StringComparison.OrdinalIgnoreCase)));

    private static void FlattenDataBlockMember(XElement member, string section, string parentPath, int depth, List<object> rows)
    {
        if (depth > 32) throw new InvalidOperationException("Data-block member nesting exceeds the supported depth of 32.");
        var memberName = member.Attribute("Name")?.Value ?? "<unnamed>";
        var path = string.IsNullOrEmpty(parentPath) ? memberName : parentPath + "." + memberName;
        var attributes = member.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.OrdinalIgnoreCase);
        string? FindValue(params string[] names) => member.Elements().FirstOrDefault(element => names.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim()
            ?? member.Descendants().FirstOrDefault(element => names.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim();
        var comments = member.Elements().Where(element => element.Name.LocalName is "Comment" or "Text")
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var children = member.Elements().Where(element => element.Name.LocalName == "Member").ToArray();
        rows.Add(new
        {
            section, path, name = memberName, depth,
            dataType = member.Attribute("Datatype")?.Value ?? member.Attribute("DataType")?.Value,
            startValue = FindValue("StartValue", "InitialValue", "Value"),
            accessibility = member.Attribute("Accessibility")?.Value,
            retain = member.Attribute("Retain")?.Value,
            attributes, comments, childCount = children.Length
        });
        foreach (var child in children) FlattenDataBlockMember(child, section, path, depth + 1, rows);
    }

    public object GetBlockNetworks(string plc, string name, string? group, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        var export = ExportBlock(plc, name, group);
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var knownBlockNames = ListBlocks(plc: plc, limit: 1000).OfType<JsonObject>()
            .Select(row => row["name"]?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var networks = document.Descendants().Where(element => element.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal))
            .Select((unit, index) => ParseNetwork(unit, index, knownBlockNames)).ToArray();
        return new
        {
            plc, group = export["group"]?.GetValue<string>(), name,
            type = export["type"]?.GetValue<string>(), baselineHash = export["baselineHash"]?.GetValue<string>(),
            totalNetworks = networks.Length, offset, limit,
            returned = Math.Min(limit, Math.Max(0, networks.Length - offset)),
            networks = networks.Skip(offset).Take(limit).ToArray()
        };
    }

    public object GetBlockReferences(string plc, string name, string? group)
    {
        var export = ExportBlock(plc, name, group);
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var accesses = document.Descendants().Where(element => element.Name.LocalName == "Access").Select(access => new
        {
            scope = access.Attribute("Scope")?.Value,
            symbol = JoinComponents(access.Descendants().Where(element => element.Name.LocalName == "Symbol").FirstOrDefault()),
            constant = access.Descendants().FirstOrDefault(element => element.Name.LocalName == "Constant")?.Attribute("Name")?.Value
                       ?? access.Descendants().FirstOrDefault(element => element.Name.LocalName == "ConstantValue")?.Value.Trim()
        }).ToArray();
        var parts = document.Descendants().Where(element => element.Name.LocalName == "Part").Select(part => part.Attribute("Name")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var instances = document.Descendants().Where(element => element.Name.LocalName == "Instance")
            .Select(instance => JoinComponents(instance)).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            plc, group = export["group"]?.GetValue<string>(), name,
            type = export["type"]?.GetValue<string>(), baselineHash = export["baselineHash"]?.GetValue<string>(),
            scopes = accesses.Where(item => !string.IsNullOrWhiteSpace(item.scope)).GroupBy(item => item.scope!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            symbols = accesses.Select(item => item.symbol).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            constants = accesses.Select(item => item.constant).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            instances, parts
        };
    }

    private static object ParseNetwork(XElement unit, int index, HashSet<string> knownBlockNames)
    {
        var objectList = unit.Elements().FirstOrDefault(element => element.Name.LocalName == "ObjectList");
        string[] Multilingual(string composition) => objectList?.Elements()
            .Where(element => element.Name.LocalName == "MultilingualText" && string.Equals(element.Attribute("CompositionName")?.Value, composition, StringComparison.OrdinalIgnoreCase))
            .SelectMany(element => element.Descendants().Where(child => child.Name.LocalName == "MultilingualTextItem"))
            .Select(item =>
            {
                var culture = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Culture")?.Value.Trim();
                var text = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Text")?.Value.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : (string.IsNullOrWhiteSpace(culture) ? text : culture + ": " + text);
            }).Where(value => value is not null).Select(value => value!).ToArray() ?? [];
        var source = unit.Descendants().FirstOrDefault(element => element.Name.LocalName == "NetworkSource");
        var symbols = source?.Descendants().Where(element => element.Name.LocalName == "Symbol").Select(JoinComponents)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var parts = source?.Descendants().Where(element => element.Name.LocalName == "Part").Select(element => element.Attribute("Name")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return new
        {
            index,
            programmingLanguage = unit.Descendants().FirstOrDefault(element => element.Name.LocalName == "ProgrammingLanguage")?.Value.Trim(),
            titles = Multilingual("Title"), comments = Multilingual("Comment"),
            symbols,
            calls = parts.Where(part => knownBlockNames.Contains(part!)).ToArray(),
            instructions = parts.Where(part => !knownBlockNames.Contains(part!)).ToArray(),
            instances = source?.Descendants().Where(element => element.Name.LocalName == "Instance").Select(JoinComponents)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []
        };
    }

    private static string? JoinComponents(XElement? element)
    {
        if (element is null) return null;
        var names = element.DescendantsAndSelf().Where(child => child.Name.LocalName == "Component")
            .Select(child => child.Attribute("Name")?.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return names.Length == 0 ? null : string.Join(".", names);
    }

    public object SearchPlcBlocks(string plc, string query, string? type, string? groupContains, int maxBlocks, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Search query must not be empty.");
        if (maxBlocks is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxBlocks), "MaxBlocks must be between 1 and 500.");
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        var candidates = ListBlocks(plc, type, groupContains, null, 0, maxBlocks).OfType<JsonObject>().ToArray();
        var matches = new List<object>();
        foreach (var candidate in candidates)
        {
            if (matches.Count >= limit) break;
            var blockName = candidate["name"]?.GetValue<string>();
            var blockGroup = candidate["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(blockName)) continue;
            var export = ExportBlock(plc, blockName, blockGroup);
            var document = ParseXml(export["xml"]!.GetValue<string>());
            var snippets = document.Descendants()
                .Where(element => element.Name.LocalName is "Text" or "Component" or "Symbol" or "Constant" or "Member")
                .Select(element => new
                {
                    element = element.Name.LocalName,
                    text = (element.Attribute("Name")?.Value ?? element.Value).Trim()
                })
                .Where(item => item.text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.element + "\0" + item.text)
                .Take(20).ToArray();
            if (snippets.Length == 0) continue;
            matches.Add(new
            {
                plc, group = blockGroup, name = blockName,
                type = candidate["type"]?.GetValue<string>(),
                programmingLanguage = candidate["programmingLanguage"]?.GetValue<string>(),
                baselineHash = export["baselineHash"]?.GetValue<string>(),
                snippets
            });
        }
        return new { plc, query, type, groupContains, scannedBlocks = candidates.Length, count = matches.Count, truncated = matches.Count == limit, matches };
    }

    public object GetBlockDependencies(string plc, int maxBlocks)
    {
        if (maxBlocks is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxBlocks), "MaxBlocks must be between 1 and 500.");
        var blocks = ListBlocks(plc: plc, limit: maxBlocks).OfType<JsonObject>().ToArray();
        var knownNames = blocks.Select(block => block["name"]?.GetValue<string>()).Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = blocks.Select(block => new
        {
            name = block["name"]?.GetValue<string>(),
            group = block["group"]?.GetValue<string>(),
            type = block["type"]?.GetValue<string>(),
            programmingLanguage = block["programmingLanguage"]?.GetValue<string>()
        }).ToArray();
        var edges = new List<DependencyEdge>();
        foreach (var block in blocks)
        {
            var source = block["name"]?.GetValue<string>();
            var group = block["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source)) continue;
            var export = ExportBlock(plc, source, group);
            var document = ParseXml(export["xml"]!.GetValue<string>());
            var targets = document.Descendants().Where(element => element.Name.LocalName == "Part")
                .Select(element => element.Attribute("Name")?.Value).Where(target => !string.IsNullOrWhiteSpace(target) && knownNames.Contains(target))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(target => target, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var target in targets) edges.Add(new DependencyEdge(source, target!, "call"));
        }
        var incoming = edges.GroupBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var outgoing = edges.GroupBy(edge => edge.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new
        {
            plc, scannedBlocks = blocks.Length, truncated = blocks.Length == maxBlocks,
            nodeCount = nodes.Length, edgeCount = edges.Count, nodes, edges,
            roots = nodes.Where(node => node.name is not null && !incoming.ContainsKey(node.name)).Select(node => node.name).ToArray(),
            leaves = nodes.Where(node => node.name is not null && !outgoing.ContainsKey(node.name)).Select(node => node.name).ToArray()
        };
    }

    private sealed record DependencyEdge(string Source, string Target, string Kind);

    public object GetHardwareOverview(string? nameContains, string? typeContains)
    {
        var rows = ListDevices(limit: 1000).OfType<JsonObject>()
            .Where(row => MatchesContains(row, "name", nameContains) && MatchesContains(row, "type", typeContains)).ToArray();
        return new
        {
            total = rows.Length,
            filters = new { nameContains, typeContains },
            byKind = CountBy(rows, "kind"),
            byType = CountBy(rows, "type"),
            items = rows.Select(row => new
            {
                kind = row["kind"]?.GetValue<string>(),
                name = row["name"]?.GetValue<string>(),
                parent = row["parent"]?.GetValue<string>(),
                typeIdentifier = row["type"]?.GetValue<string>()
            }).ToArray(),
            addressNote = "Resolve configured symbolic I/O addresses through tia_get_tag_table_overview or tia_search_tag_table; DeviceItem does not expose a uniform address property across supported TIA versions."
        };
    }

    public object CreateProjectSnapshot(string? plc, int maxBlocks, int maxTagTables)
    {
        if (maxBlocks is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxBlocks), "MaxBlocks must be between 1 and 500.");
        if (maxTagTables is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxTagTables), "MaxTagTables must be between 1 and 200.");
        CleanupExpiredSnapshots();
        var selectedPlc = !string.IsNullOrWhiteSpace(plc) ? plc : DiscoverFirstPlcName();
        var snapshot = CaptureSnapshot(selectedPlc, maxBlocks, maxTagTables);
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        projectSnapshots[snapshotId] = snapshot;
        return new
        {
            snapshotId, snapshot.ProjectName, snapshot.Plc, snapshot.CreatedAtUtc, snapshot.ExpiresAtUtc,
            blockCount = snapshot.Blocks.Count, tagTableCount = snapshot.TagTables.Count,
            warning = snapshot.Blocks.Count == maxBlocks || snapshot.TagTables.Count == maxTagTables
                ? "Snapshot reached a configured object limit and may be incomplete." : null
        };
    }

    public object CompareProjectSnapshot(string snapshotId)
    {
        CleanupExpiredSnapshots();
        if (!projectSnapshots.TryGetValue(snapshotId, out var baseline))
            throw new InvalidOperationException("Snapshot was not found or has expired. Create a new snapshot.");
        var currentProject = GetSingleOpenProjectName();
        if (!string.Equals(currentProject, baseline.ProjectName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Open project changed: snapshot is for '{baseline.ProjectName}', current project is '{currentProject}'.");
        var current = CaptureSnapshot(baseline.Plc, Math.Min(500, baseline.Blocks.Count + 100), Math.Min(200, baseline.TagTables.Count + 50));
        var blockDiff = CompareSnapshotItems(baseline.Blocks, current.Blocks);
        var tagTableDiff = CompareSnapshotItems(baseline.TagTables, current.TagTables);
        return new
        {
            snapshotId, baseline.ProjectName, baseline.Plc, baseline.CreatedAtUtc, comparedAtUtc = DateTime.UtcNow,
            changed = blockDiff.ChangeCount + tagTableDiff.ChangeCount > 0,
            blocks = blockDiff.Value, tagTables = tagTableDiff.Value
        };
    }

    private ProjectSnapshot CaptureSnapshot(string plc, int maxBlocks, int maxTagTables)
    {
        var blocks = new Dictionary<string, SnapshotItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ListBlocks(plc: plc, limit: maxBlocks).OfType<JsonObject>())
        {
            var name = row["name"]?.GetValue<string>();
            var group = row["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var export = ExportBlock(plc, name, group);
            blocks[(group ?? "") + "\0" + name] = new SnapshotItem("block", name, group, row["type"]?.GetValue<string>(), export["baselineHash"]!.GetValue<string>());
        }
        var tagTables = new Dictionary<string, SnapshotItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ListTagTables(plc: plc, limit: maxTagTables).OfType<JsonObject>())
        {
            var name = row["name"]?.GetValue<string>();
            var group = row["group"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var export = ExportTagTable(plc, name);
            tagTables[(group ?? "") + "\0" + name] = new SnapshotItem("tagTable", name, group, null, export["baselineHash"]!.GetValue<string>());
        }
        var now = DateTime.UtcNow;
        return new ProjectSnapshot(GetSingleOpenProjectName(), plc, now, now.AddMinutes(30), blocks, tagTables);
    }

    private sealed record SnapshotDiff(object Value, int ChangeCount);
    private static SnapshotDiff CompareSnapshotItems(Dictionary<string, SnapshotItem> baseline, Dictionary<string, SnapshotItem> current)
    {
        var added = current.Keys.Except(baseline.Keys, StringComparer.OrdinalIgnoreCase).Select(key => current[key]).ToArray();
        var removed = baseline.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).Select(key => baseline[key]).ToArray();
        var common = baseline.Keys.Intersect(current.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var changed = common.Where(key => !string.Equals(baseline[key].Hash, current[key].Hash, StringComparison.OrdinalIgnoreCase))
            .Select(key => new { before = baseline[key], after = current[key] }).ToArray();
        return new SnapshotDiff(new { added, removed, changed, unchangedCount = common.Length - changed.Length }, added.Length + removed.Length + changed.Length);
    }

    private string DiscoverFirstPlcName()
    {
        var plc = ListBlocks(limit: 1).OfType<JsonObject>().Select(row => row["plc"]?.GetValue<string>()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? ListTagTables(limit: 1).OfType<JsonObject>().Select(row => row["plc"]?.GetValue<string>()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return plc ?? throw new InvalidOperationException("No PLC software was found in the open project.");
    }

    private void CleanupExpiredSnapshots()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in projectSnapshots.Where(pair => pair.Value.ExpiresAtUtc <= now).ToArray())
            projectSnapshots.TryRemove(pair.Key, out _);
    }

    private static object ParseInterfaceMember(XElement member)
    {
        var children = member.Elements().Where(element => element.Name.LocalName == "Member").Select(ParseInterfaceMember).ToArray();
        var comments = member.Descendants().Where(element => element.Name.LocalName is "Text" or "Comment")
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return new
        {
            name = member.Attribute("Name")?.Value,
            dataType = member.Attribute("Datatype")?.Value ?? member.Attribute("DataType")?.Value,
            accessibility = member.Attribute("Accessibility")?.Value,
            retain = member.Attribute("Retain")?.Value,
            comments,
            members = children
        };
    }

    public object SearchBlockText(string plc, string name, string? group, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Search query must not be empty.");
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 200.");
        var export = ExportBlock(plc, name, group);
        var document = ParseXml(export["xml"]!.GetValue<string>());
        var matches = new List<object>();
        if (document.Root is not null) Visit(document.Root, "/" + document.Root.Name.LocalName + "[1]");
        return new
        {
            plc,
            group = export["group"]?.GetValue<string>(),
            name,
            baselineHash = export["baselineHash"]?.GetValue<string>(),
            query,
            count = matches.Count,
            truncated = matches.Count == limit,
            matches
        };

        void Visit(XElement element, string path)
        {
            if (matches.Count >= limit) return;
            var directText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)).Trim();
            if (directText.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add(new { path, element = element.Name.LocalName, text = directText.Length <= 500 ? directText : directText[..500] });
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in element.Elements())
            {
                var localName = child.Name.LocalName;
                positions.TryGetValue(localName, out var position);
                positions[localName] = ++position;
                Visit(child, $"{path}/{localName}[{position}]");
                if (matches.Count >= limit) return;
            }
        }
    }

    public object PreviewBlockChange(string plc, string name, string? group, string baselineHash, string proposedXml)
    {
        var current = ExportBlock(plc, name, group);
        var currentXml = current["xml"]!.GetValue<string>();
        var currentHash = current["baselineHash"]!.GetValue<string>();
        if (!string.Equals(currentHash, baselineHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Baseline hash mismatch. The TIA block changed after it was exported; export it again before preparing a modification.");

        var currentDocument = ParseXml(currentXml);
        var proposedDocument = ParseXml(proposedXml);
        var currentIdentity = ReadBlockIdentity(currentDocument);
        var proposedIdentity = ReadBlockIdentity(proposedDocument);
        if (!string.Equals(currentIdentity.Name, proposedIdentity.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Block name cannot change in a replacement preview: expected '{currentIdentity.Name}', got '{proposedIdentity.Name}'.");
        if (!string.Equals(currentIdentity.Type, proposedIdentity.Type, StringComparison.Ordinal))
            throw new InvalidOperationException($"Block type cannot change in a replacement preview: expected '{currentIdentity.Type}', got '{proposedIdentity.Type}'.");
        if (!string.Equals(name, proposedIdentity.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Proposed XML contains block '{proposedIdentity.Name}', not requested block '{name}'.");

        ValidateNetworkUIds(proposedDocument);
        var proposedHash = ComputeBlockHash(proposedXml);
        var writeEnabled = IsWriteEnabled();
        return new
        {
            valid = true,
            changed = !string.Equals(currentHash, proposedHash, StringComparison.OrdinalIgnoreCase),
            plc,
            group = current["group"]?.GetValue<string>(),
            name = proposedIdentity.Name,
            type = proposedIdentity.Type,
            baselineHash = currentHash,
            proposedHash,
            current = DescribeXml(currentDocument, currentXml),
            proposed = DescribeXml(proposedDocument, proposedXml),
            diff = DescribeXmlDiff(currentDocument, proposedDocument),
            warnings = Array.Empty<string>(),
            writeEnabled,
            applyToken = writeEnabled ? CreateApplyToken(plc, group, name, currentHash, proposedHash) : null,
            writePerformed = false
        };
    }

    public object PrepareTextReplacement(string plc, string name, string? group, string find, string replace, int expectedOccurrences, bool includeXml)
    {
        if (string.Equals(find, replace, StringComparison.Ordinal))
            throw new InvalidOperationException("Find and replacement text are identical.");
        if (expectedOccurrences is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(expectedOccurrences), "Expected occurrences must be between 1 and 100.");

        var export = ExportBlock(plc, name, group);
        var currentXml = export["xml"]!.GetValue<string>();
        var actualOccurrences = CountOccurrences(currentXml, find);
        if (actualOccurrences != expectedOccurrences)
            throw new InvalidOperationException($"Expected {expectedOccurrences} exact occurrence(s), found {actualOccurrences}; no candidate was prepared.");

        var proposedXml = currentXml.Replace(find, replace, StringComparison.Ordinal);
        var baselineHash = export["baselineHash"]!.GetValue<string>();
        var preview = PreviewBlockChange(plc, name, group, baselineHash, proposedXml);
        RemoveExpiredPreparedChanges();
        var changeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
        preparedChanges[changeId] = new PreparedChange(plc, name, group, baselineHash, proposedXml, DateTime.UtcNow.AddMinutes(30));
        var result = new Dictionary<string, object?>
        {
            ["changeId"] = changeId,
            ["expiresAtUtc"] = DateTime.UtcNow.AddMinutes(30),
            ["plc"] = plc,
            ["group"] = export["group"]?.GetValue<string>(),
            ["name"] = name,
            ["find"] = find,
            ["replace"] = replace,
            ["occurrences"] = actualOccurrences,
            ["baselineHash"] = baselineHash,
            ["preview"] = preview,
            ["writePerformed"] = false
        };
        if (includeXml) result["proposedXml"] = proposedXml;
        return result;
    }

    public object ApplyPreparedChange(string changeId, string confirmation)
    {
        if (!string.Equals(confirmation, "APPLY_PREPARED_CHANGE", StringComparison.Ordinal))
            throw new InvalidOperationException("Explicit confirmation APPLY_PREPARED_CHANGE is required.");
        RemoveExpiredPreparedChanges();
        if (!preparedChanges.TryRemove(changeId, out var change))
            throw new InvalidOperationException("Prepared change was not found, expired, or already consumed.");
        if (!IsWriteEnabled())
            throw new InvalidOperationException("TIA write operations are disabled. Prepare again after enabling the controlled write window.");
        var proposedHash = ComputeBlockHash(change.ProposedXml);
        var applyToken = CreateApplyToken(change.Plc, change.Group, change.Name, change.BaselineHash, proposedHash);
        return ApplyBlockChange(change.Plc, change.Name, change.Group, change.BaselineHash, change.ProposedXml, applyToken);
    }

    private void RemoveExpiredPreparedChanges()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in preparedChanges.Where(pair => pair.Value.ExpiresAtUtc <= now).ToArray())
            preparedChanges.TryRemove(pair.Key, out _);
    }

    public object ApplyBlockChange(string plc, string name, string? group, string baselineHash, string proposedXml, string applyToken)
    {
        if (!IsWriteEnabled())
            throw new InvalidOperationException("TIA write operations are disabled. Set TIA_ENABLE_WRITE=true and configure TIA_WRITE_SECRET.");

        _ = PreviewBlockChange(plc, name, group, baselineHash, proposedXml);
        var proposedHash = ComputeBlockHash(proposedXml);
        if (string.Equals(baselineHash, proposedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The proposed XML is identical to the current block; no import was performed.");
        var expectedToken = CreateApplyToken(plc, group, name, baselineHash, proposedHash);
        if (!FixedTimeEquals(expectedToken, applyToken)) throw new InvalidOperationException("Invalid apply token. Preview this exact change again.");
        if (!consumedApplyTokens.TryAdd(applyToken, 0)) throw new InvalidOperationException("Apply token has already been used.");

        var current = ExportBlock(plc, name, group);
        var backupXml = current["xml"]!.GetValue<string>();
        var backupDirectory = Environment.GetEnvironmentVariable("TIA_BACKUP_DIRECTORY")
            ?? Path.Combine(AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var safeName = string.Concat($"{plc}-{name}".Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var operationId = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}-{baselineHash[..12]}";
        var backupPath = Path.Combine(backupDirectory, operationId + ".backup.xml");
        var proposedSnapshotPath = Path.Combine(backupDirectory, operationId + ".proposed.xml");
        var actualSnapshotPath = Path.Combine(backupDirectory, operationId + ".actual.xml");
        var journalPath = Path.Combine(backupDirectory, operationId + ".journal.json");
        File.WriteAllText(backupPath, backupXml, new UTF8Encoding(false));
        File.WriteAllText(proposedSnapshotPath, proposedXml, new UTF8Encoding(false));
        WriteJournal("prepared");

        var proposedPath = Path.Combine(Path.GetTempPath(), $"tia-apply-{Guid.NewGuid():N}.xml");
        File.WriteAllText(proposedPath, proposedXml, new UTF8Encoding(false));
        try
        {
            WriteJournal("importing");
            var importResult = Execute("import-block", plc, name, group ?? "", proposedPath);
            WriteJournal("compiling");
            var compileResult = Execute("compile-plc", plc) as JsonObject
                ?? throw new InvalidOperationException("PLC compile returned an invalid result.");
            var compileErrors = compileResult["errorCount"]?.GetValue<int>() ?? 0;
            if (compileErrors > 0)
                throw new InvalidOperationException($"PLC compile failed with {compileErrors} error(s).");
            WriteJournal("verifying-import");
            var after = ExportBlock(plc, name, group);
            File.WriteAllText(actualSnapshotPath, after["xml"]!.GetValue<string>(), new UTF8Encoding(false));
            var actualHash = after["baselineHash"]!.GetValue<string>();
            var actualXml = after["xml"]!.GetValue<string>();
            var exactHashMatched = string.Equals(actualHash, proposedHash, StringComparison.OrdinalIgnoreCase);
            var semanticHashMatched = exactHashMatched || string.Equals(
                ComputeSemanticBlockHash(actualXml), ComputeSemanticBlockHash(proposedXml), StringComparison.OrdinalIgnoreCase);
            if (!semanticHashMatched)
                throw new InvalidOperationException("Post-import verification found a semantic difference between the proposed and actual XML.");
            WriteJournal("succeeded");
            var projectName = GetSingleOpenProjectName();
            var saveEnabled = IsSaveEnabled();
            return new
            {
                ok = true, plc, group = after["group"]?.GetValue<string>(), name,
                baselineHash, appliedHash = actualHash, exactHashMatched, semanticHashMatched,
                backupPath, importResult, compileResult,
                projectName, saveEnabled,
                saveToken = saveEnabled ? CreateSaveToken(projectName, plc, group, name, actualHash) : null,
                projectSaved = false, compiled = true, writePerformed = true
            };
        }
        catch (Exception applyException)
        {
            WriteJournal("rolling-back", applyException.GetBaseException().Message);
            var rollbackPath = Path.Combine(Path.GetTempPath(), $"tia-rollback-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(rollbackPath, backupXml, new UTF8Encoding(false));
                Execute("import-block", plc, name, group ?? "", rollbackPath);
                Execute("compile-plc", plc);
                WriteJournal("rolled-back", applyException.GetBaseException().Message);
            }
            catch (Exception rollbackException)
            {
                WriteJournal("rollback-failed", rollbackException.GetBaseException().Message);
                throw new AggregateException("Block apply failed and automatic rollback also failed. Restore the backup manually: " + backupPath,
                    applyException, rollbackException);
            }
            finally { if (File.Exists(rollbackPath)) File.Delete(rollbackPath); }
            throw new InvalidOperationException("Block apply failed; the original XML was re-imported from backup.", applyException);
        }
        finally { if (File.Exists(proposedPath)) File.Delete(proposedPath); }

        void WriteJournal(string stage, string? error = null)
        {
            File.WriteAllText(journalPath, JsonSerializer.Serialize(new
            {
                operationId, timestampUtc = DateTime.UtcNow, stage, plc, group, name,
                baselineHash, proposedHash, backupPath, proposedSnapshotPath, actualSnapshotPath, error
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }

    public JsonNode CompilePlc(string plc)
    {
        if (!IsWriteEnabled())
            throw new InvalidOperationException("PLC compilation is disabled. Set TIA_ENABLE_WRITE=true and configure TIA_WRITE_SECRET.");
        return Execute("compile-plc", plc);
    }

    public object SaveProject(string projectName, string plc, string name, string? group, string expectedBlockHash, string saveToken)
    {
        if (!IsSaveEnabled())
            throw new InvalidOperationException("TIA project saving is disabled. Set TIA_ENABLE_SAVE=true together with the write safeguards.");
        var current = ExportBlock(plc, name, group);
        var currentHash = current["baselineHash"]!.GetValue<string>();
        if (!string.Equals(currentHash, expectedBlockHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Block hash changed after apply; the project was not saved.");
        var expectedToken = CreateSaveToken(projectName, plc, group, name, currentHash);
        if (!FixedTimeEquals(expectedToken, saveToken)) throw new InvalidOperationException("Invalid save token.");
        if (!consumedSaveTokens.TryAdd(saveToken, 0)) throw new InvalidOperationException("Save token has already been used.");
        var actualProjectName = GetSingleOpenProjectName();
        if (!string.Equals(actualProjectName, projectName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Open project changed: expected '{projectName}', found '{actualProjectName}'.");
        var result = Execute("save-project", projectName);
        return new { ok = true, projectName, verifiedBlockHash = currentHash, result, projectSaved = true };
    }

    private static bool IsWriteEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("TIA_ENABLE_WRITE"), "true", StringComparison.OrdinalIgnoreCase) &&
        (Environment.GetEnvironmentVariable("TIA_WRITE_SECRET")?.Length ?? 0) >= 32;

    private static bool IsSaveEnabled() => IsWriteEnabled() &&
        string.Equals(Environment.GetEnvironmentVariable("TIA_ENABLE_SAVE"), "true", StringComparison.OrdinalIgnoreCase);

    private static string CreateApplyToken(string plc, string? group, string name, string baselineHash, string proposedHash)
    {
        var secret = Environment.GetEnvironmentVariable("TIA_WRITE_SECRET")
            ?? throw new InvalidOperationException("TIA_WRITE_SECRET is not configured.");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = string.Join("\n", plc, group ?? "", name, baselineHash.ToLowerInvariant(), proposedHash.ToLowerInvariant());
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string CreateSaveToken(string projectName, string plc, string? group, string name, string appliedHash)
    {
        var secret = Environment.GetEnvironmentVariable("TIA_WRITE_SECRET")
            ?? throw new InvalidOperationException("TIA_WRITE_SECRET is not configured.");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = string.Join("\n", "save", projectName, plc, group ?? "", name, appliedHash.ToLowerInvariant());
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private string GetSingleOpenProjectName()
    {
        var projects = Execute("projects") as JsonArray ?? throw new InvalidOperationException("Worker returned invalid project data.");
        var names = projects.OfType<JsonObject>().Select(item => item["project"]?["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 1 ? names[0]! : throw new InvalidOperationException($"Expected exactly one open project, found {names.Length}.");
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string ComputeBlockHash(string xml)
    {
        var document = ParseXml(xml);
        document.Root?.Element("DocumentInfo")?.Remove();
        foreach (var whitespace in document.DescendantNodes().OfType<XText>().Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
            whitespace.Remove();
        var normalized = document.ToString(SaveOptions.DisableFormatting);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string ComputeSemanticBlockHash(string xml)
    {
        var document = ParseXml(xml);
        document.Root?.Element("DocumentInfo")?.Remove();
        var canonical = document.Root is null ? string.Empty : Canonicalize(document.Root).ToString(SaveOptions.DisableFormatting);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        static XElement Canonicalize(XElement source)
        {
            var attributes = source.Attributes()
                .Where(attribute => attribute.Name.LocalName is not ("ID" or "UId"))
                .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value));
            var nodes = source.Nodes().Select<XNode, XNode?>(node => node switch
            {
                XElement element => Canonicalize(element),
                XCData cdata => new XCData(cdata.Value),
                XText text when !string.IsNullOrWhiteSpace(text.Value) => new XText(text.Value),
                _ => null
            }).Where(node => node is not null);
            return new XElement(source.Name, attributes, nodes!);
        }
    }

    private static XDocument ParseXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new InvalidOperationException("XML must not be empty.");
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20_000_000
        });
        try { return XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { throw new InvalidOperationException($"Proposed XML is not well formed: {ex.Message}", ex); }
    }

    private static (string Name, string Type) ReadBlockIdentity(XDocument document)
    {
        if (document.Root?.Name.LocalName != "Document") throw new InvalidOperationException("Expected TIA XML root element 'Document'.");
        var block = document.Root.Elements().FirstOrDefault(element => element.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
        if (block is null) throw new InvalidOperationException("TIA XML does not contain a PLC block element.");
        var name = block.Elements().FirstOrDefault(element => element.Name.LocalName == "AttributeList")?
            .Elements().FirstOrDefault(element => element.Name.LocalName == "Name")?.Value;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("TIA XML block name is missing.");
        return (name, block.Name.LocalName["SW.Blocks.".Length..]);
    }

    private static void ValidateNetworkUIds(XDocument document)
    {
        foreach (var network in document.Descendants().Where(element => element.Name.LocalName == "NetworkSource"))
        {
            foreach (var collectionName in new[] { "Parts", "Wires" })
            {
                var collection = network.Descendants().FirstOrDefault(element => element.Name.LocalName == collectionName);
                if (collection is null) continue;
                var duplicates = collection.Elements().Attributes("UId").GroupBy(attribute => attribute.Value)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicates.Length > 0)
                    throw new InvalidOperationException($"Duplicate UId values in network {collectionName}: " + string.Join(", ", duplicates));
            }
        }
    }

    private static object DescribeXml(XDocument document, string xml) => new
    {
        characters = xml.Length,
        elements = document.Descendants().Count(),
        networks = document.Descendants().Count(element => element.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal)),
        programmingLanguages = document.Descendants().Where(element => element.Name.LocalName == "ProgrammingLanguage")
            .Select(element => element.Value).Distinct().ToArray()
    };

    private static object DescribeXmlDiff(XDocument current, XDocument proposed)
    {
        var currentSnapshot = BuildXmlSnapshot(current);
        var proposedSnapshot = BuildXmlSnapshot(proposed);
        var added = proposedSnapshot.Keys.Except(currentSnapshot.Keys, StringComparer.Ordinal).OrderBy(path => path).ToArray();
        var removed = currentSnapshot.Keys.Except(proposedSnapshot.Keys, StringComparer.Ordinal).OrderBy(path => path).ToArray();
        var modified = currentSnapshot.Keys.Intersect(proposedSnapshot.Keys, StringComparer.Ordinal)
            .Where(path => !string.Equals(currentSnapshot[path], proposedSnapshot[path], StringComparison.Ordinal))
            .OrderBy(path => path).ToArray();

        return new
        {
            addedElements = added.Length,
            removedElements = removed.Length,
            modifiedElements = modified.Length,
            sampleLimit = 20,
            addedPaths = added.Take(20).ToArray(),
            removedPaths = removed.Take(20).ToArray(),
            modifiedPaths = modified.Take(20).ToArray(),
            truncated = added.Length > 20 || removed.Length > 20 || modified.Length > 20
        };
    }

    private static Dictionary<string, string> BuildXmlSnapshot(XDocument source)
    {
        var document = new XDocument(source);
        document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "DocumentInfo")?.Remove();
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.Root is not null) AddElement(document.Root, "/" + document.Root.Name.LocalName + "[1]");
        return snapshot;

        void AddElement(XElement element, string path)
        {
            var attributes = string.Join("|", element.Attributes().OrderBy(attribute => attribute.Name.LocalName)
                .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value));
            var directText = string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)).Trim();
            snapshot[path] = attributes + "\n" + directText;

            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in element.Elements())
            {
                var localName = child.Name.LocalName;
                positions.TryGetValue(localName, out var position);
                positions[localName] = ++position;
                AddElement(child, $"{path}/{localName}[{position}]");
            }
        }
    }

    private static JsonArray Filter(JsonNode source, Func<JsonObject, bool> predicate, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        var result = new JsonArray();
        foreach (var row in (source as JsonArray ?? []).OfType<JsonObject>().Where(predicate).Skip(offset).Take(limit))
            result.Add(row.DeepClone());
        return result;
    }

    private static bool MatchesExact(JsonObject row, string property, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(row[property]?.GetValue<string>(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesContains(JsonObject row, string property, string? value) =>
        string.IsNullOrWhiteSpace(value) || (row[property]?.GetValue<string>()?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false);

    private static int CountOccurrences(string value, string search)
    {
        if (string.IsNullOrEmpty(search)) throw new InvalidOperationException("Find text must not be empty.");
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
            count++;
        return count;
    }

    private static JsonNode Execute(string command, params string[] arguments)
    {
        var workerPath = ResolveWorkerPath();
        var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("TIA_WORKER_TIMEOUT_SECONDS"), out var configured)
            ? Math.Max(1, configured) : 60;
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start TIA Openness Worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"TIA Openness Worker timed out after {timeoutSeconds} seconds.");
        }
        Task.WaitAll(stdout, stderr);
        var envelope = JsonNode.Parse(stdout.Result) as JsonObject
            ?? throw new InvalidOperationException("TIA Openness Worker returned invalid JSON.");
        if (envelope["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(envelope["error"]?.GetValue<string>() ?? stderr.Result.Trim() ?? "TIA Openness Worker failed.");
        return envelope["data"]?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }

    private static string ResolveWorkerPath()
    {
        return WorkerPathCandidates().FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("TiaOpennessWorker.exe not found. Set TIA_WORKER_PATH to its full path.");
    }

    private static IEnumerable<string> WorkerPathCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("TIA_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) yield return Path.GetFullPath(configured);
        yield return Path.Combine(AppContext.BaseDirectory, "worker", "TiaOpennessWorker.exe");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "TiaOpennessWorker", "bin", "Debug", "net452", "TiaOpennessWorker.exe");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "TiaOpennessWorker", "bin", "Release", "net452", "TiaOpennessWorker.exe");
    }

    private static bool IsLoopbackOnly()
    {
        var url = Environment.GetEnvironmentVariable("TIA_MCP_URL") ?? "http://127.0.0.1:5111";
        return url.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                 System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address)));
    }
}

sealed record BlockChangeRequest(string Plc, string Name, string? Group, string BaselineHash, string Xml);
sealed record ApplyBlockChangeRequest(string Plc, string Name, string? Group, string BaselineHash, string Xml, string ApplyToken);
sealed record SaveProjectRequest(string ProjectName, string Plc, string Name, string? Group, string ExpectedBlockHash, string SaveToken);
sealed record ProjectSnapshotRequest(string? Plc, int? MaxBlocks, int? MaxTagTables);
sealed record ChatRequest(string Message, string? PreviousResponseId);
sealed record OpenAiSettingsRequest(string ApiKey, string? Model);
