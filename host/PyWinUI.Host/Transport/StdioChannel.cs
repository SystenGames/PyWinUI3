using System.Text;

namespace PyWinUI.Host.Transport;

/// <summary>
/// stdin/stdout 上的行分帧通道。
///
/// 为什么不直接用 <c>Console.In</c> / <c>Console.Out</c>：
/// 那两个是带缓冲的文本包装，且受控制台代码页影响。这里显式指定 UTF-8 无 BOM，
/// 和 Python 侧的 <c>encode()</c> 严格对齐，避免中文在管道里变成乱码。
///
/// 读在后台线程（阻塞读不能占 UI 线程），写在任意线程（加锁）。
/// </summary>
public sealed class StdioChannel : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private Thread? _thread;
    private volatile bool _disposed;

    /// <summary>收到一整行（不含换行符）。在后台线程触发。</summary>
    public event Action<string>? LineReceived;

    /// <summary>输入流结束。参数是异常信息，正常 EOF 时为 null。</summary>
    public event Action<string?>? Closed;

    public StdioChannel()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        _reader = new StreamReader(
            Console.OpenStandardInput(),
            utf8,
            detectEncodingFromByteOrderMarks: false);

        _writer = new StreamWriter(Console.OpenStandardOutput(), utf8)
        {
            // 每行都手动 Flush：Python 侧是阻塞读，漏一次 flush 就卡死
            AutoFlush = false,
        };
    }

    public void Start()
    {
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "pywinui-stdin",
        };
        _thread.Start();
    }

    private void ReadLoop()
    {
        string? error = null;
        try
        {
            string? line;
            while (!_disposed && (line = _reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (Encoding.UTF8.GetByteCount(line) > Protocol.Wire.MaxLineBytes)
                {
                    HostLog.Write($"丢弃超长消息（{line.Length} 字符）");
                    continue;
                }

                LineReceived?.Invoke(line);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Closed?.Invoke(error);
        }
    }

    /// <summary>写一行消息（不含换行符）。线程安全。</summary>
    public void Send(string line)
    {
        if (_disposed)
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
            }
            catch (IOException)
            {
                // 对端已经关了，没什么可做的
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _reader.Dispose();
        }
        catch
        {
        }

        lock (_writeLock)
        {
            try
            {
                _writer.Dispose();
            }
            catch
            {
            }
        }
    }
}
