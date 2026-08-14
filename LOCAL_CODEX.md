# Codex 本机直连 TIA Portal

本项目不要求使用 Web 控制台。Codex 桌面应用通过本机 MCP 地址直接调用 TIA Portal Openness：

```text
Codex Desktop -> http://127.0.0.1:5111/mcp -> TiaMcpBridge -> TiaOpennessWorker -> TIA Portal V16-V21
```

## 日常启动

首次克隆到一台新客户端后，先执行：

```powershell
.\scripts\Initialize-Client.ps1
```

跨客户端同步的完整流程见 `SYNC.md`。密钥、TIA 安装路径和运行时文件不会通过 Git 同步。

先打开 TIA Portal V16–V21 和目标项目，然后在 PowerShell 中运行：

```powershell
.\scripts\Start-TiaMcp.ps1
```

默认 `-TiaVersion Auto` 会从 V21 到 V16 探测已安装的 Openness API，并优先选择存在运行中 TIA 进程的版本。也可以明确指定：

```powershell
.\scripts\Start-TiaMcp.ps1 -TiaVersion V16
.\scripts\Start-TiaMcp.ps1 -TiaVersion V17
.\scripts\Start-TiaMcp.ps1 -TiaVersion V18
.\scripts\Start-TiaMcp.ps1 -TiaVersion V19
.\scripts\Start-TiaMcp.ps1 -TiaVersion V20
.\scripts\Start-TiaMcp.ps1 -TiaVersion V21
```

单个桥接实例只加载一个主版本的 `Siemens.Engineering.dll`。如需同时连接两个不同版本，可分别指定版本和端口启动两个实例，并在 Codex 中注册两个 MCP 地址。

脚本会在隐藏窗口中启动桥接服务；如果服务已经运行，则只报告当前状态。日志和进程号保存在 `.runtime` 目录。

验证 MCP 与当前博途项目：

```powershell
.\scripts\Test-TiaMcp.ps1
```

还可以对指定块执行“导出后原样预检”的安全回归测试。该测试验证中文名称、XML 传输和哈希基线，不会导入或保存：

```powershell
.\scripts\Test-TiaMcp.ps1 -Plc '水系' -BlockName 'Main' -Group '程序块'
```

停止由启动脚本管理的桥接进程：

```powershell
.\scripts\Stop-TiaMcp.ps1
```

## Codex 配置

全局配置文件 `%USERPROFILE%\.codex\config.toml` 应包含：

```toml
[mcp_servers.tia-openness]
url = "http://127.0.0.1:5111/mcp"
```

修改配置后新建一个 Codex 任务，使新任务重新加载 MCP 工具。可使用下面的第一条提示进行只读确认：

```text
调用 tia-openness 检查博途连接，列出当前项目、PLC 和程序块，不执行写入。
```

需要准备小范围修改时，优先让 Codex 调用 `tia_prepare_text_replacement`。该工具要求精确的旧文本、新文本和预期匹配次数，自动完成导出、候选生成、哈希校验和差异预检，但不会导入项目。只有审核其 `preview.diff` 后，才能把候选交给写入工具。

默认情况下候选 XML 保存在桥接服务内存中 30 分钟，工具只返回短 `changeId`，避免大段 XML 占用 Codex 上下文。受控写入窗口开启后，审核者明确确认，再调用 `tia_apply_prepared_change` 并传入 `confirmation=APPLY_PREPARED_CHANGE`。变更编号只能消费一次；桥接服务重启后内存候选会失效。

`tia_list_change_history` 用于读取最近的变更日志，包括操作编号、目标 PLC 和块、阶段、前后哈希及错误。每次真实写入后应先确认最终阶段为 `succeeded`；若出现 `rolling-back`、`rolled-back` 或 `rollback-failed`，应停止后续写入并检查备份。

修改前先调用 `tia_get_block_overview` 获取块语言、网络数量和可读文本；需要定位注释或字符串时调用 `tia_search_block_text`。只有定位结果唯一且基线哈希未变化时，再调用候选生成工具。这样可以避免为了查找一段内容把整份 TIA XML 送入对话上下文。

分析 PLC 符号和 I/O 时，先调用 `tia_list_tag_tables`，再用 `tia_search_tag_table` 搜索变量名、地址、类型或注释；仅在确实需要完整结构时调用 `tia_export_tag_table`。变量表能力当前严格只读。

首次接触项目中的某个 PLC 时，先调用 `tia_get_plc_overview` 建立块类型、语言、分组和变量表地图。需要批量理解变量时使用 `tia_get_tag_table_overview` 分页读取，避免无目标地导出大量 XML。

分析 FB/FC/DB 契约时调用 `tia_get_block_interface`，优先根据输入、输出、静态变量和注释理解职责。查找一个符号在哪些块中出现时调用 `tia_search_plc_blocks`，并尽量设置块类型、分组和最大扫描块数，避免对大型工程进行无界导出。

需要理解程序调用层级时调用 `tia_get_block_dependencies`，先从根块沿调用边向下分析。硬件拓扑使用 `tia_get_hardware_overview`，但 I/O 地址必须再通过变量表工具核对，不能从模块名称或 `TypeIdentifier` 推断地址。

准备开始一组分析或变更前，调用 `tia_create_project_snapshot` 建立临时基线；完成后调用 `tia_compare_project_snapshot`，确认实际变化范围与预期一致。快照在内存中保存 30 分钟，桥接重启后失效，不能替代 TIA 项目备份或 Git 提交。

分析参数、状态、配方或实例数据时，先用 `tia_list_data_blocks` 缩小目标，再调用 `tia_get_data_block_overview` 分页读取扁平化成员路径、类型、初始值和注释。遇到嵌套 UDT 或数组时保留完整路径和原始属性，不根据名称猜测业务含义。

## 写入模式

默认启动是只读的。只有准备执行已经审核的变更时，才在当前 PowerShell 进程中设置密钥并显式开启写入：

```powershell
$env:TIA_MCP_TOKEN = '<随机 Bearer Token>'
$env:TIA_WRITE_SECRET = '<至少 32 字符的独立随机密钥>'
.\scripts\Start-TiaMcp.ps1 -EnableWrite
```

项目保存需要额外传入 `-EnableSave`。修改链路仍要求导出基线、预检、一次性令牌、备份、导入、编译和哈希校验。服务不会自动下载到 PLC。
