using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LLMContinuityToolkit.App;

internal static class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [ModuleInitializer]
    internal static void Initialize()
        => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        TrySet(hwnd, DwmUseImmersiveDarkMode, 1);
        TrySet(hwnd, DwmCaptionColor, ColorRef(15, 23, 42));
        TrySet(hwnd, DwmBorderColor, ColorRef(51, 65, 85));
        TrySet(hwnd, DwmTextColor, ColorRef(248, 250, 252));
    }

    private static int ColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);
    private static void TrySet(IntPtr hwnd, int attribute, int value)
    {
        try { _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
