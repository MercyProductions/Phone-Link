using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Contacts;

namespace PhoneLink.Windows.Pages;

public sealed partial class ContactsPage : Page, IDisposable
{
    private CancellationTokenSource? _searchCancellation;

    public ContactsPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<ContactListItem> Contacts { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadContactsAsync(SearchBox.Text, CancellationToken.None);
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, _searchCancellation.Token);
            await LoadContactsAsync(SearchBox.Text, _searchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        GC.SuppressFinalize(this);
    }

    private async Task LoadContactsAsync(string? search, CancellationToken cancellationToken)
    {
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            var synchronized = await AppServices.Current.ListContactsAsync(search, cancellationToken);
            Contacts.Clear();
            foreach (var contact in synchronized)
            {
                Contacts.Add(ContactListItem.FromContact(contact));
            }
            EmptyState.Visibility = Contacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContactsList.Visibility = Contacts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }
}

public sealed class ContactListItem
{
    public string DisplayName { get; set; } = string.Empty;

    public string Initials { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public static ContactListItem FromContact(SyncedContact contact)
    {
        var name = string.IsNullOrWhiteSpace(contact.DisplayName) ? "Unnamed contact" : contact.DisplayName;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
        var methods = contact.PhoneNumbers.Select(method => method.DisplayValue)
            .Concat(contact.EmailAddresses.Select(method => method.DisplayValue))
            .Take(3);
        var detail = string.Join("  •  ", methods);
        if (string.IsNullOrEmpty(detail))
        {
            detail = contact.OrganizationName;
        }
        return new ContactListItem
        {
            DisplayName = name,
            Initials = initials,
            Detail = detail,
        };
    }
}
