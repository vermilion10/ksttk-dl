using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using KsttkDl.App.Infrastructure;

namespace KsttkDl.App.Views;

public partial class SyncView : UserControl
{
    public SyncView()
    {
        InitializeComponent();

        // keep the newest log line in view
        NotifyCollectionChangedEventHandler follow = (_, _) =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
        Loaded += (_, _) => AppServices.Current.Log.CollectionChanged += follow;
        Unloaded += (_, _) => AppServices.Current.Log.CollectionChanged -= follow;
    }
}
