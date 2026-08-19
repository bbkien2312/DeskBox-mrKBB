using System.Runtime.InteropServices;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly WidgetManager _widgetManager;
    private readonly OrganizerService _organizerService;
    private readonly LocalizationService _localizationService;
    private readonly DesktopOrganizationScanner _scanner;
    private readonly DesktopOrganizationPlanner _planner;
    private readonly DesktopOrganizationPlacementPlanner _placementPlanner = new();
    private readonly DesktopOrganizationTransaction _transaction;
    private readonly DesktopOrganizationLogService _log;

    public DesktopOrganizationCoordinator(
        SettingsService settingsService,
        FileService fileService,
        WidgetManager widgetManager,
        OrganizerService organizerService,
        LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _widgetManager = widgetManager;
        _organizerService = organizerService;
        _localizationService = localizationService;
        _log = new DesktopOrganizationLogService();
        var classifier = new DesktopOrganizationClassifier();
        _scanner = new DesktopOrganizationScanner(classifier);
        _planner = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver());
        _transaction = new DesktopOrganizationTransaction(settingsService, fileService, log: _log);
    }

    public async Task<DesktopOrganizationPlan> BuildPlanAsync(
        bool includeSlowItems = false,
        bool includeManagedWidgetItems = false,
        CancellationToken cancellationToken = default)
    {
        _log.Info(
            "ScanStarted",
            $"includeSlowItems={includeSlowItems}; includeManagedWidgetItems={includeManagedWidgetItems}");
        string[] managedRoots = includeManagedWidgetItems
            ? _settingsService.Settings.Widgets
                .Where(widget => widget.WidgetKind == WidgetKind.File &&
                                 !widget.IsDisabled &&
                                 !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
                .Select(widget => widget.MappedFolderPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        DesktopOrganizationScanResult scan =
            await _scanner.ScanAsync(
                includeSlowItems,
                cancellationToken,
                managedRoots,
                includePublicDesktopItems: true,
                includeFolders: true);
        string root = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        DesktopOrganizationPlan plan = _planner.CreatePlan(
            scan,
            root,
            _settingsService.Settings.Widgets,
            _settingsService.Settings.DesktopOrganizationRules,
            ResolveCategoryName);

        AssignNonOverlappingBounds(plan);
        _log.Ok(
            "ScanCompleted",
            $"found={scan.TotalCount}; eligible={scan.EligibleCount}; targets={plan.Targets.Count}; newTargets={plan.NewWidgetCount}");
        return plan;
    }

    /// <summary>
    /// Compiles the user's preview selections into an immutable execution
    /// plan. The scan plan is never mutated, so changing a combo box cannot
    /// leak into a later refresh or into another execution attempt.
    /// </summary>
    public DesktopOrganizationPlan CreateExecutionPlan(
        DesktopOrganizationPlan previewPlan,
        IReadOnlyCollection<DesktopOrganizationTargetSelection> selections)
    {
        var selectionByBucket = selections
            .Where(selection => !string.IsNullOrWhiteSpace(selection.SourceBucketId))
            .ToDictionary(selection => selection.SourceBucketId, StringComparer.Ordinal);
        var widgetsById = _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .ToDictionary(widget => widget.Id, StringComparer.Ordinal);
        var targetsByDestination = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.Ordinal);

        foreach (DesktopOrganizationTargetPlan source in previewPlan.Targets)
        {
            if (selectionByBucket.TryGetValue(source.SourceBucketId, out DesktopOrganizationTargetSelection? selection) &&
                !selection.IsSelected)
            {
                continue;
            }

            DesktopOrganizationTargetPlan target = source;
            bool shouldResolveExistingDestination =
                selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget ||
                !source.CreatesWidget;
            if (shouldResolveExistingDestination)
            {
                string? requestedWidgetId = selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                    ? selection.ExistingWidgetId
                    : source.TargetWidgetId;
                if (string.IsNullOrWhiteSpace(requestedWidgetId) ||
                    !widgetsById.TryGetValue(requestedWidgetId, out WidgetConfig? widget) ||
                    string.IsNullOrWhiteSpace(widget.MappedFolderPath))
                {
                    throw new InvalidOperationException(
                        _localizationService.T("DesktopOrganization.Error.TargetUnavailable"));
                }

                target = source.CloneWith(
                    widget.Id,
                    widget.Name,
                    Path.GetFullPath(widget.MappedFolderPath),
                    createsWidget: false,
                    source.Items);
            }

            if (targetsByDestination.TryGetValue(target.TargetWidgetId, out DesktopOrganizationTargetPlan? merged))
            {
                targetsByDestination[target.TargetWidgetId] = merged.CloneWith(
                    merged.TargetWidgetId,
                    merged.SuggestedDisplayName,
                    merged.TargetDirectoryPath,
                    merged.CreatesWidget,
                    merged.Items.Concat(target.Items));
            }
            else
            {
                targetsByDestination.Add(target.TargetWidgetId, target);
            }
        }

        var executionPlan = new DesktopOrganizationPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            DesktopPath = previewPlan.DesktopPath,
            StorageRootPath = previewPlan.StorageRootPath,
            Targets = targetsByDestination.Values
                .Where(target => target.Items.Count > 0)
                .ToList(),
            ExcludedItems = previewPlan.ExcludedItems.ToList()
        };

        AssignNonOverlappingBounds(executionPlan);
        return executionPlan;
    }

    public IReadOnlyList<DesktopOrganizationDestinationOption> GetDestinationOptions()
    {
        return _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(widget => new DesktopOrganizationDestinationOption(
                widget.Id,
                widget.Name,
                Path.GetFullPath(widget.MappedFolderPath!),
                IsDynamic: false))
            .ToList();
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, progress: null, cancellationToken);
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        IProgress<DesktopOrganizationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        string[] existingTargetIds = plan.Targets
            .Where(target => !target.CreatesWidget)
            .Select(target => target.TargetWidgetId)
            .ToArray();
        _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: true);
        DesktopOrganizationExecutionResult result;
        try
        {
            _log.Info(
                "CoordinatorExecuteStarted",
                $"items={plan.EligibleItemCount}; targets={plan.Targets.Count}");
            result = await _transaction.ExecuteAsync(plan, progress, cancellationToken);
        }
        finally
        {
            _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: false);
        }

        var shownWidgetIds = new List<string>();
        try
        {
            foreach (WidgetConfig widget in result.CreatedWidgets)
            {
                await _widgetManager.ShowWidgetAsync(widget.Id, reveal: true, autoRestoreOnReveal: false);
                shownWidgetIds.Add(widget.Id);
            }

            foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => !target.CreatesWidget))
            {
                await _widgetManager.RefreshFileWidgetAsync(target.TargetWidgetId);
            }

            return result;
        }
        catch
        {
            _log.Error("CoordinatorRefreshFailed", "Widget refresh/show failed after file transfer; undo was requested.");
            foreach (string widgetId in shownWidgetIds)
            {
                await _widgetManager.RemoveWidgetAsync(widgetId, WidgetRemovalAction.RemoveWidgetOnly);
            }

            await _organizerService.UndoAsync(result.History.Id);
            _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                result.CreatedWidgets.Any(widget =>
                    string.Equals(widget.Id, rule.TargetWidgetId, StringComparison.Ordinal)));
            _settingsService.Settings.Widgets.RemoveAll(widget =>
                result.CreatedWidgets.Any(created =>
                    string.Equals(created.Id, widget.Id, StringComparison.Ordinal)));
            await _settingsService.SaveAsync(notifySubscribers: false);
            throw;
        }
    }

    public Task<int> RecoverPendingAsync() => _transaction.RecoverPendingAsync();

    public async Task UndoAsync(string historyId)
    {
        OrganizationHistoryEntry? history = _settingsService.Settings.RecentOrganizationHistory
            .FirstOrDefault(entry =>
                string.Equals(entry.Id, historyId, StringComparison.Ordinal));
        if (history is null)
        {
            throw new InvalidOperationException("The organization history entry no longer exists.");
        }

        await _organizerService.UndoAsync(historyId);
        foreach (OrganizationHistoryTarget target in history.Targets)
        {
            if (target.WasCreated)
            {
                await _widgetManager.RemoveWidgetAsync(
                    target.WidgetId,
                    WidgetRemovalAction.RemoveWidgetOnly);
                _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                    string.Equals(
                        rule.TargetWidgetId,
                        target.WidgetId,
                        StringComparison.Ordinal));
                TryDeleteEmptyDirectory(target.DirectoryPath);
            }
            else
            {
                await _widgetManager.RefreshFileWidgetAsync(target.WidgetId);
            }
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    private string ResolveCategoryName(string categoryId)
    {
        string key = $"DesktopOrganization.Category.{categoryId}";
        string localized = _localizationService.T(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? categoryId
            : localized;
    }

    private void AssignNonOverlappingBounds(DesktopOrganizationPlan plan)
    {
        if (plan.NewWidgetCount == 0)
        {
            return;
        }

        NativeRect nativeWorkArea = default;
        if (!SystemParametersInfo(SpiGetWorkArea, 0, ref nativeWorkArea, 0))
        {
            return;
        }

        double scale = Math.Max(1, GetDpiForSystem() / 96d);
        var workArea = new DesktopOrganizationRect(
            nativeWorkArea.Left,
            nativeWorkArea.Top,
            nativeWorkArea.Right - nativeWorkArea.Left,
            nativeWorkArea.Bottom - nativeWorkArea.Top);
        var occupied = _settingsService.Settings.Widgets
            .Where(widget => widget.IsVisible && !widget.IsDisabled)
            .Select(widget => new DesktopOrganizationRect(
                widget.X,
                widget.Y,
                widget.Width * scale,
                widget.Height * scale))
            .ToList();

        if (!_placementPlanner.TryAssignBounds(
                plan,
                workArea,
                occupied,
                _settingsService.Settings.DefaultWidgetWidth * scale,
                _settingsService.Settings.DefaultWidgetHeight * scale,
                DesktopOrganizationPlacementPlanner.DefaultEdgeMargin * scale,
                DesktopOrganizationPlacementPlanner.DefaultGap * scale))
        {
            throw new InvalidOperationException(
                _localizationService.T("DesktopOrganization.Error.NoLayoutSpace"));
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private const uint SpiGetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeRect value,
        uint update);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
