using System.Collections.ObjectModel;
using System.Windows;
using AltManager.App.ViewModels;

namespace AltManager.App.Views;

public partial class ActivityWindow : Window
{
    public ActivityWindow(ObservableCollection<ActivityEntry> activity)
    {
        InitializeComponent();
        Items.ItemsSource = activity; // live view of the feed
    }
}
