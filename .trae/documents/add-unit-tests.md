# 补充单元测试

## Context

项目当前没有任何测试项目或测试文件。需要新建 xUnit 测试项目，为核心工具类和逻辑编写单元测试，确保通配符匹配、密钥脱敏、JSON 解析、模型映射、SSE 聚合等关键功能的正确性。

## 方案

新建 `AiProxy.Tests` xUnit 测试项目（net10.0），加入解决方案。按可测试性从高到低分优先级编写测试。

### 测试项目创建

- 新建 `d:\MySource\AIProxy\AiProxy.Tests\AiProxy.Tests.csproj`
  - `TargetFramework`: net10.0
  - 依赖：xUnit、xUnit.runner.visualstudio、Microsoft.NET.Test.Sdk
  - ProjectReference: `..\AiProxy\AiProxy.csproj`
- 在 `AiProxy.sln` 中添加测试项目
- 主项目需对测试项目暴露 `internal` 成员：在 `AiProxy.csproj` 中添加 `InternalsVisibleTo Include="AiProxy.Tests"`，或添加 `AssemblyInfo.cs`

### 测试类 & 用例

#### 1. `WildcardMatcherTests` — `AiProxy/Util/WildcardMatcher.cs`（public static，零依赖）

| 用例 | Pattern | Input | 期望 |
|------|---------|-------|------|
| 精确匹配 | `gpt-4` | `gpt-4` | true |
| 精确不匹配 | `gpt-4` | `gpt-4o` | false |
| `*` 匹配任意后缀 | `gpt-4*` | `gpt-4o` | true |
| `*` 匹配空 | `gpt-4*` | `gpt-4` | true |
| `*` 匹配多段 | `gpt-4*` | `gpt-4-turbo-preview` | true |
| `?` 单字符 | `gpt-?` | `gpt-4` | true |
| `?` 不匹配空 | `gpt-?` | `gpt-` | false |
| `?` 不匹配多字符 | `gpt-?` | `gpt-4o` | false |
| 多 `*` | `*-*` | `claude-3-opus` | true |
| 空 pattern | `` | `gpt-4` | false |
| 空 input | `gpt-4` | `` | false |
| 双空 | `` | `` | true |
| 全 `*` | `*` | `anything` | true |
| 大小写敏感 | `GPT-4` | `gpt-4` | false |
| `*` 在中间 | `gpt*4` | `gpt-4` | true |
| `?` 在中间 | `g?ting` | `getting` | true |
| 多 `?` | `??-4` | `gp-4` | true |

#### 2. `KeyMaskerTests` — `AiProxy/Util/KeyMasker.cs`（public static，零依赖）

| 用例 | Input | 期望 |
|------|-------|------|
| 正常长密钥 | `sk-abcd1234efgh5678` | `sk-****5678` |
| 短密钥(≤4) | `abc` | `****` |
| 刚好5位 | `abcde` | `ab****cde` → 需验证实际行为 |
| null | null | `""` |
| 空串 | `""` | `""` |
| 4位 | `abcd` | `****` |
| 6位 | `abcdef` | `abc****def` → 需验证 |

#### 3. `OpenAiParserTests` — `AiProxy/Forwarding/OpenAiParser.cs`（internal static，需 InternalsVisibleTo）

**TryGetModel:**
| 用例 | Input | 期望 |
|------|-------|------|
| 正常提取 | `{"model":"gpt-4","messages":[]}` | `gpt-4` |
| 无 model | `{"messages":[]}` | null |
| model 非 string | `{"model":123}` | null |
| 空 JSON | `{}` | null |
| null | null | null |
| 空串 | `""` | null |
| 非 JSON | `not json` | null |

**TryGetUsage:**
| 用例 | Input | 期望 |
|------|-------|------|
| OpenAI 格式 | `{"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}` | (10,20,30) |
| Anthropic 格式 | `{"usage":{"input_tokens":10,"output_tokens":20}}` | (10,20,30) |
| 无 usage | `{}` | (null,null,null) |
| null | null | (null,null,null) |

#### 4. `EndpointPathMapperTests` — `AiProxy/Forwarding/Converters/EndpointPathMapper.cs`（internal static，需 InternalsVisibleTo）

| 用例 | Path | From | To | 期望 |
|------|------|------|----|------|
| Anthropic→OpenAI | `/v1/messages` | Anthropic | OpenAI | `/v1/chat/completions` |
| OpenAI→Anthropic | `/v1/chat/completions` | OpenAI | Anthropic | `/v1/messages` |
| 同格式透传 | `/v1/chat/completions` | OpenAI | OpenAI | `/v1/chat/completions` |
| 无前缀 | `/messages` | Anthropic | OpenAI | `/chat/completions` |
| 未知路径 | `/v1/unknown` | Anthropic | OpenAI | `/v1/unknown` |

#### 5. `ApplyModelMappingsTests` — `AiProxy/Forwarding/ForwardingEndpoint.cs`（private static，需改为 internal 或通过公共入口间接测试）

由于 `ApplyModelMappings` 和 `ReplaceModelInBody` 是 `private static`，有两个选择：
- **方案 A**（推荐）：将这两个方法改为 `internal static`，通过 `InternalsVisibleTo` 暴露给测试
- **方案 B**：通过公共方法 `ConvertRequestBodyAsync` 间接测试，但需要构造 HttpContext，较重

采用方案 A。

| 用例 | Body | Mappings | 期望 changed | 期望 model |
|------|------|----------|-------------|-----------|
| 空映射 | `{"model":"gpt-4"}` | [] | false | gpt-4 |
| 命中替换 | `{"model":"gpt-4"}` | [{P:"gpt-4",R:"gpt-4o",E:true}] | true | gpt-4o |
| 通配符命中 | `{"model":"gpt-4o"}` | [{P:"gpt-4*",R:"gpt-4-turbo",E:true}] | true | gpt-4-turbo |
| 首次匹配 | `{"model":"gpt-4"}` | [{P:"gpt-4",R:"a",E:true},{P:"gpt-4",R:"b",E:true}] | true | a |
| disabled 跳过 | `{"model":"gpt-4"}` | [{P:"gpt-4",R:"a",E:false}] | false | gpt-4 |
| 无命中 | `{"model":"gpt-4"}` | [{P:"claude*",R:"a",E:true}] | false | gpt-4 |
| 替换值相同 | `{"model":"gpt-4"}` | [{P:"gpt-4",R:"gpt-4",E:true}] | false | gpt-4 |
| 无 model 字段 | `{"messages":[]}` | [{P:"*",R:"x",E:true}] | false | - |

#### 6. `SseAggregatorTests` — `AiProxy/Forwarding/SseAggregator.cs`（internal static，需 InternalsVisibleTo）

| 用例 | 输入 SSE 流 | 期望 |
|------|-----------|------|
| 空 bytes | `Span<byte>.Empty` | ("", null, null, null) |
| OpenAI 简单流 | `data: {"choices":[{"delta":{"content":"Hi"}}]}\ndata: [DONE]\n` | 包含 "Hi" 的完整 JSON |
| Anthropic 简单流 | `data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}\n` | 包含 "Hi" 的完整 JSON |
| 非 JSON fallback | `data: not-json-here\n` | "not-json-here\n" |

## 具体步骤

1. 新建 `AiProxy.Tests` 项目（csproj + 空目录）
2. 在 `AiProxy.sln` 添加项目引用
3. 在 `AiProxy` 主项目添加 `InternalsVisibleTo("AiProxy.Tests")`
4. 将 `ForwardingEndpoint.ApplyModelMappings` 和 `ReplaceModelInBody` 从 `private static` 改为 `internal static`
5. 编写 `WildcardMatcherTests`
6. 编写 `KeyMaskerTests`
7. 编写 `OpenAiParserTests`
8. 编写 `EndpointPathMapperTests`
9. 编写 `ApplyModelMappingsTests`
10. 编写 `SseAggregatorTests`
11. `dotnet test` 验证全部通过

## Verification

- `dotnet test` 全部通过
- 测试覆盖核心工具类（WildcardMatcher、KeyMasker）和核心逻辑（OpenAiParser、EndpointPathMapper、ApplyModelMappings、SseAggregator）
