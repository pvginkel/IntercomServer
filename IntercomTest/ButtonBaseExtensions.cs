using System.Windows;
using System.Windows.Controls.Primitives;

namespace IntercomTest
{
    internal static class ButtonBaseExtensions
    {
        public static void PerformClick(this ButtonBase self)
        {
            self.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
    }
}
