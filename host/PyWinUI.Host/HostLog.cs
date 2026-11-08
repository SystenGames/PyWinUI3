using System.Text;

namespace PyWinUI.Host;

/// <summary>
/// 宿主日志。
///
/// 存在的理由：WinUI 3 的失败经常是 stowed exception，进程直接以 0xC000027B 退出，
/// 托管侧的 try/catch 抓不到，也没有任何输出。唯一能用的手段是往文件里逐行打点，
/// 事后看最后一行停在哪。
///
/// 因此这里的所有方法都不允许抛异常——日志本身绝不能成为故障源。
/// </summary>
internal static class HostLog
{
    private static readonly object Gate = new();

    /// <summary>日志落盘位置。放在 TEMP 下，避免污染程序目录。</summary>
    public static string FilePath { get; } =
        Path.Combine(Path.GetTempPath(), "pywinui-host.log");

    private static bool _initialized;

    /// <summary>清空日志，用于每次启动时重新开始。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(FilePath, "", Encoding.UTF8);
            }
            catch
            {
            }

            _initialized = true;
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                _initialized = true;
                try
                {
                    File.WriteAllText(FilePath, "", Encoding.UTF8);
                }
                catch
                {
                }
            }

            try
            {
                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    public static void Write(Exception ex, string context)
    {
        Write($"{context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace is { Length: > 0 })
        {
            Write("    " + ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "    "));
        }
    }
}
