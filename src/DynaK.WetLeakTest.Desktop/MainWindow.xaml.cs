using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DynaK.WetLeakTest.Desktop;

public partial class MainWindow : Window
{
    public const string WindowTitle = "DynaK Wet Leak Test Station";

    private DesktopSettings? _settings;
    private BackendHost? _backend;
    private bool _webViewConfigured;
    private bool _initializing;
    private bool _closeApproved;
    private bool _closing;
    private string? _allowedOrigin;
    private static readonly HttpClient ExportClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeApplicationAsync();
        HmiWebView.PreviewDragOver += (_, eventArgs) => eventArgs.Handled = true;
        HmiWebView.PreviewDrop += (_, eventArgs) => eventArgs.Handled = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_backend is not null)
            {
                using var stateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var runtimeStatus = await _backend.GetRuntimeStatusAsync(stateTimeout.Token);
                if (!string.IsNullOrWhiteSpace(runtimeStatus) && !string.Equals(runtimeStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    var result = MessageBox.Show(
                        $"The station state is {runtimeStatus.ToUpperInvariant()}. Closing the HMI will not stop background acquisition.\n\nClose only the HMI?",
                        WindowTitle,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning,
                        MessageBoxResult.No);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backend.ShutdownOwnedIfStoppedAsync(shutdownTimeout.Token);
            }

            HmiWebView.Dispose();
            if (_backend is not null)
            {
                await _backend.DisposeAsync();
            }

            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            DesktopLog.Error("Desktop close handling failed; the backend was left running to protect station work.", ex);
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closing = false;
        }
    }

    private async Task InitializeApplicationAsync()
    {
        if (_initializing)
        {
            return;
        }

        _initializing = true;
        StartupActions.Visibility = Visibility.Collapsed;
        StartupDetail.Text = "";
        StartupOverlay.Visibility = Visibility.Visible;
        HmiWebView.Visibility = Visibility.Collapsed;

        try
        {
            SetStartupStatus("Validating configuration...");
            _settings ??= DesktopSettings.Load();
            _backend ??= new BackendHost(_settings);

            SetStartupStatus("Starting service...");
            await _backend.EnsureReadyAsync(SetStartupStatus, CancellationToken.None);

            SetStartupStatus("Loading HMI...");
            await InitializeWebViewAsync(_settings);

            SetStartupStatus("Starting READ-ONLY PLC polling...");
            await _backend.StartReadOnlyPollingAsync(CancellationToken.None);

            HmiWebView.Visibility = Visibility.Visible;
            StartupOverlay.Visibility = Visibility.Collapsed;
            DesktopLog.Information("DynaK HMI loaded in WebView2.");
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            ShowStartupError(
                "Microsoft WebView2 Runtime is not installed.",
                "Run the DynaK installer again or install the supported Microsoft WebView2 Runtime, then select Retry.",
                ex);
        }
        catch (TimeoutException ex)
        {
            ShowStartupError("DynaK service startup timed out.", ex.Message, ex);
        }
        catch (OperationCanceledException ex)
        {
            ShowStartupError("DynaK service startup timed out.", "Check the service and desktop logs, then select Retry.", ex);
        }
        catch (Exception ex)
        {
            ShowStartupError("DynaK service could not be started.", ex.Message, ex);
        }
        finally
        {
            _initializing = false;
        }
    }

    private async Task InitializeWebViewAsync(DesktopSettings settings)
    {
        var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        DesktopLog.Information($"WebView2 Runtime {runtimeVersion} detected.");

        var userDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynaK",
            "Wet Leak Test Station",
            "WebView2");
        Directory.CreateDirectory(userDataDirectory);

        if (HmiWebView.CoreWebView2 is null)
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDirectory);
            await HmiWebView.EnsureCoreWebView2Async(environment);
        }

        var core = HmiWebView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 initialization completed without a browser instance.");
        _allowedOrigin = settings.BackendUri.GetLeftPart(UriPartial.Authority);
        await ConfigureWebViewAsync();
        await core.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.DiskCache |
            CoreWebView2BrowsingDataKinds.CacheStorage |
            CoreWebView2BrowsingDataKinds.ServiceWorkers);
        DesktopLog.Information("Cleared cached HMI renderer data before navigation.");
        await NavigateToHmiAsync(settings.BackendUri);
    }

    private async Task ConfigureWebViewAsync()
    {
        if (_webViewConfigured)
        {
            return;
        }

        var core = HmiWebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.NavigationStarting += (_, eventArgs) =>
        {
            if (!IsAllowedNavigation(eventArgs.Uri))
            {
                eventArgs.Cancel = true;
                DesktopLog.Warning($"Blocked WebView navigation to {eventArgs.Uri}.");
            }
        };
        core.NewWindowRequested += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            DesktopLog.Warning($"Blocked WebView new-window request to {eventArgs.Uri}.");
        };
        core.DownloadStarting += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            DesktopLog.Warning("Blocked a WebView download request.");
        };
        core.PermissionRequested += (_, eventArgs) =>
        {
            eventArgs.State = CoreWebView2PermissionState.Deny;
        };
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += (_, eventArgs) =>
        {
            Dispatcher.Invoke(() => ShowStartupError(
                "The DynaK HMI browser process stopped unexpectedly.",
                $"WebView2 process failure: {eventArgs.ProcessFailedKind}. Select Retry to reload the HMI.",
                null));
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('dragover',e=>e.preventDefault());document.addEventListener('drop',e=>e.preventDefault());");
        _webViewConfigured = true;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (!IsAllowedNavigation(eventArgs.Source))
        {
            DesktopLog.Warning($"Blocked WebView message from {eventArgs.Source}.");
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            if (!message.RootElement.TryGetProperty("type", out var type))
            {
                return;
            }

            if (string.Equals(type.GetString(), "select-database", StringComparison.Ordinal))
            {
                SelectDatabase(message.RootElement);
                return;
            }

            if (string.Equals(type.GetString(), "export-history-excel", StringComparison.Ordinal))
            {
                await ExportHistoryExcelAsync(message.RootElement);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            DesktopLog.Warning("Could not process a WebView host request.", ex);
            PostHistoryExportResult(false, false, "Could not process the export request.", null);
        }
    }

    private void SelectDatabase(JsonElement message)
    {
        var currentPath = message.TryGetProperty("currentPath", out var current)
            ? current.GetString()
            : null;
        var dialog = new SaveFileDialog
        {
            Title = "Select DynaK station database",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            DefaultExt = ".db",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false,
            FileName = string.IsNullOrWhiteSpace(currentPath) ? "dynak-wet-leak-test.db" : Path.GetFileName(currentPath)
        };

        var currentDirectory = string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            HmiWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "database-selected",
                path = Path.GetFullPath(dialog.FileName)
            }));
        }
    }

    private async Task ExportHistoryExcelAsync(JsonElement message)
    {
        var settings = _settings ?? throw new InvalidOperationException("Backend settings are not loaded.");
        var suggestedFileName = message.TryGetProperty("suggestedFileName", out var suggested)
            ? SafeExportFileName(suggested.GetString())
            : $"DynaK_Part_History_{DateTime.Now:yyyy-MM-dd_HHmm}.xlsx";
        var query = message.TryGetProperty("query", out var queryElement)
            ? queryElement.GetString()
            : "";

        var dialog = new SaveFileDialog
        {
            Title = "Export DynaK part history",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = suggestedFileName
        };

        if (dialog.ShowDialog(this) != true)
        {
            PostHistoryExportResult(false, true, null, null);
            return;
        }

        try
        {
            var builder = new UriBuilder(settings.BackendUri)
            {
                Path = "api/records/export/excel",
                Query = query ?? ""
            };

            using var response = await ExportClient.GetAsync(builder.Uri);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            if (new FileInfo(dialog.FileName).Length != bytes.Length)
            {
                throw new IOException("The Excel report file was not written completely.");
            }

            int? recordCount = null;
            if (response.Headers.TryGetValues("X-DynaK-Record-Count", out var values) &&
                int.TryParse(values.FirstOrDefault(), out var parsed))
            {
                recordCount = parsed;
            }

            DesktopLog.Information($"Exported DynaK part history to {dialog.FileName}.");
            PostHistoryExportResult(true, false, null, recordCount);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            DesktopLog.Warning("DynaK part history Excel export failed.", ex);
            PostHistoryExportResult(false, false, $"Excel export failed: {ex.Message}", null);
        }
    }

    private static string SafeExportFileName(string? value)
    {
        var fileName = string.IsNullOrWhiteSpace(value)
            ? $"DynaK_Part_History_{DateTime.Now:yyyy-MM-dd_HHmm}.xlsx"
            : Path.GetFileName(value.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }

        return fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.xlsx";
    }

    private void PostHistoryExportResult(bool success, bool cancelled, string? message, int? recordCount)
    {
        HmiWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "history-export-result",
            success,
            cancelled,
            message,
            recordCount
        }));
    }

    private async Task NavigateToHmiAsync(Uri uri)
    {
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs) => completed.TrySetResult(eventArgs);

        HmiWebView.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            HmiWebView.CoreWebView2.Navigate(uri.AbsoluteUri);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"The local HMI could not be loaded ({result.WebErrorStatus}).");
            }
        }
        finally
        {
            HmiWebView.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private bool IsAllowedNavigation(string target)
    {
        if (string.Equals(target, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), _allowedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStartupStatus(string status)
    {
        Dispatcher.Invoke(() => StartupStatus.Text = status);
    }

    private void ShowStartupError(string title, string detail, Exception? exception)
    {
        DesktopLog.Error($"{title} {detail}", exception);
        HmiWebView.Visibility = Visibility.Collapsed;
        StartupOverlay.Visibility = Visibility.Visible;
        StartupStatus.Text = title;
        StartupDetail.Text = detail;
        StartupActions.Visibility = Visibility.Visible;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeApplicationAsync();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
