# 模型映射：正则改通配符 + 移除匹配测试 UI

## Context

当前模型映射使用 .NET 正则表达式匹配 model 字段，支持反向引用替换。正则表达式对用户来说过于复杂，现改为简单的通配符匹配（`*` 匹配任意字符，`?` 匹配单个字符）。同时移除管理面板中的"匹配测试"功能。

## 改动清单

### 1. 新建 `AiProxy/Util/WildcardMatcher.cs`

- 静态工具类，`WildcardToRegex(string pattern)` 将通配符转为正则（转义特殊字符，`*` → `.*`，`?` → `.`，加 `^...$` 锚定全串匹配）
- `IsMatch(string input, string pattern)` 封装正则匹配，1 秒超时防 ReDoS

### 2. 修改 `AiProxy/Config/AiServiceOptions.cs`

- `ModelMappingOptions` 类：更新 `Pattern` / `Replacement` 属性文档注释
  - Pattern：正则 → 通配符（`*` 匹配任意字符，`?` 单字符，其余原义），移除 Regex.IsMatch / ReDoS 引用
  - Replacement：移除 `$1`/`${name}` 反向引用说明，改为"匹配时直接替换为该值"

### 3. 修改 `AiProxy/Forwarding/ForwardingEndpoint.cs`

- `ApplyModelMappings` 方法：
  - `new Regex(pattern).IsMatch(model)` → `WildcardMatcher.IsMatch(model, mapping.Pattern)`
  - `regex.Replace(model, mapping.Replacement)` → 直接用 `mapping.Replacement`
  - 移除 `newModel == model` 的反向引用相同判断，改为 `mapping.Replacement == model`
  - 移除 `try/catch` 中 Regex 编译异常处理（通配符始终合法）
  - 更新方法文档注释
- 移除 `using System.Text.RegularExpressions;`（文件不再直接使用 Regex）

### 4. 修改 `AiProxy/Services/ConfigService.cs`

- `ValidateModelMappings` 方法体清空（通配符无需校验语法），保留方法签名与调用点
- 更新方法文档注释

### 5. 修改 `AiProxy/Admin/wwwroot/index.html`

- 删除 `<div class="mappings-test">` 区块（匹配测试输入框 + 结果显示）

### 6. 修改 `AiProxy/Admin/wwwroot/services.js`

- 删除 `runMappingTest()` 函数
- 删除 `openServiceModal` 中 `$('#mappingTestInput').value = ''` 和 `runMappingTest()` 调用
- 删除所有事件处理中的 `runMappingTest()` 调用（input / change / click / addMappingBtn）
- 删除 `$('#mappingTestInput').addEventListener('input', runMappingTest)`
- 更新区块注释

### 7. 修改 `AiProxy/Admin/wwwroot/i18n.js`

- 更新 `service.mappingsHint`：zh `支持正则` → `通配符：* 匹配任意字符，? 匹配单个字符`；en 同步
- 更新 `service.mappingsPatternPlaceholder`：zh `如 ^gpt-4$` → `如 gpt-4*`；en 同步
- 删除 5 个 i18n key：`service.mappingsTest` / `service.mappingsTestPlaceholder` / `service.mappingsResult` / `service.mappingsNoMatch` / `service.mappingsInvalidRegex`（zh + en）

### 8. 修改 `AiProxy/Admin/wwwroot/style.css`

- 删除 `.mapping-match` 规则（匹配测试高亮样式）

### 9. 修改 `README.md` + `README.en.md`

- 特性表：`正则规则` → `通配符规则`
- 配置示例：`^gpt-4$` → `gpt-4`，`^claude-3-opus.*` → `claude-3-opus*`
- 字段说明表：Pattern 改通配符，Replacement 移除反向引用
- 匹配规则：`Regex.Replace` → 直接替换
- "正则与安全" 节 → "通配符规则" 节（`*` / `?` 语义，空串合法，无 ReDoS 风险）
- 管理面板操作：移除"正则匹配测试"条目

## Verification

1. `dotnet build` 编译通过
2. 启动服务，在管理面板中新增/编辑服务，添加模型映射（使用通配符 `gpt-4*`），保存成功
3. 配置通配符映射后发送请求，验证 model 字段被正确替换
4. 确认管理面板中不再显示匹配测试区域
5. 确认 README 文档中无正则相关残留描述
