using System.Text;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 响应转换包装流：将下游格式响应字节实时转换为客户端格式后写入真实客户端流，
/// 同时旁路捕获**转换后**字节供日志解析。
///
/// 两种工作模式（首次 Write 时按 <c>isStreamResolver</c> 判定，依据 Response.ContentType）：
/// <list type="bullet">
/// <item><b>流式</b>（text/event-stream）：将下游 chunk 喂 <see cref="IStreamingResponseConverter.Process"/>，
///   所得转换字节立即写 inner + 捕获；<see cref="CompleteAsync"/> 调 <see cref="IStreamingResponseConverter.Flush"/> 写尾段。</item>
/// <item><b>非流式</b>（JSON）：Write 时仅缓冲原始字节到内部 <see cref="MemoryStream"/>；
///   <see cref="CompleteAsync"/> 时整体 <see cref="INonStreamingResponseConverter.Convert"/> 后写 inner + 捕获。</item>
/// </list>
///
/// 关键时序：<see cref="CompleteAsync"/> 必须在 <see cref="GetCapturedSpan"/> 之前调用
/// （由 <c>RequestLoggingMiddleware</c> 在还原 Response.Body 后显式 await 调用）。
/// 非流式模式的整体转换在 <see cref="CompleteAsync"/> 中完成，否则捕获为空。
/// <see cref="Dispose(bool)"/>/<see cref="DisposeAsync"/> 兜底调用 <see cref="CompleteAsync"/>，防中间件遗漏。
///
/// 所有对内部响应流（<c>_inner</c>）的写入均通过 <c>WriteAsync</c> 完成：Kestrel 默认禁用同步 IO
/// （AllowSynchronousIO=false），同步调用 <see cref="Stream.Write(byte[],int,int)"/> 会抛
/// <see cref="InvalidOperationException"/>。
///
/// fail-open：转换过程抛异常时回退为透传原始字节（写 inner + 捕获），避免转换 bug 卡死响应。
/// 非流式缓冲超 <see cref="MaxCaptureBytes"/> 时亦 fail-open 透传，避免超大响应吃光内存。
/// </summary>
internal sealed class ConvertingStream : Stream
{
    /// <summary>捕获与缓冲上限，避免超大响应吃光内存</summary>
    private const long MaxCaptureBytes = 64 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly IStreamingResponseConverter _streamingConverter;
    private readonly INonStreamingResponseConverter _nonStreamConverter;
    private readonly Func<bool> _isStreamResolver;

    private readonly MemoryStream _capture = new();
    private long _captured;

    // 下游原始字节捕获（转换前），供日志记录下游原始响应
    private readonly MemoryStream _rawCapture = new();
    private long _rawCaptured;

    // 非流式模式缓冲下游原始字节（未转换）
    private readonly MemoryStream _rawBuffer = new();

    private bool _modeDecided;
    private bool _isStreamMode;
    private bool _completed;
    // 非流式缓冲超限：切换为直接透传，不再缓冲/转换
    private bool _overflow;

    public ConvertingStream(
        Stream innerClient,
        IStreamingResponseConverter streamingConverter,
        INonStreamingResponseConverter nonStreamConverter,
        Func<bool> isStreamResolver)
    {
        _inner = innerClient;
        _streamingConverter = streamingConverter;
        _nonStreamConverter = nonStreamConverter;
        _isStreamResolver = isStreamResolver;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override async Task FlushAsync(CancellationToken cancellationToken) => await _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteCore(new ReadOnlySpan<byte>(buffer, offset, count));

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 注意：转换与捕获是同步内存操作，不依赖 ct；仅 inner 的异步写接受 ct
        var converted = WriteCoreBytes(buffer.Span);
        if (converted.Length > 0)
        {
            await _inner.WriteAsync(converted, cancellationToken);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var converted = WriteCoreBytes(new ReadOnlySpan<byte>(buffer, offset, count));
        if (converted.Length > 0)
        {
            await _inner.WriteAsync(converted, cancellationToken);
        }
    }

    /// <summary>
    /// 同步写核心：决定模式 → 转换 → 写 inner + 捕获。
    /// 流式模式：转换后字节立即写 inner；非流式模式：仅缓冲原始字节（不写 inner），待 Complete 整体转换。
    /// </summary>
    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        var converted = WriteCoreBytes(buffer);
        if (converted.Length > 0)
        {
            _inner.Write(converted);
        }
    }

    /// <summary>
    /// 转换核心：返回应写入 inner 的字节（流式=转换后字节 / fail-open=原始字节），
    /// 或空数组表示暂不写 inner（非流式缓冲中）。同时更新捕获缓冲（流式=转换后字节；非流式=Complete 时统一捕获）。
    /// 本方法不直接写 inner，由调用方（WriteCore/WriteAsync）统一写入。
    /// </summary>
    private byte[] WriteCoreBytes(ReadOnlySpan<byte> buffer)
    {
        if (!_modeDecided)
        {
            DecideMode();
        }

        // 捕获下游原始字节（转换前）
        CaptureRawBytes(buffer);

        if (_isStreamMode)
        {
            // 流式：转换并立即返回待写 inner 的字节
            try
            {
                var converted = _streamingConverter.Process(buffer);
                CaptureBytes(converted);
                return converted;
            }
            catch
            {
                // fail-open：透传原始字节（由调用方写 inner）
                var raw = buffer.ToArray();
                CaptureBytes(raw);
                return raw;
            }
        }
        else
        {
            // 非流式：缓冲原始字节，不写 inner。超限时 fail-open 透传避免 OOM。
            // 注意：此处不能直接调用 _inner.Write/WriteAsync，必须把待写字节返回给调用方
            // （WriteCore 用同步 Write，WriteAsync 用异步 WriteAsync），否则在 Kestrel 上
            // 同步调用 Write 会抛 InvalidOperationException（Synchronous operations are disallowed）。
            if (_overflow)
            {
                var raw = buffer.ToArray();
                CaptureBytes(raw);
                return raw;
            }
            if (_rawBuffer.Length + buffer.Length > MaxCaptureBytes)
            {
                // 超限：刷出已缓冲字节 + 当前字节，后续直接透传
                _overflow = true;
                var already = _rawBuffer.ToArray();
                var cur = buffer.ToArray();
                var combined = new byte[already.Length + cur.Length];
                Buffer.BlockCopy(already, 0, combined, 0, already.Length);
                Buffer.BlockCopy(cur, 0, combined, already.Length, cur.Length);
                CaptureBytes(already);
                CaptureBytes(cur);
                return combined;
            }
            _rawBuffer.Write(buffer);
            return Array.Empty<byte>();
        }
    }

    private void DecideMode()
    {
        _modeDecided = true;
        try
        {
            _isStreamMode = _isStreamResolver();
        }
        catch
        {
            _isStreamMode = false;
        }
    }

    /// <summary>
    /// 终结转换。幂等。
    /// 流式：调 <see cref="IStreamingResponseConverter.Flush"/> 写尾段 + Dispose 转换器。
    /// 非流式：整体 <see cref="INonStreamingResponseConverter.Convert"/> 后写 inner + 捕获（超限透传时跳过）。
    /// 必须使用异步写入：Kestrel 默认禁用同步 IO（AllowSynchronousIO=false），
    /// 对响应流调用同步 <see cref="Stream.Write"/> 会抛 InvalidOperationException。
    /// </summary>
    public async Task CompleteAsync()
    {
        if (_completed) return;
        _completed = true;

        try
        {
            if (!_modeDecided)
            {
                DecideMode();
            }

            if (_isStreamMode)
            {
                try
                {
                    var tail = _streamingConverter.Flush();
                    if (tail.Length > 0)
                    {
                        await _inner.WriteAsync(tail);
                        CaptureBytes(tail);
                    }
                }
                catch
                {
                    // fail-open：尾段转换失败忽略（主体已转发）
                }
                finally
                {
                    _streamingConverter.Dispose();
                }
            }
            else if (!_overflow)
            {
                // 非流式：整体转换缓冲的原始字节（超限透传场景已在 Write 中写出，跳过）
                var rawBytes = _rawBuffer.ToArray();
                var rawText = Encoding.UTF8.GetString(rawBytes);
                string convertedText;
                try
                {
                    convertedText = _nonStreamConverter.Convert(rawText);
                }
                catch
                {
                    // fail-open：转换失败透传原始字节
                    convertedText = rawText;
                }
                var convertedBytes = Encoding.UTF8.GetBytes(convertedText);
                if (convertedBytes.Length > 0)
                {
                    await _inner.WriteAsync(convertedBytes);
                    CaptureBytes(convertedBytes);
                }
            }
        }
        finally
        {
            _rawBuffer.Dispose();
        }
    }

    /// <summary>已捕获的转换后字节切片（供日志解析）。非流式模式下需先 <see cref="Complete"/>。</summary>
    public ReadOnlySpan<byte> GetCapturedSpan()
    {
        if (_captured == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        return _capture.GetBuffer().AsSpan(0, (int)Math.Min(_captured, int.MaxValue));
    }

    /// <summary>已捕获的下游原始字节切片（转换前，供日志记录下游原始响应）。</summary>
    public ReadOnlySpan<byte> GetRawCapturedSpan()
    {
        if (_rawCaptured == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        return _rawCapture.GetBuffer().AsSpan(0, (int)Math.Min(_rawCaptured, int.MaxValue));
    }

    private void CaptureBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || _captured >= MaxCaptureBytes) return;
        int toCopy = (int)Math.Min(bytes.Length, MaxCaptureBytes - _captured);
        if (toCopy > 0)
        {
            _capture.Write(bytes, 0, toCopy);
            _captured += toCopy;
        }
    }

    private void CaptureRawBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || _rawCaptured >= MaxCaptureBytes) return;
        int toCopy = (int)Math.Min(bytes.Length, MaxCaptureBytes - _rawCaptured);
        if (toCopy > 0)
        {
            _rawCapture.Write(bytes.Slice(0, toCopy));
            _rawCaptured += toCopy;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteAsync().GetAwaiter().GetResult();
            _capture.Dispose();
            _rawCapture.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CompleteAsync();
        _capture.Dispose();
        _rawCapture.Dispose();
        await base.DisposeAsync();
    }
}
