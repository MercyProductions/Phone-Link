using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneLink.Application.Features.Attachments;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PhoneLink.Windows.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the page. Unload, privacy lock and preview close release the player, source and stream.")]
public sealed partial class PhotosPage : Page
{
    private const int MaximumPreviewBytes = 25 * 1024 * 1024;
    private const uint MaximumPreviewEdge = 1600;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _previewCancellation;
    private SoftwareBitmap? _previewBitmap;
    private InMemoryRandomAccessStream? _videoStream;
    private MediaSource? _videoSource;
    private MediaPlayer? _videoPlayer;
    private bool _loaded;

    public PhotosPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<ImportedPhotoListItem> Photos { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await ReloadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        ClearForPrivacy();
    }

    public void SetPrivacyLocked(bool locked)
    {
        if (locked) ClearForPrivacy();
        else if (_loaded) _ = ReloadAsync();
    }

    private void ClearForPrivacy()
    {
        _listCancellation?.Cancel();
        _listCancellation = null;
        ClearPreview();
        Photos.Clear();
        EmptyState.Visibility = PhotosList.Visibility = Visibility.Collapsed;
        ErrorBar.IsOpen = LoadingRing.IsActive = false;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        if (!_loaded || App.IsLocked || _listCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _listCancellation = cancellation;
        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        try
        {
            var attachments = await AppServices.Current.ListAttachmentsAsync(cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested) return;
            Photos.Clear();
            foreach (var attachment in attachments)
            {
                Photos.Add(ImportedPhotoListItem.FromAttachment(attachment));
            }
            EmptyState.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PhotosList.Visibility = Photos.Count == 0 || PreviewPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ReferenceEquals(_listCancellation, cancellation) && _loaded && !App.IsLocked)
            {
                ErrorBar.Title = "Photos could not be loaded";
                ErrorBar.Message = "Try refreshing the list again.";
                ErrorBar.IsOpen = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_listCancellation, cancellation))
            {
                _listCancellation = null;
                RefreshButton.IsEnabled = true;
                LoadingRing.IsActive = false;
            }
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || _previewCancellation is not null ||
            sender is not Button { CommandParameter: Guid attachmentId }) return;
        var photo = Photos.FirstOrDefault(item => item.AttachmentId == attachmentId);
        if (photo is null) return;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _previewCancellation = cancellation;
        PreviewPanel.Visibility = Visibility.Visible;
        PhotosList.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewStatus.Text = "Verifying the selected file…";
        ClosePreviewButton.Focus(FocusState.Programmatic);
        InMemoryRandomAccessStream? stream = null;
        SoftwareBitmap? bitmap = null;
        SoftwareBitmapSource? source = null;
        try
        {
            if (photo.ByteCount is <= 0 or > MaximumPreviewBytes) throw new InvalidDataException("Preview input is oversized.");
            stream = new InMemoryRandomAccessStream();
            using (var plaintext = stream.GetOutputStreamAt(0).AsStreamForWrite())
                await AppServices.Current.ExportAttachmentAsync(attachmentId, plaintext, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (stream.Size != (ulong)photo.ByteCount) throw new InvalidDataException("Preview input length changed.");
            stream.Seek(0);
            if (photo.IsVideo)
            {
                if (!_loaded || App.IsLocked || PreviewPanel.Visibility != Visibility.Visible) return;
                _videoStream = stream;
                stream = null;
                _videoSource = MediaSource.CreateFromStream(_videoStream, photo.MediaType);
                _videoPlayer = new MediaPlayer { AutoPlay = false };
                _videoPlayer.CommandManager.IsEnabled = false;
                _videoPlayer.MediaFailed += PreviewVideo_MediaFailed;
                PreviewVideo.SetMediaPlayer(_videoPlayer);
                PreviewVideo.Source = _videoSource;
                PreviewVideoPanel.Visibility = Visibility.Visible;
                PreviewStatus.Text = $"{photo.DisplayName} — press Play to watch.";
                return;
            }
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation.Token);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width is 0 or > 16_384 || height is 0 or > 16_384 || (ulong)width * height > 100_000_000)
                throw new InvalidDataException("Preview dimensions are oversized.");
            var scale = Math.Min(1d, MaximumPreviewEdge / (double)Math.Max(width, height));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1u, (uint)(width * scale)),
                ScaledHeight = Math.Max(1u, (uint)(height * scale)),
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellation.Token);
            source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap).AsTask(cancellation.Token);
            if (!_loaded || App.IsLocked || cancellation.IsCancellationRequested || PreviewPanel.Visibility != Visibility.Visible) return;
            PreviewImage.Source = source;
            _previewBitmap = bitmap;
            source = null;
            bitmap = null;
            PreviewStatus.Text = photo.DisplayName;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_loaded && !App.IsLocked && PreviewPanel.Visibility == Visibility.Visible)
            {
                ClearVideo();
                PreviewStatus.Text = "Preview unavailable. The file could not be verified or decoded within the preview limits, or Windows is missing the required codec.";
            }
        }
        finally
        {
            stream?.Dispose();
            source?.Dispose();
            bitmap?.Dispose();
            _previewCancellation = null;
        }
    }

    private void ClosePreviewButton_Click(object sender, RoutedEventArgs e) => ClearPreview();

    private void PlayPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded && !App.IsLocked) _videoPlayer?.Play();
    }

    private void PausePreviewButton_Click(object sender, RoutedEventArgs e) => _videoPlayer?.Pause();

    private void ClearPreview()
    {
        _previewCancellation?.Cancel();
        var source = PreviewImage.Source as SoftwareBitmapSource;
        PreviewImage.Source = null;
        source?.Dispose();
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        ClearVideo();
        PreviewStatus.Text = string.Empty;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PhotosList.Visibility = Photos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviewVideo_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_videoPlayer, sender) || !_loaded || App.IsLocked) return;
            ClearVideo();
            PreviewStatus.Text = "Windows could not play this clip. The video or audio codec may not be installed.";
        });

    private void ClearVideo()
    {
        if (_videoPlayer is { } player)
        {
            _videoPlayer = null;
            player.MediaFailed -= PreviewVideo_MediaFailed;
            PreviewVideo.Source = null;
            PreviewVideo.SetMediaPlayer(null);
            player.Dispose();
        }
        PreviewVideoPanel.Visibility = Visibility.Collapsed;
        _videoSource?.Dispose();
        _videoSource = null;
        _videoStream?.Dispose();
        _videoStream = null;
    }

    private async void SaveCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || App.IsLocked || sender is not Button { CommandParameter: Guid attachmentId } button)
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
        picker.FileTypeChoices.Add(photo.IsVideo ? "Video" : "Image", [ImportedPhotoListItem.GetExtension(photo.MediaType)]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null || !_loaded || App.IsLocked)
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
    public long ByteCount { get; set; }
    public bool IsVideo => MediaType is "video/mp4" or "video/quicktime";

    public static ImportedPhotoListItem FromAttachment(ImportedAttachment attachment) =>
        new()
        {
            AttachmentId = attachment.AttachmentId,
            DisplayName = attachment.DisplayName,
            MediaType = attachment.MediaType.ToLowerInvariant(),
            ByteCount = attachment.ByteCount,
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
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
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
