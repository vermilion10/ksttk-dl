using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KsttkDl.App.ViewModels;

namespace KsttkDl.App.Views;

public partial class RoomView : UserControl
{
    private Point? _dragFrom;

    public RoomView() => InitializeComponent();

    private RoomViewModel? Model => DataContext as RoomViewModel;

    // positions come from the canvas itself, so they are already in room pixels whatever the viewbox scale
    private void Room_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(RoomCanvas);
        RoomCanvas.CaptureMouse();
        Focus();
    }

    private void Room_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var to = e.GetPosition(RoomCanvas);
        var dx = Math.Round(to.X - from.X);
        var dy = Math.Round(to.Y - from.Y);
        if (dx == 0 && dy == 0)
        {
            return;
        }

        Model?.DragBy(dx, dy);
        _dragFrom = new Point(from.X + dx, from.Y + dy);
    }

    private void Room_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        RoomCanvas.ReleaseMouseCapture();
    }

    private void FurnitureRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryRow<EntityTile> row })
        {
            Model?.Gallery.Show(row);
        }
    }

    private void ShipRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryRow<ShipTile> row })
        {
            Model?.ShipGallery.Show(row);
        }
    }
}
