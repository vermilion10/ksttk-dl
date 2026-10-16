using System.Collections.ObjectModel;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Catalog;

namespace KsttkDl.App.ViewModels;

/// <summary>one picture in a gallery: the asset it shows and, once downloaded, the file.</summary>
public abstract class Tile : Observable
{
    private string? _imagePath;
    private bool _absent;

    public abstract int Id { get; }

    public abstract string Title { get; }

    public virtual string Subtitle => "";

    /// <summary>the asset the gallery shows for this tile; null when the entity has none.</summary>
    public abstract AssetEntry? Picture { get; }

    public string? ImagePath
    {
        get => _imagePath;
        private set
        {
            if (Set(ref _imagePath, value))
            {
                Raise(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _imagePath is not null;

    /// <summary>the cdn has no such file.</summary>
    public bool Absent
    {
        get => _absent;
        private set => Set(ref _absent, value);
    }

    public void Refresh()
    {
        var library = AppServices.Current.Library;
        if (library is null || Picture is null)
        {
            Absent = Picture is null;
            return;
        }

        ImagePath = library.LocalPath(Picture);
        Absent = ImagePath is null && library.IsMissing(Picture);
    }
}

public sealed class GalleryRow<T>(IReadOnlyList<T> items)
{
    public IReadOnlyList<T> Items { get; } = items;
}

/// <summary> lays tiles out in rows for a virtualizing list: wpf has no virtualizing wrap panel, so the page measures how many columns fit and the gallery is re-chunked whenever that changes </summary>
public sealed class Gallery<T> : Observable where T : Tile
{
    private IReadOnlyList<T> _items = [];
    private int _columns = 5;

    public ObservableCollection<GalleryRow<T>> Rows { get; } = [];

    public int Count => _items.Count;

    public void SetItems(IReadOnlyList<T> items)
    {
        _items = items;
        Rebuild();
        Raise(nameof(Count));
    }

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        Rebuild();
    }

    /// <summary>a row came into view: fetch the pictures it is missing.</summary>
    public void Show(GalleryRow<T> row)
    {
        var missing = row.Items.Where(t => !t.HasImage && t.Picture is not null).ToList();
        foreach (var tile in missing)
        {
            tile.Refresh();
        }

        var wanted = missing.Where(t => !t.HasImage && !t.Absent).ToList();
        if (wanted.Count > 0)
        {
            AppServices.Current.Fetcher.Request(wanted.Select(t => t.Picture!), () =>
            {
                foreach (var tile in wanted)
                {
                    tile.Refresh();
                }
            });
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        for (var i = 0; i < _items.Count; i += _columns)
        {
            Rows.Add(new GalleryRow<T>(_items.Skip(i).Take(_columns).ToList()));
        }
    }
}
