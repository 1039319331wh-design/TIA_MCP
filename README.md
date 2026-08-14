# TIA Codex Console 与 Openness Bridge

这是一个运行在 TIA Portal 所在 Windows 机器上的只读 MCP/REST 桥接服务。

服务同时提供本机 Web 控制台，可在浏览器中浏览项目和程序块、与 Codex 对话并查看工具调用记录。打开服务根地址即可使用：

```text
http://127.0.0.1:5111/
```

对话使用 OpenAI Responses API。API Key 只从服务进程环境变量读取，不会发送到浏览器：

```powershell
$env:OPENAI_API_KEY = '你的 OpenAI API Key'
$env:OPENAI_MODEL = 'gpt-5.6'
```

也可以在控制台右上角“连接设置”中输入 API Key。Key 仅通过回环地址提交给本机服务，并使用 Windows DPAPI 按当前用户加密保存；页面刷新后不会保留明文。默认加密配置位置：

```text
%LOCALAPPDATA%\TiaCodexConsole\secrets.json
```

环境变量的优先级高于加密配置。不要将 `secrets.json` 复制到其他 Windows 用户或机器，因为它无法在那里解密。

控制台中的 Codex 只获得状态、项目、设备、块列表和 XML 导出工具。导入与保存不会作为自动对话工具开放，仍通过独立预检和令牌流程控制。

项目采用双进程架构：

- `TiaMcpBridge`：基于 .NET 8 的 HTTP 与 MCP 服务。
- `TiaOpennessWorker`：基于 .NET Framework 4.5.2、运行于本机 .NET Framework 4.8 Runtime，负责加载 Siemens Openness DLL。

采用 Worker 是因为 TIA Portal V16–V20 的 `Siemens.Engineering.dll` 依赖 .NET Framework API，不能直接加载到 .NET 8 进程。

## 当前能力

- 检测指定版本中可附加的 TIA Portal 实例
- 列出已打开的项目
- 遍历设备和 `DeviceItem`
- 遍历 PLC 程序块及嵌套块组
- 将指定 PLC 程序块导出为完整 TIA Portal XML
- 提供 REST API 和 MCP Streamable HTTP 端点
- 提供不会附加或修改项目的依赖诊断，检查 Worker、V16–V21 Openness API、用户组权限和安全配置
- 严格只读：不保存、不导入、不编译、不下载

## 环境准备

1. 安装 TIA Portal 及对应 Openness 组件。
2. 将运行服务的 Windows 用户加入 `Siemens TIA Openness` 用户组，然后注销并重新登录。
3. 安装 .NET 8 SDK。
4. 在 TIA Portal 中打开待测试项目。

本项目的 Worker 以 `net452` 编译，可在已安装 .NET Framework 4.8 的 TIA 虚拟机中运行。

## 构建

```powershell
dotnet build .\TiaMcpBridge.csproj
```

构建主项目时会自动构建 Worker，并将其复制到：

```text
bin\Debug\net8.0-windows\worker\TiaOpennessWorker.exe
```

## 运行

建议明确选择与当前 TIA Portal 工程相同版本的 Openness DLL：

```powershell
$env:TIA_OPENNESS_DLL = 'C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll'
$env:TIA_MCP_URL = 'http://127.0.0.1:5111'
$env:TIA_MCP_TOKEN = '请替换为足够长的随机字符串'
dotnet run --project .\TiaMcpBridge.csproj
```

若未设置 `TIA_OPENNESS_DLL`，Worker 会从 V21 到 V15 扫描常见安装位置，并选择找到的第一个 DLL。安装多个 TIA 版本时应显式配置，避免连接错误版本。

第一次附加 TIA Portal 时可能出现 Openness 访问确认窗口，需要在虚拟机桌面中允许访问。

## REST 测试

```powershell
$headers = @{ Authorization = 'Bearer 请替换为相同的随机字符串' }
Invoke-RestMethod http://127.0.0.1:5111/health -Headers $headers
Invoke-RestMethod http://127.0.0.1:5111/api/diagnostics -Headers $headers
Invoke-RestMethod http://127.0.0.1:5111/api/projects -Headers $headers
Invoke-RestMethod http://127.0.0.1:5111/api/devices -Headers $headers
Invoke-RestMethod http://127.0.0.1:5111/api/blocks -Headers $headers
Invoke-RestMethod 'http://127.0.0.1:5111/api/blocks/export?plc=水系&name=Main&group=程序块' -Headers $headers
```

设备接口支持以下可选查询参数：

- `kind`：精确筛选 `device` 或 `deviceItem`
- `nameContains`：名称模糊匹配，不区分大小写
- `offset`：跳过匹配结果的数量，默认 `0`
- `limit`：返回数量，默认 `500`，最大 `1000`

程序块接口支持以下可选查询参数：

- `plc`：精确匹配 PLC 名称
- `type`：精确匹配 `OB`、`FB`、`FC`、`GlobalDB` 或 `InstanceDB`
- `groupContains`：块组路径模糊匹配
- `nameContains`：块名称模糊匹配
- `offset`、`limit`：分页参数

例如，只读取“油系”PLC 的前 20 个 FB：

```powershell
Invoke-RestMethod 'http://127.0.0.1:5111/api/blocks?plc=油系&type=FB&limit=20' -Headers $headers
```

MCP 地址：

```text
http://<虚拟机IP>:5111/mcp
```

可用工具：

- `tia_status`
- `tia_diagnostics`
- `tia_list_projects`
- `tia_list_devices`
- `tia_list_blocks`
- `tia_list_tag_tables`
- `tia_export_tag_table`
- `tia_search_tag_table`
- `tia_get_tag_table_overview`
- `tia_get_plc_overview`
- `tia_get_block_interface`
- `tia_search_plc_blocks`
- `tia_get_block_dependencies`
- `tia_get_hardware_overview`
- `tia_create_project_snapshot`
- `tia_compare_project_snapshot`
- `tia_list_data_blocks`
- `tia_get_data_block_overview`
- `tia_get_block_networks`
- `tia_get_block_references`
- `tia_audit_plc_io`
- `tia_export_block`
- `tia_preview_block_change`
- `tia_apply_block_change`（默认禁用）
- `tia_compile_plc`（默认禁用）
- `tia_save_project`（默认禁用）

`tia_list_devices` 和 `tia_list_blocks` 的 MCP 参数与上述 REST 筛选参数一致。

变量表工具支持按 PLC、分组路径和名称筛选。`tia_export_tag_table` 返回只读 XML 与稳定哈希；优先使用 `tia_search_tag_table` 按变量名、逻辑地址、数据类型或注释检索，避免将完整变量表放入对话上下文。

开始分析一个 PLC 时，建议先调用 `tia_get_plc_overview` 获取块类型、编程语言、程序分组和变量表清单。`tia_get_tag_table_overview` 会将变量表解析为紧凑的结构化条目，适合分批读取名称、逻辑地址、数据类型与注释。

`tia_get_block_interface` 将 FB、FC、OB 和 DB 的接口解析为 Input、Output、InOut、Static、Temp、Constant、Return 等区段，并保留嵌套成员和注释。`tia_search_plc_blocks` 可在受限数量的块中搜索符号、注释或调用痕迹；大型项目应通过 `type`、`groupContains` 和 `maxBlocks` 缩小范围。

`tia_get_block_dependencies` 将 LAD/FBD XML 中与已知块名匹配的调用部件整理为节点和边，并返回可能的根块与叶子块。`tia_get_hardware_overview` 返回设备层级、模块名称和 `TypeIdentifier`；不同 TIA 版本的硬件对象没有统一地址属性，因此 I/O 地址继续以变量表工具返回的符号地址为准。

在修改或人工联调前可调用 `tia_create_project_snapshot` 建立临时只读基线，之后使用 `tia_compare_project_snapshot` 检查程序块和变量表的新增、删除与哈希变化。快照只保存在桥接进程内存中，默认 30 分钟失效，服务重启后清空；大型工程可通过最大块数和变量表数限制开销。

`tia_list_data_blocks` 用于筛选全局 DB 与实例 DB。`tia_get_data_block_overview` 将 DB 接口递归展开为成员路径，返回区段、数据类型、初始值、Retain、访问属性、注释和原始成员属性；支持分页，并限制最大嵌套深度，适合分析配方、设备状态、参数和实例数据。

`tia_get_block_networks` 按 `CompileUnit` 返回每个网络的语言、标题、注释、符号路径、实例、调用块与指令部件，适合逐网络解释 LAD/FBD。`tia_get_block_references` 提供单块唯一引用摘要，包括访问作用域、符号、常量、实例和部件名称；调用识别只接受当前 PLC 中真实存在的块名。

`tia_audit_plc_io` 遍历指定 PLC 的变量表，统计输入、输出、M 区、DB、定时器和计数器等地址区域，并报告精确地址重复、符号名重复、缺失数据类型和缺失注释。地址冲突仅按标准化后的完整地址文本判断，不推断位、字节、字或双字之间的范围重叠。

`tia_export_block` 需要 `plc` 和 `name`，并接受可选的精确 `group` 路径。若存在同名块，必须指定组路径。该工具只导出 XML，不修改工程。

导出结果包含忽略易变 `DocumentInfo` 的稳定 `baselineHash`。准备候选 XML 后，调用 `tia_preview_block_change` 并传入：

- `plc`、`name`、可选 `group`
- 导出时返回的 `baselineHash`
- 完整候选 `xml`

预检会重新读取当前块并校验基线哈希、XML 格式、块名称、块类型和网络 UId。返回值中的 `writePerformed` 始终为 `false`。如果工程中的块在导出后发生变化，预检会拒绝旧基线，避免覆盖其他修改。

当服务端显式开启写入后，预检会返回与该次具体变更绑定的 `applyToken`。`tia_apply_block_change` 只接受完全相同的 PLC、块、基线哈希和候选 XML，并且令牌只能使用一次。导入前会保存原始 XML；导入后先编译 PLC，再重新导出并比较哈希，失败时尝试自动回滚。TIA 在导入和编译之间会将块标记为暂时不一致，此时不能执行导出。当前版本不会自动保存项目，也不会下载到 PLC。

导入后服务会自动编译整个目标 PLC，并返回编译状态、错误数、警告数和分层诊断消息。存在编译错误时，本次修改会被判定失败，原始块将被重新导入并再次编译。也可以在开启写入开关后单独调用 `tia_compile_plc`。

保存项目是独立的第二次授权步骤。仅当 `TIA_ENABLE_SAVE=true` 时，成功的 `tia_apply_block_change` 才返回 `saveToken` 和精确 `projectName`。调用 `tia_save_project` 时还必须提供目标块的 `expectedBlockHash`；服务会在保存前重新导出该块，确认哈希未变化，并确保仍然只有同一个打开项目。保存令牌只能使用一次。

对应 REST 接口：

```text
POST /api/blocks/preview
```

## 配置项

| 环境变量 | 用途 | 默认值 |
| --- | --- | --- |
| `TIA_OPENNESS_DLL` | 指定 `Siemens.Engineering.dll` | 自动扫描 V21–V15 |
| `TIA_MCP_URL` | HTTP 监听地址 | `http://127.0.0.1:5111` |
| `TIA_MCP_TOKEN` | Bearer Token；局域网监听时必须配置 | 未启用 |
| `TIA_WORKER_PATH` | 自定义 Worker 路径 | 输出目录中的 `worker` 子目录 |
| `TIA_WORKER_TIMEOUT_SECONDS` | 单次 Openness 操作超时 | `60` |
| `TIA_ENABLE_WRITE` | 是否开放导入工具；必须明确设为 `true` | `false` |
| `TIA_ENABLE_SAVE` | 是否开放项目保存；要求同时开启写入 | `false` |
| `TIA_WRITE_SECRET` | 生成变更专用一次性令牌，至少 32 个字符 | 未配置 |
| `TIA_BACKUP_DIRECTORY` | 导入前原始 XML 的备份目录 | 输出目录中的 `backups` |

开启写入时必须同时配置 `TIA_MCP_TOKEN`，否则服务会拒绝启动：

```powershell
$env:TIA_ENABLE_WRITE = 'true'
$env:TIA_ENABLE_SAVE = 'true' # 只有确实需要落盘时才设置
$env:TIA_WRITE_SECRET = '请替换为至少32个字符的独立随机密钥'
$env:TIA_MCP_TOKEN = '请替换为足够长的Bearer令牌'
```

建议仅在准备执行已审核变更时临时开启写入。只有确认导入、验签和编译结果后才临时开启保存；完成后关闭服务并移除 `TIA_ENABLE_WRITE` 和 `TIA_ENABLE_SAVE`。

每次应用会在备份目录生成：

- `*.backup.xml`：修改前的原始块
- `*.proposed.xml`：候选 XML
- `*.actual.xml`：编译后重新导出的实际 XML
- `*.journal.json`：`prepared`、`importing`、`compiling`、`verifying-import`、`succeeded` 或回滚阶段日志

V20 已验证的执行顺序为：

```text
导入 → 编译 → 重新导出 → SHA-256 验签
```

导入后、编译前的块处于暂时不一致状态，TIA 会拒绝导出。

## 已知边界

- 每次查询启动独立 Worker，以隔离 Openness 版本和故障；后续可改成长驻 Worker 提升大量查询时的性能。
- 当前设备与块查询使用第一个找到的已打开项目。
- 当前返回原始程序块 XML；后续将增加结构化解析、差异预览和受控导入。
- 写入链路已具备显式开关、哈希校验、预检、一次性导入令牌、自动备份、导入后校验、PLC 编译诊断、失败恢复和独立的一次性保存令牌。服务不会自动下载到 PLC。
- 不要将服务端口暴露到公网。监听局域网地址时，建议 Windows 防火墙只允许宿主机 IP。
