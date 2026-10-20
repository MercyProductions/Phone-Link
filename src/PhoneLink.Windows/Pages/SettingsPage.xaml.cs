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
        try
        {
            var changed = await App.SetHelloLockEnabledAsync(requested);
            HelloStatusBar.Severity = changed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            HelloStatusBar.Title = changed
                ? requested ? "Windows Hello lock enabled" : "Windows Hello lock disabled"
                : "Windows Hello setting was not changed";
            HelloStatusBar.Message = changed
                ? requested
                    ? "Phone Link locks at startup, after five minutes without interaction, and when Windows locks or suspends."
                    : "The inactivity lock is off."
                : "Windows Hello verification must succeed before this protection can be enabled.";
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Windows Hello setting failed: {exception.HResult:X8}");
            HelloStatusBar.Severity = InfoBarSeverity.Error;
            HelloStatusBar.Title = "Windows Hello setting was not changed";
            HelloStatusBar.Message = "Windows could not verify or save this setting. You can try again.";
        }
        finally
        {
            HelloLockCheckBox.IsChecked = App.HelloLockEnabled;
            HelloLockCheckBox.IsEnabled = true;
            HelloStatusBar.IsOpen = true;
        }
    }
}
