# AiProxy — 本地 AI 转发代理服务

[English](README.en.md)

轻量级本地 AI 接口转发服务，单端口 + URL 路径前缀路由，统一密钥托管、全量日志与可视化管理面板。

---

## 核心特性

| 能力 | 说明 |
|------|------|
| **URL 前缀路由** | 按路径首段匹配下游服务 `/<prefix>/...` |
| **OpenAI/Anthropic 兼容** | 支持两种格式互转，流式响应低延迟转发 |
| **请求模型映射** | 按正则规则将请求体 `model` 字段替换后再转发下游 |
| **密钥统一托管** | 客户端无法获取真实下游密钥 |
| **全量日志** | SQLite 持久化请求/响应、Token 用量 |
| **Web 管理面板** | 服务概览、日志查询、配置、请求重放 |
| **页签持久化** | 刷新后自动恢复上次激活的管理面板页签 |
| **日志悬浮复制** | 请求/响应体面板悬浮显示复制按钮，一键复制原文 |
| **请求/响应区分** | 蓝色 → / 绿色 ← 色条与图标，一眼区分请求与响应 |
| **配置热更新** | 管理面板编辑后即时生效，无需重启 |
| **跨平台单文件** | Windows / macOS / Linux 通用 |

---

## 快速开始

### 运行配置

编辑 `appsettings.json`：

```json
{
  "Proxy": {
    "GlobalApiKey": "proxy-local-123456",
    "ListenAddress": "localhost",
    "ListenPort": 8000
  }
}
```

| 配置项 | 说明 | 运行时可改 |
|--------|------|:------:|
| `Proxy.GlobalApiKey` | 全局代理访问密钥，空则关闭鉴权 | ✓ |
| `Proxy.ListenAddress` | 监听地址（localhost / 0.0.0.0） | ✗ |
| `Proxy.ListenPort` | 监听端口 | ✗ |

### 启动服务

```bash
cd AiProxy
dotnet run
```

启动后访问 `http://localhost:8000/` 打开管理面板。
![服务概览](docs/img/image.png)

### 设置下游AI服务
![配置管理](docs/img/image-1.png)
![编辑服务](docs/img/image-2.png)

### 测试调用
![测试服务](docs/img/image-3.png)

### 用量统计
![用量统计](docs/img/image-4.png)

### 日志
![日志查询](docs/img/image-5.png)
![日志详情-成功](docs/img/image-6.png)
![日志详情-失败](docs/img/image-7.png)

---

## 请求模型映射

每个下游服务可配置一组有序的模型映射规则，转发时在**格式转换之后、发送下游之前**，按规则顺序对请求体中的 `model` 字段做匹配替换。适用于将客户端请求的模型名重定向到下游实际可用的模型。

### 配置示例

在 `appsettings.json` 的 `AiServices` 数组元素中添加 `ModelMappings`：

```json
{
  "Name": "openai-proxy",
  "PathPrefix": "openai",
  "BaseUrl": "https://api.openai.com",
  "ServiceFormat": "OpenAI",
  "ModelMappings": [
    {
      "Pattern": "^gpt-4$",
      "Replacement": "gpt-4-turbo",
      "Enabled": true
    },
    {
      "Pattern": "^claude-3-opus.*",
      "Replacement": "claude-3-5-sonnet",
      "Enabled": true
    }
  ]
}
```

| 字段 | 说明 |
|------|------|
| `Pattern` | .NET 正则表达式，对请求体 `model` 字段做 `IsMatch` 测试 |
| `Replacement` | 替换值，支持 `$1`、`${name}` 等反向引用 |
| `Enabled` | 启用开关，`false` 时跳过本条 |

### 匹配规则

- 按列表顺序遍历**启用**的映射，**首次命中即替换并停止**遍历。
- 命中时用 `Regex.Replace(model, Pattern, Replacement)` 生成新 model 并写回请求体。
- 全部未命中或映射列表为空时，`model` 保持原值原样转发。
- 先执行格式转换再应用映射，因此跨格式转换（如 OpenAI 客户端 → Anthropic 下游）映射同样生效。

### 正则与安全

- 采用 .NET Regex 语法，编译时使用 `RegexOptions.CultureInvariant` 保证跨区域行为一致。
- 每次匹配设置 **1 秒超时**，防止恶意或低效正则引发 ReDoS（正则拒绝服务）。
- `Pattern` 为空字符串视为合法但不参与匹配；非法正则在转发时被跳过（fail-open，不阻塞请求）。

### 管理面板操作

在「配置管理 → 编辑服务」Modal 中可见**模型映射**区块：

- **新增 / 删除**：添加按钮新增一条空映射，✕ 按钮删除单条。
- **排序**：↑ / ↓ 按钮调整顺序，顺序即配置顺序，持久化到 `appsettings.json`。
- **启用开关**：每条映射前的复选框控制是否参与匹配。
- **正则匹配测试**：在测试输入框输入模型名，即时显示首条命中的映射（高亮）与替换结果；无匹配时提示「无匹配」。

### 日志影响

日志的 `Model` 字段记录的是**实际转发给下游的模型名**（格式转换 + 模型映射后的最终请求体中的 `model`），而非客户端原始请求的模型。日志列表与详情均显示该实际模型。
