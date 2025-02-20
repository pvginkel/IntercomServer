using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Color = System.Windows.Media.Color;

namespace IntercomTest;

/// <summary>
/// Base class for WPF windows.
/// </summary>
/// <remarks>
/// Use this class as the base class for any WPF windows you create to
/// integrate with the TQL theme and styling.
/// </remarks>
public class BaseWindow : Window
{
    /// <summary>
    /// Initializes a new <see cref="BaseWindow"/>.
    /// </summary>
    public BaseWindow()
    {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ShowInTaskbar = false;
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        Style = (Style)FindResource("CustomWindowStyle");
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var isBackgroundLight = true;
        if (Background is SolidColorBrush brush)
            isBackgroundLight = IsColorLight(brush.Color);

        if (!isBackgroundLight)
        {
            var interop = new WindowInteropHelper(this);

            unsafe
            {
                var value = 1;
                PInvoke.DwmSetWindowAttribute(
                    new HWND(interop.Handle),
                    DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    &value,
                    sizeof(int)
                );
            }
        }
    }

    // Taken from https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/apply-windows-themes.
    private static bool IsColorLight(Color clr)
    {
        return (((5 * clr.G) + (2 * clr.R) + clr.B) > (8 * 128));
    }
}
