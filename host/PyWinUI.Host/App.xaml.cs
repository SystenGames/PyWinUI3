using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace PyWinUI.Host;

/// <summary>
/// 应用对象。<c>partial</c> 是必须的——XAML 编译器会生成另一半，
/// 里面有 <c>InitializeComponent</c> 和类型元数据提供器。
/// </summary>
public sealed partial class HostApp : Application
{
    /// <summary>加载圈边长（DIP）。初始窗口按它的 3 倍见方来定。</summary>
    private const double RingSize = 56;

    /// <summary>
    /// 加载态至少显示这么久。
    ///
    /// 宿主初始化本身就要几百毫秒，Python 的 render 往往在窗口刚出现时就已经
    /// 在管道里排队了。不设下限的话加载圈只闪一两帧，看起来像界面抖了一下。
    /// 延迟的是「开始读 stdin」而不是「开始渲染」——管道有缓冲，消息不会丢。
    /// </summary>
    private const int LoadingFloorMs = 500;

    private Window? _window;
    private Session? _session;

    public HostApp()
    {
        InitializeComponent();
        HostLog.Write("ctor: InitializeComponent done");

        UnhandledException += (_, e) => HostLog.Write("UnhandledException: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        HostLog.Write("OnLaunched: begin");

        _window = new Window
        {
            Title = "正在加载",
        };

        // 先放一个加载占位：单独双击 exe 时也能确认宿主活着。
        // 收到 render 消息后，整个内容会被 Python 描述的控件树替换掉。
        _window.Content = BuildLoadingView();

        ApplyInitialGeometry();

        _window.Activate();
        HostLog.Write("OnLaunched: window activated");

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _session = new Session(_window, dispatcher);
        StartSessionAfterFloor(dispatcher);

        HostLog.Write("OnLaunched: waiting for loading floor");
    }

    /// <summary>
    /// 等加载圈显示满 <see cref="LoadingFloorMs"/> 再开始读协议。
    /// 用 DispatcherQueueTimer 而不是 Thread.Sleep：睡眠会冻住 UI 线程，
    /// 加载圈自己也不转了。
    /// </summary>
    private void StartSessionAfterFloor(DispatcherQueue dispatcher)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(LoadingFloorMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            HostLog.Write($"[loader] floor {LoadingFloorMs}ms elapsed, session starting");
            _session?.Start();
        };
        timer.Start();
    }

    /// <summary>
    /// 初始窗口：3 个加载环见方，居中到主显示器。
    ///
    /// 这一步只在加载阶段有效——Python 发来 render 后，窗口尺寸会被
    /// 协议里的 window.width / window.height 覆盖。
    /// </summary>
    private void ApplyInitialGeometry()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var appWindow = _window.AppWindow;
            var scale = GetDpiScale();
            var side = (int)Math.Round(RingSize * 3 * scale);

            appWindow.Resize(new SizeInt32(side, side));

            // 加载阶段这个窗口只是个提示，不该让人最大化或最小化。
            // 收尾能力由 Session 在收到第一条 render 时恢复。
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }

            var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var size = appWindow.Size;

            appWindow.Move(new PointInt32(
                work.X + (work.Width - size.Width) / 2,
                work.Y + (work.Height - size.Height) / 2));

            HostLog.Write($"[window] initial geometry {side}x{side}px (scale={scale}) centered");
        }
        catch (Exception ex)
        {
            HostLog.Write("initial geometry failed: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private double GetDpiScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 等待 Python 发来 render 之前的占位界面：居中转圈的加载指示器 + 一行说明。
    /// </summary>
    private static FrameworkElement BuildLoadingView()
    {
        var ring = new ProgressRing
        {
            IsActive = true,
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var caption = new TextBlock
        {
            Text = "正在加载环境...",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.72,
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
        };
        stack.Children.Add(ring);
        stack.Children.Add(caption);

        return stack;
    }
}
