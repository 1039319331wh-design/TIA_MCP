import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const projectRoot = process.cwd();
const threadId = "019ff4ec-4282-7261-812d-0b23a9d1f579";
const outputDir = path.join(projectRoot, "outputs", threadId);
const previewDir = path.join(outputDir, "previews");
const outputPath = path.join(outputDir, "TIA_Hardware_Configuration_Template.xlsx");
await fs.mkdir(previewDir, { recursive: true });

const wb = Workbook.create();
const guide = wb.worksheets.add("填写说明");
const stations = wb.worksheets.add("站点与CPU");
const modules = wb.worksheets.add("硬件模块");
const networks = wb.worksheets.add("网络连接");
const ioMap = wb.worksheets.add("IO映射");
const summary = wb.worksheets.add("校验汇总");
const options = wb.worksheets.add("选项");

const COLORS = {
  navy: "#17365D",
  blue: "#1F4E78",
  teal: "#0F6B78",
  lightBlue: "#D9EAF7",
  required: "#FFF2CC",
  optional: "#E2F0D9",
  computed: "#E7E6E6",
  warning: "#FCE4D6",
  danger: "#F4CCCC",
  ok: "#D9EAD3",
  white: "#FFFFFF",
  text: "#1F2937",
  border: "#C9D3DF",
};

function title(sheet, endCol, text, subtitle) {
  sheet.showGridLines = false;
  sheet.getRange(`A1:${endCol}1`).merge();
  sheet.getRange("A1").values = [[text]];
  sheet.getRange(`A1:${endCol}1`).format = {
    fill: COLORS.navy,
    font: { bold: true, color: COLORS.white, size: 18 },
    verticalAlignment: "center",
    rowHeight: 34,
  };
  sheet.getRange(`A2:${endCol}2`).merge();
  sheet.getRange("A2").values = [[subtitle]];
  sheet.getRange(`A2:${endCol}2`).format = {
    fill: COLORS.lightBlue,
    font: { color: COLORS.text, italic: true, size: 10 },
    wrapText: true,
    rowHeight: 30,
  };
}

function setupDataSheet(sheet, endCol, titleText, subtitle, headers, widths, requiredIndexes, tableName, rows = 60) {
  title(sheet, endCol, titleText, subtitle);
  const headerRange = sheet.getRange(`A4:${endCol}4`);
  headerRange.values = [headers];
  headerRange.format = {
    fill: COLORS.blue,
    font: { bold: true, color: COLORS.white, size: 10 },
    wrapText: true,
    horizontalAlignment: "center",
    verticalAlignment: "center",
    rowHeight: 38,
    borders: { preset: "outside", style: "thin", color: COLORS.border },
  };
  for (let i = 0; i < headers.length; i++) {
    const col = String.fromCharCode(65 + i);
    sheet.getRange(`${col}5:${col}${rows + 4}`).format = {
      fill: requiredIndexes.includes(i) ? COLORS.required : COLORS.optional,
      font: { color: COLORS.text, size: 10 },
      verticalAlignment: "center",
    };
    sheet.getRange(`${col}4:${col}${rows + 4}`).format.columnWidth = widths[i];
  }
  sheet.getRange(`A5:${endCol}${rows + 4}`).format.borders = {
    insideHorizontal: { style: "thin", color: "#E5E7EB" },
  };
  sheet.freezePanes.freezeRows(4);
  sheet.tables.add(`A4:${endCol}${rows + 4}`, true, tableName).style = "TableStyleMedium2";
}

// Fill guide
title(guide, "H", "TIA Portal 硬件组态填写模板", "请只填写黄色/绿色区域；灰色校验列由公式自动计算。填写完成后把此文件交回，我会据此生成或修改项目硬件组态。值为“跳过”的行不会处理。");
guide.getRange("A4:H4").merge();
guide.getRange("A4").values = [["使用流程"]];
guide.getRange("A4:H4").format = { fill: COLORS.teal, font: { bold: true, color: COLORS.white, size: 12 }, rowHeight: 24 };
guide.getRange("A5:H9").values = [
  ["1", "先填写“站点与CPU”", "每个 PLC、远程 I/O 站或其他站点使用唯一的站点ID。", null, null, null, null, null],
  ["2", "再填写“硬件模块”", "模块通过站点ID、机架号、槽位和子槽位定位。订货号尽量填写完整。", null, null, null, null, null],
  ["3", "填写“网络连接”", "定义接口、子网、IP 地址和 PROFINET 设备名称。", null, null, null, null, null],
  ["4", "可选填写“IO映射”", "把通道、逻辑地址和 PLC 变量对应起来，便于后续自动创建标签。", null, null, null, null, null],
  ["5", "查看“校验汇总”", "所有错误数为 0 后保存并交回。不要删除工作表或修改列名。", null, null, null, null, null],
];
guide.getRange("A5:A9").format = { fill: COLORS.blue, font: { bold: true, color: COLORS.white }, horizontalAlignment: "center" };
guide.getRange("B5:H9").format = { wrapText: true, verticalAlignment: "center" };
guide.getRange("A5:H9").format.rowHeight = 32;
guide.getRange("A11:H11").merge();
guide.getRange("A11").values = [["颜色说明"]];
guide.getRange("A11:H11").format = { fill: COLORS.teal, font: { bold: true, color: COLORS.white, size: 12 } };
guide.getRange("A12:B14").values = [["黄色", "必填字段"], ["绿色", "可选字段"], ["灰色", "自动校验字段，请勿修改"]];
guide.getRange("A12").format.fill = COLORS.required;
guide.getRange("A13").format.fill = COLORS.optional;
guide.getRange("A14").format.fill = COLORS.computed;
guide.getRange("A16:H16").merge();
guide.getRange("A16").values = [["填写约定"]];
guide.getRange("A16:H16").format = { fill: COLORS.teal, font: { bold: true, color: COLORS.white, size: 12 } };
guide.getRange("A17:H22").values = [
  ["•", "操作列", "新建、更新、克隆或跳过。更新需要目标对象已存在；克隆需在备注中写明源站点/源模块。", null, null, null, null, null],
  ["•", "订货号", "请以 Siemens Industry Mall 或实物铭牌为准，例如 6ES7…；不要只写简称。", null, null, null, null, null],
  ["•", "槽位", "使用 TIA Portal 中显示的数字；接口子模块请同时填写子槽位。", null, null, null, null, null],
  ["•", "地址", "起始地址和长度均使用十进制字节数；布尔通道可在 IO 映射中写 I0.0 / Q0.0。", null, null, null, null, null],
  ["•", "空白行", "允许保留；系统只处理操作列不为空且不为“跳过”的行。", null, null, null, null, null],
  ["•", "安全硬件", "F-CPU/F-I/O 请在模块类型选择“安全”，并在备注中写明 F 目标和 PROFIsafe 地址。", null, null, null, null, null],
];
guide.getRange("A17:H22").format = { wrapText: true, verticalAlignment: "center", rowHeight: 30 };
guide.getRange("A1:H22").format.borders = { outside: { style: "thin", color: COLORS.border } };
guide.getRange("A1:A22").format.columnWidth = 6;
guide.getRange("B1:B22").format.columnWidth = 24;
guide.getRange("C1:H22").format.columnWidth = 16;
guide.freezePanes.freezeRows(2);

// Stations
const stationHeaders = ["操作*", "站点ID*", "站点名称*", "站点类型*", "TIA版本*", "设备系列*", "CPU/接口模块订货号*", "固件版本", "机架名称", "机架号*", "PROFINET设备名称", "IP地址", "子网掩码", "网关", "PLC名称", "源站点ID(克隆时)", "备注", "校验结果"];
setupDataSheet(stations, "R", "站点与 CPU", "每个站点一行。带 * 的列为必填；远程 I/O 站可把接口模块订货号填入“CPU/接口模块订货号”。", stationHeaders, [12,14,20,14,12,16,24,12,16,10,24,16,16,16,18,18,28,18], [0,1,2,3,4,5,6,9], "StationsTable", 60);
stations.getRange("A5:A64").dataValidation = { rule: { type: "list", formula1: "选项!$A$2:$A$6" } };
stations.getRange("D5:D64").dataValidation = { rule: { type: "list", formula1: "选项!$B$2:$B$7" } };
stations.getRange("E5:E64").dataValidation = { rule: { type: "list", formula1: "选项!$C$2:$C$8" } };
stations.getRange("J5:J64").dataValidation = { rule: { type: "whole", operator: "between", formula1: 0, formula2: 99 } };
stations.getRange("R5").formulas = [["=IF(A5=\"\",\"\",IF(A5=\"跳过\",\"跳过\",IF(OR(B5=\"\",C5=\"\",D5=\"\",E5=\"\",F5=\"\",G5=\"\",LEN(J5)=0),\"缺少必填项\",IF(COUNTIF($B$5:$B$64,B5)>1,\"站点ID重复\",IF(AND(A5=\"克隆\",P5=\"\"),\"缺少源站点ID\",\"通过\")))))"]];
stations.getRange("R5:R64").fillDown();
stations.getRange("R5:R64").format.fill = COLORS.computed;

// Modules
const moduleHeaders = ["操作*", "站点ID*", "机架号*", "槽位*", "子槽位", "模块名称*", "模块类型*", "订货号*", "固件版本", "父模块名称", "输入起始地址", "输入长度(Byte)", "输出起始地址", "输出长度(Byte)", "过程映像分区", "源模块名称(克隆时)", "备注", "校验结果"];
setupDataSheet(modules, "R", "硬件模块", "每个硬件模块一行。相同站点、机架、槽位、子槽位的组合必须唯一。电源模块也可列出，但无 I/O 地址时留空。", moduleHeaders, [12,14,10,10,10,20,16,24,12,18,14,14,14,14,16,22,28,18], [0,1,2,3,5,6,7], "ModulesTable", 120);
modules.getRange("A5:A124").dataValidation = { rule: { type: "list", formula1: "选项!$A$2:$A$6" } };
modules.getRange("G5:G124").dataValidation = { rule: { type: "list", formula1: "选项!$D$2:$D$14" } };
for (const col of ["C", "D", "E", "K", "L", "M", "N"]) modules.getRange(`${col}5:${col}124`).dataValidation = { rule: { type: "whole", operator: "between", formula1: 0, formula2: 65535 } };
modules.getRange("R5").formulas = [["=IF(A5=\"\",\"\",IF(A5=\"跳过\",\"跳过\",IF(OR(B5=\"\",LEN(C5)=0,LEN(D5)=0,F5=\"\",G5=\"\",H5=\"\"),\"缺少必填项\",IF(COUNTIF('站点与CPU'!$B$5:$B$64,B5)=0,\"站点ID不存在\",IF(COUNTIFS($B$5:$B$124,B5,$C$5:$C$124,C5,$D$5:$D$124,D5,$E$5:$E$124,E5)>1,\"槽位重复\",IF(AND(A5=\"克隆\",P5=\"\"),\"缺少源模块名称\",\"通过\"))))))"]];
modules.getRange("R5:R124").fillDown();
modules.getRange("R5:R124").format.fill = COLORS.computed;

// Networks
const networkHeaders = ["操作*", "连接ID*", "站点ID*", "接口名称*", "网络类型*", "子网名称*", "PROFINET设备名称", "IP地址", "子网掩码", "网关", "IO系统名称", "更新时间(ms)", "同步角色", "备注", "校验结果"];
setupDataSheet(networks, "O", "网络连接", "每个接口或网络连接一行。IP 地址与 PROFINET 设备名称应在项目内唯一。", networkHeaders, [12,16,14,20,16,18,24,16,16,16,20,14,14,28,18], [0,1,2,3,4,5], "NetworksTable", 80);
networks.getRange("A5:A84").dataValidation = { rule: { type: "list", formula1: "选项!$A$2:$A$6" } };
networks.getRange("E5:E84").dataValidation = { rule: { type: "list", formula1: "选项!$E$2:$E$6" } };
networks.getRange("M5:M84").dataValidation = { rule: { type: "list", formula1: "选项!$F$2:$F$5" } };
networks.getRange("L5:L84").dataValidation = { rule: { type: "whole", operator: "between", formula1: 1, formula2: 100000 } };
networks.getRange("O5").formulas = [["=IF(A5=\"\",\"\",IF(A5=\"跳过\",\"跳过\",IF(OR(B5=\"\",C5=\"\",D5=\"\",E5=\"\",F5=\"\"),\"缺少必填项\",IF(COUNTIF('站点与CPU'!$B$5:$B$64,C5)=0,\"站点ID不存在\",IF(AND(H5<>\"\",COUNTIF($H$5:$H$84,H5)>1),\"IP地址重复\",IF(AND(G5<>\"\",COUNTIF($G$5:$G$84,G5)>1),\"设备名称重复\",\"通过\"))))))"]];
networks.getRange("O5:O84").fillDown();
networks.getRange("O5:O84").format.fill = COLORS.computed;

// IO mapping
const ioHeaders = ["操作*", "站点ID*", "模块名称*", "槽位*", "通道*", "方向*", "符号名称*", "逻辑地址*", "数据类型*", "信号描述", "工程单位", "常态", "滤波时间(ms)", "变量表", "备注", "校验结果"];
setupDataSheet(ioMap, "P", "I/O 映射", "用于生成 PLC 变量表及地址映射。逻辑地址示例：I0.0、IW64、Q4.2、QW100。", ioHeaders, [12,14,20,10,10,12,24,16,14,30,12,12,14,20,28,18], [0,1,2,3,4,5,6,7,8], "IoMapTable", 160);
ioMap.getRange("A5:A164").dataValidation = { rule: { type: "list", formula1: "选项!$A$2:$A$6" } };
ioMap.getRange("F5:F164").dataValidation = { rule: { type: "list", formula1: "选项!$G$2:$G$5" } };
ioMap.getRange("I5:I164").dataValidation = { rule: { type: "list", formula1: "选项!$H$2:$H$14" } };
ioMap.getRange("D5:E164").dataValidation = { rule: { type: "whole", operator: "between", formula1: 0, formula2: 65535 } };
ioMap.getRange("P5").formulas = [["=IF(A5=\"\",\"\",IF(A5=\"跳过\",\"跳过\",IF(OR(B5=\"\",C5=\"\",LEN(D5)=0,LEN(E5)=0,F5=\"\",G5=\"\",H5=\"\",I5=\"\"),\"缺少必填项\",IF(COUNTIF('站点与CPU'!$B$5:$B$64,B5)=0,\"站点ID不存在\",IF(COUNTIF('硬件模块'!$F$5:$F$124,C5)=0,\"模块名称不存在\",IF(COUNTIF($G$5:$G$164,G5)>1,\"符号名称重复\",IF(COUNTIF($H$5:$H$164,H5)>1,\"逻辑地址重复\",\"通过\")))))))"]];
ioMap.getRange("P5:P164").fillDown();
ioMap.getRange("P5:P164").format.fill = COLORS.computed;

// Conditional formats for all validation columns.
for (const [sheet, range] of [[stations,"R5:R64"],[modules,"R5:R124"],[networks,"O5:O84"],[ioMap,"P5:P164"]]) {
  const r = sheet.getRange(range);
  r.conditionalFormats.add("containsText", { text: "通过", format: { fill: COLORS.ok, font: { color: "#27632A", bold: true } } });
  r.conditionalFormats.add("containsText", { text: "缺少", format: { fill: COLORS.danger, font: { color: "#9C0006", bold: true } } });
  r.conditionalFormats.add("containsText", { text: "重复", format: { fill: COLORS.warning, font: { color: "#9C6500", bold: true } } });
  r.conditionalFormats.add("containsText", { text: "不存在", format: { fill: COLORS.danger, font: { color: "#9C0006", bold: true } } });
}

// Summary dashboard
title(summary, "H", "模板校验汇总", "保存前请确保四个工作表的错误数均为 0。计数会随填写内容自动更新。");
summary.getRange("A4:D4").values = [["工作表", "有效数据行", "通过", "错误"]];
summary.getRange("A4:D4").format = { fill: COLORS.blue, font: { bold: true, color: COLORS.white }, horizontalAlignment: "center" };
summary.getRange("A5:A8").values = [["站点与CPU"],["硬件模块"],["网络连接"],["IO映射"]];
summary.getRange("B5:D8").formulas = [
  ["=COUNTIF('站点与CPU'!$A$5:$A$64,\"<>\")-COUNTIF('站点与CPU'!$A$5:$A$64,\"跳过\")", "=COUNTIF('站点与CPU'!$R$5:$R$64,\"通过\")", "=B5-C5"],
  ["=COUNTIF('硬件模块'!$A$5:$A$124,\"<>\")-COUNTIF('硬件模块'!$A$5:$A$124,\"跳过\")", "=COUNTIF('硬件模块'!$R$5:$R$124,\"通过\")", "=B6-C6"],
  ["=COUNTIF('网络连接'!$A$5:$A$84,\"<>\")-COUNTIF('网络连接'!$A$5:$A$84,\"跳过\")", "=COUNTIF('网络连接'!$O$5:$O$84,\"通过\")", "=B7-C7"],
  ["=COUNTIF('IO映射'!$A$5:$A$164,\"<>\")-COUNTIF('IO映射'!$A$5:$A$164,\"跳过\")", "=COUNTIF('IO映射'!$P$5:$P$164,\"通过\")", "=B8-C8"],
];
summary.getRange("A10:C10").values = [["总数据行", "总错误", "提交状态"]];
summary.getRange("A10:C10").format = { fill: COLORS.teal, font: { bold: true, color: COLORS.white }, horizontalAlignment: "center" };
summary.getRange("A11:C11").formulas = [["=SUM(B5:B8)", "=SUM(D5:D8)", "=IF(B11=0,\"尚未填写\",IF(B11=0,\"可提交\",IF(B11=0,\"可提交\",\"请先修正错误\")))"]];
// Correct status formula explicitly after seeding the KPI row.
summary.getRange("C11").formulas = [["=IF(A11=0,\"尚未填写\",IF(B11=0,\"可提交\",\"请先修正错误\"))"]];
summary.getRange("A11:C11").format = { fill: COLORS.lightBlue, font: { bold: true, size: 14 }, horizontalAlignment: "center", rowHeight: 32 };
summary.getRange("C11").conditionalFormats.add("containsText", { text: "可提交", format: { fill: COLORS.ok, font: { color: "#27632A", bold: true } } });
summary.getRange("C11").conditionalFormats.add("containsText", { text: "修正", format: { fill: COLORS.danger, font: { color: "#9C0006", bold: true } } });
summary.getRange("A4:D8").format.borders = { preset: "all", style: "thin", color: COLORS.border };
summary.getRange("A4:A11").format.columnWidth = 22;
summary.getRange("B4:D11").format.columnWidth = 16;
summary.freezePanes.freezeRows(2);

// Option lists
options.showGridLines = false;
options.getRange("A1:H1").values = [["操作", "站点类型", "TIA版本", "模块类型", "网络类型", "同步角色", "方向", "数据类型"]];
options.getRange("A1:H1").format = { fill: COLORS.navy, font: { bold: true, color: COLORS.white }, horizontalAlignment: "center" };
const optionColumns = [
  ["新建", "更新", "克隆", "删除", "跳过"],
  ["PLC", "远程IO", "HMI", "驱动", "网络设备", "其他"],
  ["V15.1", "V16", "V17", "V18", "V19", "V20", "V21"],
  ["CPU", "电源", "数字量输入", "数字量输出", "模拟量输入", "模拟量输出", "混合IO", "通信", "安全", "工艺", "接口模块", "信号板", "其他"],
  ["PROFINET", "PROFIBUS", "工业以太网", "MPI", "其他"],
  ["IO控制器", "IO设备", "IRT同步主站", "IRT同步从站"],
  ["输入", "输出", "双向", "诊断"],
  ["Bool", "Byte", "Word", "DWord", "Int", "DInt", "UInt", "UDInt", "Real", "LReal", "Time", "Date", "其他"],
];
for (let c = 0; c < optionColumns.length; c++) {
  const values = optionColumns[c].map(v => [v]);
  options.getRangeByIndexes(1, c, values.length, 1).values = values;
}
options.getRange("A1:H15").format.columnWidth = 18;
options.getRange("A2:H15").format.fill = COLORS.optional;
options.freezePanes.freezeRows(1);

// Add a small sample, intentionally valid, to make the expected format concrete.
stations.getRange("A5:Q5").values = [["新建", "PLC_01", "主控制器", "PLC", "V19", "S7-1500", "6ES7 516-3AN02-0AB0", "V3.1", "Rack_0", 0, "plc-01", "192.168.0.1", "255.255.255.0", "", "PLC_01", "", "示例行，可直接覆盖"]];
modules.getRange("A5:Q5").values = [["新建", "PLC_01", 0, 1, 0, "CPU_1516", "CPU", "6ES7 516-3AN02-0AB0", "V3.1", "", "", "", "", "", "", "", "示例行，可直接覆盖"]];
networks.getRange("A5:N5").values = [["新建", "NET_01", "PLC_01", "X1", "PROFINET", "PN_IO", "plc-01", "192.168.0.1", "255.255.255.0", "", "PNIO_SYSTEM", 4, "IO控制器", "示例行，可直接覆盖"]];
ioMap.getRange("A5:O5").values = [["新建", "PLC_01", "CPU_1516", 1, 0, "输入", "Start_Button", "I0.0", "Bool", "启动按钮", "", "常开", 3, "Hardware_IO", "示例行，可直接覆盖"]];

const inspect = await wb.inspect({ kind: "workbook,sheet,table,formula", maxChars: 9000, tableMaxRows: 6, tableMaxCols: 8, options: { maxResults: 80 } });
await fs.writeFile(path.join(outputDir, "inspection.ndjson"), inspect.ndjson ?? String(inspect), "utf8");

const renderTargets = [
  ["填写说明", "A1:H22"], ["站点与CPU", "A1:R12"], ["硬件模块", "A1:R12"],
  ["网络连接", "A1:O12"], ["IO映射", "A1:P12"], ["校验汇总", "A1:H14"], ["选项", "A1:H15"],
];
for (const [sheetName, range] of renderTargets) {
  const preview = await wb.render({ sheetName, range, scale: 1, format: "png" });
  await fs.writeFile(path.join(previewDir, `${sheetName}.png`), new Uint8Array(await preview.arrayBuffer()));
}

const xlsx = await SpreadsheetFile.exportXlsx(wb);
await xlsx.save(outputPath);
console.log(JSON.stringify({ outputPath, previewDir }, null, 2));
