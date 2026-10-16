using System.Windows.Input;
using KsttkDl.App.Infrastructure;
using KsttkDl.App.Theme;
using KsttkDl.Core.Download;
using Microsoft.Win32;

namespace KsttkDl.App.ViewModels;

public sealed class SettingsViewModel : Observable
{
    private string _dataFolder = "";
    private string _outputFolder = "";
    private NetworkMode _network;
    private string _proxyUrl = "";
    private ThemeChoice _theme;
    private bool _includeRare;
    private int _concurrency;
    private string _proxyError = "";

    public SettingsViewModel()
    {
        Revert();
        Save = new RelayCommand(SaveSettings, () => _proxyError.Length == 0);
        Discard = new RelayCommand(Revert);
        BrowseData = new RelayCommand(() => DataFolder = Browse(DataFolder) ?? DataFolder);
        BrowseOutput = new RelayCommand(() => OutputFolder = Browse(OutputFolder) ?? OutputFolder);
        ForgetRelay = new RelayCommand(() =>
        {
            AppServices.Current.Tunnel?.ForgetRelay();
            AppServices.Current.Notify("Relay forgotten", "The next connection picks the best relay again.");
        });
    }

    public string DataFolder
    {
        get => _dataFolder;
        set => Set(ref _dataFolder, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => Set(ref _outputFolder, value);
    }

    public bool Direct
    {
        get => _network == NetworkMode.Direct;
        set => SetNetwork(value, NetworkMode.Direct);
    }

    public bool JpRelay
    {
        get => _network == NetworkMode.JpRelay;
        set => SetNetwork(value, NetworkMode.JpRelay);
    }

    public bool Proxy
    {
        get => _network == NetworkMode.Proxy;
        set => SetNetwork(value, NetworkMode.Proxy);
    }

    public string ProxyUrl
    {
        get => _proxyUrl;
        set
        {
            if (Set(ref _proxyUrl, value))
            {
                Validate();
            }
        }
    }

    public string ProxyError
    {
        get => _proxyError;
        private set => Set(ref _proxyError, value);
    }

    public bool ThemeSystem
    {
        get => _theme == ThemeChoice.System;
        set => SetTheme(value, ThemeChoice.System);
    }

    public bool ThemeLight
    {
        get => _theme == ThemeChoice.Light;
        set => SetTheme(value, ThemeChoice.Light);
    }

    public bool ThemeDark
    {
        get => _theme == ThemeChoice.Dark;
        set => SetTheme(value, ThemeChoice.Dark);
    }

    public bool IncludeRare
    {
        get => _includeRare;
        set => Set(ref _includeRare, value);
    }

    public int Concurrency
    {
        get => _concurrency;
        set => Set(ref _concurrency, Math.Clamp(value, 1, 32));
    }

    public ICommand Save { get; }

    public ICommand Discard { get; }

    public ICommand BrowseData { get; }

    public ICommand BrowseOutput { get; }

    public ICommand ForgetRelay { get; }

    private void SetNetwork(bool on, NetworkMode mode)
    {
        if (!on || _network == mode)
        {
            return;
        }

        _network = mode;
        Raise(nameof(Direct));
        Raise(nameof(JpRelay));
        Raise(nameof(Proxy));
        Validate();
    }

    private void SetTheme(bool on, ThemeChoice theme)
    {
        if (!on || _theme == theme)
        {
            return;
        }

        // previewed at once; Save keeps it
        _theme = theme;
        ThemeManager.Apply(theme);
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
    }

    private void Validate()
    {
        if (_network != NetworkMode.Proxy)
        {
            ProxyError = "";
            return;
        }

        try
        {
            CdnHttpClient.ParseProxy(_proxyUrl);
            ProxyError = "";
        }
        catch (ArgumentException ex)
        {
            ProxyError = _proxyUrl.Length == 0 ? "Enter a proxy address." : ex.Message;
        }
    }

    private void Revert()
    {
        var s = AppServices.Current.Settings;
        DataFolder = s.DataFolder;
        OutputFolder = s.OutputFolder;
        _network = s.Network;
        ProxyUrl = s.ProxyUrl;
        IncludeRare = s.IncludeRare;
        Concurrency = s.Concurrency;
        _theme = s.Theme;
        ThemeManager.Apply(_theme);
        foreach (var name in new[] { nameof(Direct), nameof(JpRelay), nameof(Proxy), nameof(ThemeSystem), nameof(ThemeLight), nameof(ThemeDark) })
        {
            Raise(name);
        }

        Validate();
    }

    private void SaveSettings()
    {
        var current = AppServices.Current.Settings;
        AppServices.Current.UpdateSettings(new AppSettings
        {
            DataFolder = DataFolder.Trim(),
            OutputFolder = OutputFolder.Trim(),
            World = current.World,
            Network = _network,
            ProxyUrl = ProxyUrl.Trim(),
            Theme = _theme,
            IncludeRare = IncludeRare,
            Concurrency = Concurrency,
        });
        AppServices.Current.Notify("Settings saved", "Your changes are in effect.", ToastKind.Success);
    }

    private static string? Browse(string current)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(current) ? current : null };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
