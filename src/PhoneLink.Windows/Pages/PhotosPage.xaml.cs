using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneLink.Application.Features.Attachments;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhoneLink.Windows.Pages;

public sealed partial class PhotosPage : Page
{
    public PhotosPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<ImportedPhotoListItem> Photos { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            var attachments = await AppServices.Current.ListAttachmentsAsync(CancellationToken.None);
            Photos.Clear();
            foreach (var attachment in attachments)
            {
                Photos.Add(ImportedPhotoListItem.FromAttachment(attachment));
            }
            EmptyState.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PhotosList.Visibility = Photos.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    private async void SaveCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Guid attachmentId } button)
        {
            return;
        }
        var photo = Photos.FirstOrDefault(item => item.AttachmentId == attachmentId);
        if (photo is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = ImportedPhotoListItem.GetSafeSuggestedName(photo.DisplayName),
        };
        picker.FileTypeChoices.Add("Image", [ImportedPhotoListItem.GetExtension(photo.MediaType)]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        button.IsEnabled = false;
        ErrorBar.IsOpen = false;
        try
        {
            using var transaction = await file.OpenTransactedWriteAsync();
            transaction.Stream.Size = 0;
            using (var stream = transaction.Stream.AsStreamForWrite())
            {
                await AppServices.Current.ExportAttachmentAsync(
                    attachmentId,
                    stream,
                    CancellationToken.None);
            }
            await transaction.CommitAsync();
        }
        catch (Exception exception)
        {
            ErrorBar.Title = "The photo could not be exported";
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}

public sealed class ImportedPhotoListItem
{
    public Guid AttachmentId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public static ImportedPhotoListItem FromAttachment(ImportedAttachment attachment) =>
        new()
        {
            AttachmentId = attachment.AttachmentId,
            DisplayName = attachment.DisplayName,
            MediaType = attachment.MediaType,
            Detail = $"{attachment.MediaType}  •  {FormatBytes(attachment.ByteCount)}  •  {attachment.VerifiedAt.LocalDateTime:g}",
        };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024d):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };

    public static string GetExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "image/webp" => ".webp",
        _ => ".img",
    };

    public static string GetSafeSuggestedName(string displayName)
    {
        var name = Path.GetFileNameWithoutExtension(displayName);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Imported photo" : safe;
    }
}
