// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PhoneLink.Windows.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
        HelloLockCheckBox.IsChecked = App.HelloLockEnabled;
        _initialized = true;
    }

    private async void HelloLockCheckBox_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        HelloLockCheckBox.IsEnabled = false;
        var requested = HelloLockCheckBox.IsChecked == true;
        var changed = await App.SetHelloLockEnabledAsync(requested);
        HelloLockCheckBox.IsChecked = App.HelloLockEnabled;
        HelloLockCheckBox.IsEnabled = true;
        HelloStatusBar.Severity = changed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        HelloStatusBar.Title = changed
            ? requested ? "Windows Hello lock enabled" : "Windows Hello lock disabled"
            : "Windows Hello was not enabled";
        HelloStatusBar.Message = changed
            ? requested
                ? "Phone Link will cover private content after five inactive minutes and require verification."
                : "The inactivity lock is off."
            : "Set up Windows Hello in Windows Settings, then try again.";
        HelloStatusBar.IsOpen = true;
    }
}
