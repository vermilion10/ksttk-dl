using System.Windows.Input;
using KsttkDl.App.Infrastructure;
using KsttkDl.App.Theme;

namespace KsttkDl.App.ViewModels;

public enum Page
{
    Sync,
    Ships,
    Equipment,
    Furniture,
    Maps,
    Room,
    Settings,
}

public sealed class MainViewModel : Observable
{
    private readonly Dictionary<Page, object> _pages = [];
    private Page _page = Page.Ships;
    private object _current;

    public MainViewModel()
    {
        _current = Create(_page);
        Navigate = new RelayCommand(p =>
        {
            if (p is Page page || Enum.TryParse(p?.ToString(), out page))
            {
                Page = page;
            }
        });
        CycleTheme = new RelayCommand(() =>
        {
            var settings = Services.Settings;
            settings.Theme = ThemeManager.IsDark ? ThemeChoice.Light : ThemeChoice.Dark;
            settings.Save();
            ThemeManager.Apply(settings.Theme);
        });
    }

    public AppServices Services => AppServices.Current;

    public Page Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Current = _pages.TryGetValue(value, out var page) ? page : Create(value);
            }
        }
    }

    public object Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public ICommand Navigate { get; }

    public ICommand CycleTheme { get; }

    private object Create(Page page)
    {
        object created = page switch
        {
            Page.Sync => new SyncViewModel(),
            Page.Ships => new ShipsViewModel(this),
            Page.Equipment => new EquipmentViewModel(),
            Page.Furniture => new FurnitureViewModel(),
            Page.Maps => new MapsViewModel(),
            Page.Room => new RoomViewModel(),
            _ => new SettingsViewModel(),
        };
        _pages[page] = created;
        return created;
    }
}
