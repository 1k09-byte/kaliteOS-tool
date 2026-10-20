using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.GpuOverclock.Views
{
    public sealed class VfEditorCanvas : Canvas
    {
        public void ShowHandCursor(bool hand)
        {
            try
            {
                ProtectedCursor = InputSystemCursor.Create(
                    hand ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
            }
            catch { }
        }
    }
}
