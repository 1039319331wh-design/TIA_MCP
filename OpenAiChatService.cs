using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class OpenAiChatService(HttpClient httpClient, TiaOpennessReader tia, LocalSecretStore secrets)
{
    private const int MaxToolRounds = 8;

    public async Task<object> SendAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) throw new ArgumentException("Message must not be empty.");
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? secrets.GetOpenAiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not configured on this machine.");

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? secrets.Model ?? "gpt-5.6";
        JsonObject response = await CreateResponseAsync(apiKey, model, JsonValue.Create(request.Message)!, request.PreviousResponseId, cancellationToken);
        var calls = new List<object>();

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var functionCalls = (response["output"] as JsonArray)?.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "function_call").ToArray() ?? [];
            if (functionCalls.Length == 0)
                return new
                {
                    responseId = response["id"]?.GetValue<string>(),
                    message = ExtractText(response),
                    model = response["model"]?.GetValue<string>() ?? model,
                    toolCalls = calls
                };

            var outputs = new JsonArray();
            foreach (var call in functionCalls)
            {
                var name = call["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Function call name is missing.");
                var argumentsText = call["arguments"]?.GetValue<string>() ?? "{}";
                var arguments = JsonNode.Parse(argumentsText) as JsonObject ?? new JsonObject();
                object result;
                try { result = ExecuteTool(name, arguments); }
                catch (Exception ex) { result = new { ok = false, error = ex.GetBaseException().Message }; }
                var resultJson = JsonSerializer.Serialize(result);
                calls.Add(new { name, arguments, ok = !resultJson.Contains("\"ok\":false", StringComparison.Ordinal) });
                outputs.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call["call_id"]?.GetValue<string>(),
                    ["output"] = resultJson
                });
            }

            response = await CreateResponseAsync(apiKey, model, outputs,
                response["id"]?.GetValue<string>(), cancellationToken);
        }
        throw new InvalidOperationException($"The model exceeded the {MaxToolRounds}-round tool limit.");
    }

    private object ExecuteTool(string name, JsonObject arguments) => name switch
    {
        "tia_status" => tia.GetStatus(),
        "tia_list_projects" => tia.ListProjects(),
        "tia_list_devices" => tia.ListDevices(String(arguments, "kind"), String(arguments, "nameContains"),
            Integer(arguments, "offset", 0), Integer(arguments, "limit", 100)),
        "tia_list_blocks" => tia.ListBlocks(String(arguments, "plc"), String(arguments, "type"),
            String(arguments, "groupContains"), String(arguments, "nameContains"),
            Integer(arguments, "offset", 0), Integer(arguments, "limit", 100)),
        "tia_export_block" => tia.ExportBlock(Required(arguments, "plc"), Required(arguments, "name"), String(arguments, "group")),
        _ => throw new InvalidOperationException("Unknown or disallowed chat tool: " + name)
    };

    private async Task<JsonObject> CreateResponseAsync(string apiKey, string model, JsonNode input,
        string? previousResponseId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = """
                你是运行在 TIA Portal 本机控制台中的 Codex 工程助手。使用提供的只读工具检查项目、PLC、设备和程序块。
                回答使用简体中文，先给结论，再给必要证据。不要声称已执行未提供的工具。
                当前对话工具绝不允许导入、保存或下载。用户要求修改时，可以读取和分析 XML，明确说明拟修改内容与风险，
                但必须让用户通过控制台的独立确认流程执行写入。不要输出或索取 OPENAI_API_KEY、Bearer Token 或写入密钥。
                """,
            ["input"] = input.DeepClone(),
            ["tools"] = ToolDefinitions()
        };
        if (!string.IsNullOrWhiteSpace(previousResponseId)) body["previous_response_id"] = previousResponseId;

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonNode.Parse(responseText)?["error"]?["message"]?.GetValue<string>();
            throw new InvalidOperationException(error ?? $"OpenAI API returned HTTP {(int)response.StatusCode}.");
        }
        return JsonNode.Parse(responseText) as JsonObject ?? throw new InvalidOperationException("OpenAI API returned invalid JSON.");
    }

    private static JsonArray ToolDefinitions() =>
    [
        Tool("tia_status", "检查 Openness DLL 和可附加的 TIA Portal 实例。", new JsonObject()),
        Tool("tia_list_projects", "列出当前用户会话中已打开的 TIA 项目。", new JsonObject()),
        Tool("tia_list_devices", "列出第一个打开项目中的设备和设备项。", Properties(
            ("kind", "string"), ("nameContains", "string"), ("offset", "integer"), ("limit", "integer"))),
        Tool("tia_list_blocks", "筛选并列出 PLC 程序块。", Properties(
            ("plc", "string"), ("type", "string"), ("groupContains", "string"),
            ("nameContains", "string"), ("offset", "integer"), ("limit", "integer"))),
        Tool("tia_export_block", "按 PLC、块名和可选组路径导出完整的只读 TIA XML。", Properties(
            ("plc", "string"), ("name", "string"), ("group", "string")), ["plc", "name"])
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var parameters = new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false
        };
        if (required is { Length: > 0 }) parameters["required"] = new JsonArray(required.Select(value => JsonValue.Create(value)).ToArray());
        return new JsonObject
        {
            ["type"] = "function", ["name"] = name, ["description"] = description, ["parameters"] = parameters
        };
    }

    private static JsonObject Properties(params (string Name, string Type)[] items)
    {
        var result = new JsonObject();
        foreach (var item in items) result[item.Name] = new JsonObject { ["type"] = item.Type };
        return result;
    }

    private static string ExtractText(JsonObject response) => string.Join("\n",
        (response["output"] as JsonArray)?.OfType<JsonObject>()
            .Where(item => item["type"]?.GetValue<string>() == "message")
            .SelectMany(item => (item["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(item => item["type"]?.GetValue<string>() == "output_text")
            .Select(item => item["text"]?.GetValue<string>()).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

    private static string? String(JsonObject arguments, string name) => arguments[name]?.GetValue<string>();
    private static string Required(JsonObject arguments, string name) =>
        !string.IsNullOrWhiteSpace(String(arguments, name)) ? String(arguments, name)! : throw new ArgumentException("Missing argument: " + name);
    private static int Integer(JsonObject arguments, string name, int fallback) => arguments[name]?.GetValue<int>() ?? fallback;
}
