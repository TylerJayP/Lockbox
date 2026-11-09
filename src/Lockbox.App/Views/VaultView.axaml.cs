using Avalonia.Controls;
using Avalonia.Threading;
using Lockbox.App.ViewModels;

namespace Lockbox.App.Views;

public partial class VaultView : UserControl
{
    private VaultViewModel? _vm;

    public VaultView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.FocusSearchRequested -= OnFocusSearch;
            _vm = DataContext as VaultViewModel;
            if (_vm != null) _vm.FocusSearchRequested += OnFocusSearch;
        };
    }

    private void OnFocusSearch(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Input);
}
