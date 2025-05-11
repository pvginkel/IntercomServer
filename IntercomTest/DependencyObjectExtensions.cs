using System.Windows;
using System.Windows.Media;

namespace IntercomTest;

internal static class DependencyObjectExtensions
{
    public static Window? GetWindow(this DependencyObject self)
    {
        while (true)
        {
            var parent = VisualTreeHelper.GetParent(self);

            switch (parent)
            {
                case null:
                    return null;

                case Window window:
                    return window;

                default:
                    self = parent;
                    break;
            }
        }
    }
}
