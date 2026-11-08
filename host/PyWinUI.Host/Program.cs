using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PyWinUI.Host;

/// <summary>
/// 宿主入口。
///
/// 自己写 Main 而不是让 XAML 编译器生成：需要在 <c>Application.Start</c> 之前
/// 和之外保留控制权，方便打点和后续加命令行参数。
/// </summary>
public static class Program
{
    internal static string LogPath => HostLog.FilePath;

    internal static void Log(string message) => HostLog.Write(message);

    [STAThread]
    private static void Main(string[] args)
    {
        HostLog.Reset();

        Log($"args: {string.Join(' ', args)}");
        Log($"os: {Environment.OSVersion.VersionString}");

        try
        {
            Log("step 1: InitializeComWrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Log("step 2: Application.Start");
            Application.Start(callbackParams =>
            {
                Log("step 3: inside callback");

                var queue = DispatcherQueue.GetForCurrentThread();
                Log($"step 4: dispatcher queue = {(queue is null ? "NULL" : "ok")}");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));

                Log("step 5: new HostApp");
                _ = new HostApp();
                Log("step 6: HostApp constructed");
            });

            Log("step 7: Application.Start returned");
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            throw;
        }
    }
}

// HostApp 定义在 App.xaml.cs：它必须是 partial，好让 XAML 编译器补上
// InitializeComponent 和类型元数据提供器。
