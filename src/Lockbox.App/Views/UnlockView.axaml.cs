using Avalonia.Controls;
using Lockbox.App.ViewModels;

namespace Lockbox.App.Views;

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is UnlockViewModel vm) vm.FocusPasswordRequested += (_, _) => PasswordBox.Focus();
        };
    }
}
