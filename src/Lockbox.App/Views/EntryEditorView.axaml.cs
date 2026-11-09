using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Lockbox.App.ViewModels;

namespace Lockbox.App.Views;

public partial class EntryEditorView : UserControl
{
    private EntryEditorViewModel? _vm;

    public EntryEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as EntryEditorViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmChanged;
                if (_vm.FocusTitleRequested) FocusTitle();
            }
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EntryEditorViewModel.FocusTitleRequested) && _vm?.FocusTitleRequested == true) FocusTitle();
    }

    private void FocusTitle() => Dispatcher.UIThread.Post(() =>
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
        if (_vm != null) _vm.FocusTitleRequested = false;
    }, DispatcherPriority.Input);
}
