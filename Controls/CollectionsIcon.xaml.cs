using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinIconFinder.Controls;

public sealed partial class CollectionsIcon : PathIcon
{
    public CollectionsIcon()
    {
        InitializeComponent();

        if (Data is Geometry geometry)
        {
            geometry.Transform = new TranslateTransform
            {
                X = -97.792,
                Y = -115.072
            };
        }
    }
}
