using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Room;

namespace KsttkDl.App.ViewModels;

/// <summary>the editable fields of the port interface: its skin and what the header and clock say.</summary>
public sealed class PortUiEditor : Observable
{
    private PortSkin _skin = PortSkin.Skin1;
    private PortUiValues _values = new();

    /// <summary>raised after any field changes, so the layer can be redrawn.</summary>
    public event EventHandler? Changed;

    public static IReadOnlyList<string> Ranks => PortUi.Ranks;

    public PortSkin Skin
    {
        get => _skin;
        set
        {
            if (Set(ref _skin, value))
            {
                Raise(nameof(HasScrews));
                Raise(nameof(HasFleets));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>the classic skin shows a fleet count where the others show screws and equipment.</summary>
    public bool HasFleets => _skin == PortSkin.Classic;

    public bool HasScrews => _skin != PortSkin.Classic;

    public PortUiValues Values => _values;

    public PortUiSettings Settings => new() { Skin = _skin, Values = _values };

    public string Name { get => _values.Name; set => Update(_values with { Name = value }); }

    public string Level { get => _values.Level; set => Update(_values with { Level = value }); }

    public string Rank { get => _values.Rank; set => Update(_values with { Rank = value }); }

    public string Fleets { get => _values.Fleets; set => Update(_values with { Fleets = value }); }

    public int Ships { get => _values.Ships; set => Update(_values with { Ships = value }); }

    public int ShipMax { get => _values.ShipMax; set => Update(_values with { ShipMax = value }); }

    public int Equipment { get => _values.Equipment; set => Update(_values with { Equipment = value }); }

    public int EquipmentMax { get => _values.EquipmentMax; set => Update(_values with { EquipmentMax = value }); }

    public string Fuel { get => _values.Fuel; set => Update(_values with { Fuel = value }); }

    public string Ammo { get => _values.Ammo; set => Update(_values with { Ammo = value }); }

    public string Steel { get => _values.Steel; set => Update(_values with { Steel = value }); }

    public string Bauxite { get => _values.Bauxite; set => Update(_values with { Bauxite = value }); }

    public string Buckets { get => _values.Buckets; set => Update(_values with { Buckets = value }); }

    public string DevMaterials { get => _values.DevMaterials; set => Update(_values with { DevMaterials = value }); }

    public string Screws { get => _values.Screws; set => Update(_values with { Screws = value }); }

    public bool LiveClock { get => _values.LiveClock; set => Update(_values with { LiveClock = value }); }

    public string Date { get => _values.Date; set => Update(_values with { Date = value }); }

    public string Time { get => _values.Time; set => Update(_values with { Time = value }); }

    public bool ShowClock { get => _values.ShowClock; set => Update(_values with { ShowClock = value }); }

    public bool FriendFleetEvent { get => _values.FriendFleetEvent; set => Update(_values with { FriendFleetEvent = value }); }

    public void Apply(PortUiSettings? settings)
    {
        _skin = settings?.Skin ?? PortSkin.Skin1;
        _values = settings?.Values ?? new PortUiValues();
        Raise(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update(PortUiValues values)
    {
        if (values == _values)
        {
            return;
        }

        _values = values;
        Raise(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
