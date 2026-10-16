using System.Windows;
using System.Windows.Controls;
using KsttkDl.App.ViewModels;

namespace KsttkDl.App.Views;

/// <summary>the gallery page shared by equipment and furniture.</summary>
public partial class CollectionView : UserControl
{
    private const double TileWidth = 178;

    public CollectionView() => InitializeComponent();

    private CollectionViewModel? Model => DataContext as CollectionViewModel;

    private void Tiles_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Model?.Gallery.SetColumns((int)((e.NewSize.Width - 14) / TileWidth));

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryRow<EntityTile> row })
        {
            Model?.Gallery.Show(row);
        }
    }
}
