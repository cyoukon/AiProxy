# AiProxy 发布与部署指南

本文件说明 `AiProxy` 单文件自包含可执行程序的发布方式、产物路径、运行方法以及单端口 URL 路由行为。覆盖 Windows / Linux / macOS 三个平台。

---

## 一、发布配置说明

发布参数集中在 `AiProxy.csproj` 的 `<PropertyGroup>` 中：

| 属性 | 值 | 作用 |
|------|-----|------|
| `TargetFramework` | `net10.0` | 目标框架 |
| `AssemblyName` | `AiProxy` | 输出程序集名（即 exe 名） |
| `PublishSingleFile` | `true` | 打包为单文件 |
| `SelfContained` | `true` | 自包含 .NET 运行时，目标机器无需安装 .NET |
| `IncludeNativeLibrariesForSelfExtract` | `true` | 将 SQLite 等原生库（`e_sqlite3` 等）一并打包进单文件，避免运行时缺 native 依赖 |
| `EnableCompressionInSingleFile` | `true` | 压缩单文件内程序集，缩小分发体积（首次启动会解压到临时目录，有约 1-2 秒解压开销） |
| `InvariantGlobalization` | `true` | 使用全球化不变模式，减小体积、避免 locale 依赖 |

> `RuntimeIdentifier` **不在 csproj 中硬编码**，发布时通过 `dotnet publish -r <RID>` 传入，以支持同一份源码构建多平台产物。

管理面板 HTML 通过 `<EmbeddedResource Include="Admin\wwwroot\index.html" />` 嵌入程序集，随单文件一起打包，无需额外 wwwroot 目录。

---

## 二、各平台发布命令与产物路径

工作目录：项目根目录

### 2.1 自包含版本（Self-contained，包含 .NET 运行时）

#### Windows (win-x64)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

- 产物路径：`publish\win-x64\`
- 可执行文件：`AiProxy.exe`
- 同目录附带：`appsettings.json`（用户可编辑）、`web.config`（IIS 部署可选）

#### Linux (linux-x64)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r linux-x64 --self-contained true -o .\publish\linux-x64
```

- 产物路径：`publish\linux-x64\`
- 可执行文件：`AiProxy`（无扩展名，ELF 原生二进制）
- 同目录附带：`appsettings.json`
- 部署到 Linux 后需赋予执行权限：`chmod +x AiProxy`

#### macOS (osx-arm64, Apple Silicon)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r osx-arm64 --self-contained true -o .\publish\osx-arm64
```

- 产物路径：`publish\osx-arm64\`
- 可执行文件：`AiProxy`（无扩展名，Mach-O 原生二进制）
- 同目录附带：`appsettings.json`
- 部署到 macOS 后需赋予执行权限：`chmod +x AiProxy`
- 如需 Intel 芯片版本，使用 `-r osx-x64 -o .\publish\osx-x64` 代替

> macOS 上首次运行可能被 Gatekeeper 拦截，可在「系统设置 → 隐私与安全性」中允许打开，或临时用 `xattr -d com.apple.quarantine AiProxy` 去除隔离属性。

### 2.2 依赖框架版本（Framework-dependent，需目标机器安装 .NET 10 运行时）

依赖框架版本不包含 .NET 运行时，体积显著更小，但要求目标机器已安装 .NET 10 运行时。

#### Windows (win-x64)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64-fdd
```

- 产物路径：`publish\win-x64-fdd\`
- 可执行文件：`AiProxy.exe`
- 同目录附带：`appsettings.json`
- 前置条件：目标机器需安装 [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)

#### Linux (linux-x64)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r linux-x64 --self-contained false -o .\publish\linux-x64-fdd
```

- 产物路径：`publish\linux-x64-fdd\`
- 可执行文件：`AiProxy`
- 同目录附带：`appsettings.json`
- 部署后需赋予执行权限：`chmod +x AiProxy`
- 前置条件：目标机器需安装 .NET 10 运行时

#### macOS (osx-arm64, Apple Silicon)

```powershell
dotnet publish .\AiProxy\AiProxy.csproj -c Release -r osx-arm64 --self-contained false -o .\publish\osx-arm64-fdd
```

- 产物路径：`publish\osx-arm64-fdd\`
- 可执行文件：`AiProxy`
- 同目录附带：`appsettings.json`
- 部署后需赋予执行权限：`chmod +x AiProxy`
- 前置条件：目标机器需安装 .NET 10 运行时
- 如需 Intel 芯片版本，使用 `-r osx-x64 -o .\publish\osx-x64-fdd` 代替

> **如何选择？** 自包含版本适合目标机器未安装 .NET 或无法安装的场景（如离线部署、容器最小化镜像）；依赖框架版本适合目标机器已有 .NET 运行时、或希望减小分发体积的场景。

---

## 三、运行方法

### 3.1 默认运行（使用同目录 appsettings.json）

进入对应平台的 `publish` 目录后：

```bash
# Windows
.\AiProxy.exe

# Linux / macOS
./AiProxy
```

服务将按 `appsettings.json` 中的 `Proxy.ListenAddress` 与 `Proxy.ListenPort` 监听单一端口，业务请求与管理请求通过 URL 前缀区分。

### 3.2 命令行指定配置文件路径

支持 `--config <path>` 或 `-c <path>` 指定配置文件，适配多环境切换：

```bash
# Windows
.\AiProxy.exe --config D:\config\aiproxy-prod.json
.\AiProxy.exe -c=.\aiproxy-prod.json

# Linux / macOS
./AiProxy --config /etc/aiproxy/config.json
./AiProxy -c ./config.json
```

指定路径的配置文件会以 `reloadOnChange: true` 加载，文件变更后通过 `IOptionsMonitor<AppOptions>` 在新请求中热生效，无需重启。`Proxy.ListenAddress`/`Proxy.ListenPort`/`Proxy.LogDbPath` 启动时固定，修改需重启进程；`AiServices` 与 `GlobalApiKey` 也可经管理面板运行时编辑后即时生效。

---

## 四、产物清理

发布产物体积较大（自包含版本每个平台约 50 MB），如需清理可删除项目根目录下的 `publish\` 文件夹：

```
publish\
```

该目录已配置在 `.gitignore` 中，不会提交到 Git 仓库。发布后请按需保留或清理。

---

## 五、GitHub Actions 自动发布

项目配置了 `.github/workflows/release.yml` 工作流，推送 `v*` 格式的 Git 标签时自动触发三平台构建并发布到 GitHub Releases。

### 触发方式

```bash
git tag v1.0.0
git push origin v1.0.0
```