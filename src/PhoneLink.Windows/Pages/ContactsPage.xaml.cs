using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Contacts;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed partial class ContactsPage : Page, IDisposable
{
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _bluetoothCancellation;
    private bool _loaded;
    private bool _bluetoothExpanded;
    private ushort? _offset;

    public ContactsPage() => InitializeComponent();

    public ObservableCollection<ContactListItem> Contacts { get; } = [];
    public ObservableCollection<ClassicBluetoothPeer> BluetoothPeers { get; } = [];
    public ObservableCollection<BluetoothContactPreview> BluetoothContacts { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await RefreshSavedAsync(false);
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => await RefreshSavedAsync(true);

    private async Task RefreshSavedAsync(bool debounce)
    {
        if (!_loaded || App.IsLocked) return;
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _searchCancellation = cancellation;
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            if (debounce) await Task.Delay(250, cancellation.Token);
            var synchronized = await AppServices.Current.ListContactsAsync(SearchBox.Text, cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested) return;
            Contacts.Clear();
            foreach (var contact in synchronized) Contacts.Add(ContactListItem.FromContact(contact));
            EmptyState.Visibility = Contacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContactsList.Visibility = Contacts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && !cancellation.IsCancellationRequested)
            {
                ErrorBar.Message = "Windows could not read saved companion contacts.";
                ErrorBar.IsOpen = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                LoadingRing.IsActive = false;
            }
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) { _loaded = false; Dispose(); }

    public void Dispose()
    {
        ClearForPrivacy();
        GC.SuppressFinalize(this);
    }

    public void SetPrivacyLocked(bool locked)
    {
        if (locked) ClearForPrivacy();
        else if (_loaded)
        {
            _ = RefreshSavedAsync(false);
            if (_bluetoothExpanded) _ = ListBluetoothPeersAsync();
        }
    }

    private void ClearForPrivacy()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        Contacts.Clear();
        ContactsList.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        ClearBluetooth(clearPeers: true);
    }

    private async void BluetoothExpander_Expanding(object sender, ExpanderExpandingEventArgs e)
    {
        _bluetoothExpanded = true;
        await ListBluetoothPeersAsync();
    }

    private void BluetoothExpander_Collapsed(object sender, ExpanderCollapsedEventArgs e)
    {
        _bluetoothExpanded = false;
        ClearBluetooth(clearPeers: true);
    }

    private async Task ListBluetoothPeersAsync()
    {
        if (!_loaded || App.IsLocked || !_bluetoothExpanded || _bluetoothCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _bluetoothCancellation = cancellation;
        BluetoothStatus.Text = "Looking for paired Bluetooth devices…";
        try
        {
            var peers = await WindowsClassicPhoneProfiles.ListPairedAsync(cancellation.Token);
            if (!IsCurrentBluetoothOperation(cancellation)) return;
            foreach (var peer in peers) BluetoothPeers.Add(peer);
            BluetoothStatus.Text = peers.Count == 0 ? "No paired Classic Bluetooth devices were found." : "Select your iPhone. No contacts requested.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ReferenceEquals(_bluetoothCancellation, cancellation) && _loaded && !App.IsLocked)
                BluetoothStatus.Text = "Windows did not complete paired Bluetooth discovery. Collapse and reopen to retry.";
        }
        finally { FinishBluetoothOperation(cancellation); }
    }

    private void BluetoothPeerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_loaded) ClearBluetooth(clearPeers: false); }

    private async void BluetoothReadButton_Click(object sender, RoutedEventArgs e) => await ReadBluetoothAsync(0);
    private async void BluetoothPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offset is { } offset && offset >= WindowsClassicPhoneProfiles.PhoneBookPageSize)
            await ReadBluetoothAsync((ushort)(offset - WindowsClassicPhoneProfiles.PhoneBookPageSize));
    }
    private async void BluetoothNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offset is { } offset && offset <= ushort.MaxValue - WindowsClassicPhoneProfiles.PhoneBookPageSize)
            await ReadBluetoothAsync((ushort)(offset + WindowsClassicPhoneProfiles.PhoneBookPageSize));
    }

    private async Task ReadBluetoothAsync(ushort offset)
    {
        if (!_loaded || App.IsLocked || !_bluetoothExpanded || _bluetoothCancellation is not null ||
            BluetoothPeerBox.SelectedItem is not ClassicBluetoothPeer peer) return;
        using var cancellation = new CancellationTokenSource();
        _bluetoothCancellation = cancellation;
        BluetoothContacts.Clear();
        _offset = null;
        BluetoothStatus.Text = "Requesting the permitted phone-book page…";
        UpdateBluetoothButtons();
        try
        {
            var observation = await WindowsClassicPhoneProfiles.ReadPhoneBookAsync(peer, offset, cancellation.Token);
            if (!IsCurrentBluetoothOperation(cancellation)) return;
            BluetoothStatus.Text = observation.Status;
            if (!observation.Completed) return;
            _offset = offset;
            foreach (var contact in observation.Items) BluetoothContacts.Add(contact);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (IsCurrentBluetoothOperation(cancellation)) BluetoothStatus.Text = "Windows could not open the selected phone-book service.";
        }
        finally { FinishBluetoothOperation(cancellation); }
    }

    private bool IsCurrentBluetoothOperation(CancellationTokenSource operation) => _loaded && !App.IsLocked &&
        ReferenceEquals(_bluetoothCancellation, operation) && !operation.IsCancellationRequested;

    private void FinishBluetoothOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_bluetoothCancellation, operation)) return;
        _bluetoothCancellation = null;
        UpdateBluetoothButtons();
    }

    private void ClearBluetooth(bool clearPeers)
    {
        _bluetoothCancellation?.Cancel();
        _bluetoothCancellation = null;
        _offset = null;
        BluetoothContacts.Clear();
        if (clearPeers) BluetoothPeers.Clear();
        BluetoothStatus.Text = "No contacts requested.";
        UpdateBluetoothButtons();
    }

    private void UpdateBluetoothButtons()
    {
        var ready = _loaded && !App.IsLocked && _bluetoothExpanded && _bluetoothCancellation is null &&
            BluetoothPeerBox.SelectedItem is ClassicBluetoothPeer;
        BluetoothReadButton.IsEnabled = ready;
        BluetoothPreviousButton.IsEnabled = ready && _offset is > 0;
        BluetoothNextButton.IsEnabled = ready && _offset is <= ushort.MaxValue - WindowsClassicPhoneProfiles.PhoneBookPageSize &&
            BluetoothContacts.Count == WindowsClassicPhoneProfiles.PhoneBookPageSize;
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
