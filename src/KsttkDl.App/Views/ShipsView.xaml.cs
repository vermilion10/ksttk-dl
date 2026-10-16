using System.Windows;
using System.Windows.Controls;
using KsttkDl.App.ViewModels;

namespace KsttkDl.App.Views;

public partial class ShipsView : UserControl
{
    private const double TileWidth = 164;

    public ShipsView() => InitializeComponent();

    private ShipsViewModel? Model => DataContext as ShipsViewModel;

    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Model?.Gallery.SetColumns((int)((e.NewSize.Width - 14) / TileWidth));

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryRow<ShipTile> row })
        {
            Model?.Gallery.Show(row);
        }
    }
}
