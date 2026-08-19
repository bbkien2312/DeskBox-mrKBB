using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView : UserControl
{
    private DesktopOrganizationPlan? _plan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _executionCts;
    private string? _lastHistoryId;
    private int _scanGeneration;
    private bool _isExecuting;
    private bool _closeAfterExecutionStops;

    public DesktopOrganizationTaskView()
    {
        InitializeComponent();
        ApplyStaticLocalization();
        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += TaskView_Unloaded;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? OrganizationCompleted;

    public event EventHandler? OrganizationUndone;

    public bool IsExecutionRunning => _isExecuting;

    public void BeginScan()
    {
        if (!_isExecuting)
        {
            _ = ScanAsync();
        }
    }

    public void CancelPendingWork()
    {
        _scanCts?.Cancel();
        _executionCts?.Cancel();
    }

    private void TaskView_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingWork();
        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnLanguageChanged);
            return;
        }

        ApplyStaticLocalization();
        if (_plan is not null)
        {
            UpdateSummary(_plan);
            RenderExcludedItems(_plan);
        }
    }

    public void CancelExecutionAndCloseWhenSafe()
    {
        if (!_isExecuting)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _closeAfterExecutionStops = true;
        _executionCts?.Cancel();
        ResultInfo.Severity = InfoBarSeverity.Warning;
        ResultInfo.Title = T("DesktopOrganization.Window.CloseBlocked");
        ResultInfo.Message = string.Empty;
        ResultInfo.IsOpen = true;
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        int generation = ++_scanGeneration;

        _plan = null;
        _lastHistoryId = null;
        UndoButton.Visibility = Visibility.Collapsed;
        DoneButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ExecuteButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        ResultInfo.IsOpen = false;
        ExecutionProgressPanel.Visibility = Visibility.Collapsed;
        ExecutionProgressBar.Value = 0;
        ExecutionProgressText.Text = string.Empty;
        PreviewContent.Opacity = 0.24;
        ScanBusyPanel.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        ExecuteButton.IsEnabled = false;
        ChangePathButton.IsEnabled = false;
        StoragePathText.Text = SettingsService.NormalizeManagedStorageRootPath(
            App.Current.SettingsService.Settings.DefaultManagedStorageRootPath);

        try
        {
            DesktopOrganizationPlan plan = await CreateCoordinator().BuildPlanAsync(
                includeSlowItems: false,
                includeManagedWidgetItems: IncludeManagedWidgetItemsCheckBox.IsChecked == true,
                cancellationToken: cts.Token);
            if (cts.IsCancellationRequested || generation != _scanGeneration)
            {
                return;
            }

            _plan = plan;
            RenderPlan(plan);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _scanGeneration)
            {
                return;
            }

            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
            SummaryTitle.Text = T("DesktopOrganization.Result.FailedTitle");
            SummaryDescription.Text = T("DesktopOrganization.Preview.PageDescription");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts) && generation == _scanGeneration)
            {
                PreviewContent.Opacity = 1;
                ScanBusyPanel.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                ChangePathButton.IsEnabled = true;
            }
        }
    }

    private static DesktopOrganizationCoordinator CreateCoordinator()
    {
        App app = App.Current;
        if (app.WidgetManager is null)
        {
            throw new InvalidOperationException("Widget manager is not available.");
        }

        return new DesktopOrganizationCoordinator(
            app.SettingsService,
            app.FileService,
            app.WidgetManager,
            app.OrganizerService,
            app.LocalizationService);
    }

    private static string T(string key) => App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        App.Current.LocalizationService.Format(key, values);

    private void ApplyStaticLocalization()
    {
        PathTitleText.Text = T("DesktopOrganization.Path.Title");
        StorageSectionTitleText.Text = T("DesktopOrganization.Path.SectionTitle");
        StorageSectionDescriptionText.Text = T("DesktopOrganization.Path.NewTargetsDescription");
        ChangePathButton.Content = T("Common.Change");
        ScanBusyText.Text = T("DesktopOrganization.Preview.Scanning");
        RefreshButton.Content = T("DesktopOrganization.Preview.Refresh");
        UndoButton.Content = T("DesktopOrganization.Notification.Undo");
        CancelButton.Content = T("DesktopOrganization.Window.Cancel");
        DoneButton.Content = T("DesktopOrganization.Window.Done");
    }
}
