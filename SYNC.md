# 多客户端同步

本项目使用 Git 同步源代码、脚本和文档。密钥、TIA 安装路径、构建输出、日志与备份只保留在各自客户端，不进入仓库。

## 首台客户端

1. 在 GitHub、GitLab、Gitee 或公司 Git 服务创建一个空的私有仓库。
2. 在本目录执行：

```powershell
git remote add origin <远程仓库地址>
git branch -M main
git push -u origin main
```

## 其他客户端

```powershell
git clone <远程仓库地址>
cd TIA_MCP
.\scripts\Initialize-Client.ps1
```

然后按脚本输出，将 `tia-openness` 配置合并进该客户端的 `%USERPROFILE%\.codex\config.toml`。重启或新建 Codex 任务，使 MCP 配置重新加载。

每台 Windows 客户端仍需单独安装 .NET 8 SDK、TIA Portal/Openness，并把当前用户加入 `Siemens TIA Openness` 用户组。API Key、Bearer Token 和 `TIA_WRITE_SECRET` 也应分别安全配置；不要通过 Git 复制。

## 日常同步

开始工作前执行 `git pull --rebase`，完成后提交并推送。不要在两台客户端同时修改同一文件；如确需并行修改，使用独立分支。
