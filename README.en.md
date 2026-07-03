# AiProxy — Local AI Forwarding Proxy

[中文](README.md)

A lightweight local AI API forwarding service with single-port + URL path prefix routing, unified key management, full logging, and visual admin panel.

---

## Features

| Capability | Description |
|-----------|-------------|
| **URL Prefix Routing** | Routes by first path segment `/<prefix>/...` |
| **OpenAI/Anthropic Compatible** | Format conversion between OpenAI and Anthropic, low-latency streaming |
| **Unified Key Management** | Clients cannot access real upstream keys |
| **Full Logging** | SQLite-persisted request/response, token usage |
| **Web Admin Panel** | Service overview, log search, config, request replay |
| **Hot Config Reload** | Admin panel edits take effect immediately, no restart needed |
| **Cross-platform Single File** | Windows / macOS / Linux |

---

## Quick Start

### Configuration

Edit `appsettings.json`:

```json
{
  "Proxy": {
    "GlobalApiKey": "proxy-local-123456",
    "ListenAddress": "localhost",
    "ListenPort": 8000
  }
}
```

| Key | Description | Runtime Editable |
|-----|-------------|:---:|
| `Proxy.GlobalApiKey` | Global proxy access key, empty disables auth | ✓ |
| `Proxy.ListenAddress` | Listen address (localhost / 0.0.0.0) | ✗ |
| `Proxy.ListenPort` | Listen port | ✗ |

### Run

```bash
cd AiProxy
dotnet run
```

Open `http://localhost:8000/` for the admin panel.
![Service Overview](docs/img/image.png)

### Configure Downstream AI Services
![Config Management](docs/img/image-1.png)
![Edit Service](docs/img/image-2.png)

### Test Request
![Test Service](docs/img/image-3.png)

### Usage Statistics
![Usage Statistics](docs/img/image-4.png)

### Logs
![Log Query](docs/img/image-5.png)
![Log Detail - Success](docs/img/image-6.png)
![Log Detail - Failure](docs/img/image-7.png)
