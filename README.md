# AiProxy — 本地 AI 转发代理服务

[English](README.en.md)

轻量级本地 AI 接口转发服务，单端口 + URL 路径前缀路由，统一密钥托管、全量日志与可视化管理面板。

---

## 核心特性

| 能力 | 说明 |
|------|------|
| **URL 前缀路由** | 按路径首段匹配下游服务 `/<prefix>/...` |
| **OpenAI/Anthropic 兼容** | 支持两种格式互转，流式响应低延迟转发 |
| **请求模型映射** | 按通配符规则将请求体 `model` 字段替换后再转发下游 |
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