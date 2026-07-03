using System.Text;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 一个完整的 SSE 事件（由空行分隔的若干字段行组成）。
/// </summary>
public sealed class SseEvent
{
    /// <summary>event: 字段值（缺省为 "message"）</summary>
    public string Event { get; set; } = "message";

    /// <summary>data: 字段值（多个 data 行以 \n 拼接）</summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>是否为终止标记 data: [DONE]</summary>
    public bool IsDone => Data == "[DONE]";
}

/// <summary>
/// SSE 帧切分器：将任意字节边界的流重组为完整 SSE 事件。
/// SSE 协议：事件由若干字段行组成，以空行（\n\n 或 \r\n\r\n）结束；
/// 字段行格式 "field: value"（冒号后可选一个空格）；":..." 为注释；
/// event: 设置事件类型，data: 追加数据（多个 data 行以 \n 拼接），id:/retry: 此处忽略。
/// 流式响应转换器用本类先把字节流切成事件，再逐事件喂入状态机转换。
/// </summary>
public sealed class SseFrameReader
{
    private readonly StringBuilder _pending = new();
    private string? _currentEvent;
    private readonly StringBuilder _currentData = new();
    private bool _hasField;

    /// <summary>喂入一段字节，返回由此段（及之前缓冲）完成的完整事件。</summary>
    public List<SseEvent> Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return new List<SseEvent>();
        }
        var text = Encoding.UTF8.GetString(chunk);
        return FeedText(text);
    }

    /// <summary>喂入文本，返回完成的完整事件。</summary>
    public List<SseEvent> FeedText(string text)
    {
        var events = new List<SseEvent>();
        _pending.Append(text);

        while (TryTakeLine(out var line))
        {
            ProcessLine(line, events);
        }

        return events;
    }

    /// <summary>流结束：处理残余缓冲并返回未以空行结束的待发事件（如有）。</summary>
    public List<SseEvent> Flush()
    {
        var events = new List<SseEvent>();

        // 残余未换行的最后一行按完整行处理
        if (_pending.Length > 0)
        {
            var line = _pending.ToString();
            _pending.Clear();
            ProcessLine(line, events);
        }

        // 未以空行结束的待发事件
        if (_hasField)
        {
            events.Add(new SseEvent
            {
                Event = _currentEvent ?? "message",
                Data = _currentData.ToString()
            });
            ResetCurrent();
        }

        return events;
    }

    private void ProcessLine(string line, List<SseEvent> events)
    {
        if (line.Length == 0)
        {
            // 空行：结束当前事件（仅当已收集字段时才发射）
            if (_hasField)
            {
                events.Add(new SseEvent
                {
                    Event = _currentEvent ?? "message",
                    Data = _currentData.ToString()
                });
                ResetCurrent();
            }
            return;
        }

        // 注释行
        if (line[0] == ':')
        {
            _hasField = true;
            return;
        }

        // 解析 "field: value" 或 "field"（无冒号则整行为字段名，值为空）
        string field;
        string value;
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            field = line;
            value = string.Empty;
        }
        else
        {
            field = line[..colon];
            // 冒号后可选一个空格，需剔除
            var rest = line[(colon + 1)..];
            if (rest.Length > 0 && rest[0] == ' ')
            {
                rest = rest[1..];
            }
            value = rest;
        }

        _hasField = true;
        switch (field)
        {
            case "event":
                _currentEvent = value;
                break;
            case "data":
                if (_currentData.Length > 0)
                {
                    _currentData.Append('\n');
                }
                _currentData.Append(value);
                break;
            // id: / retry: 等字段忽略
        }
    }

    private void ResetCurrent()
    {
        _currentEvent = null;
        _currentData.Clear();
        _hasField = false;
    }

    /// <summary>从 _pending 中取出一行（以 \n 分隔，剔除 \r），剩余保留。无 \n 返回 false。</summary>
    private bool TryTakeLine(out string line)
    {
        var full = _pending.ToString();
        var nl = full.IndexOf('\n');
        if (nl < 0)
        {
            line = string.Empty;
            return false;
        }
        line = full[..nl];
        // 剔除行尾 \r
        if (line.Length > 0 && line[^1] == '\r')
        {
            line = line[..^1];
        }
        _pending.Remove(0, nl + 1);
        return true;
    }
}
