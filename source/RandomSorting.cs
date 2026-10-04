using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace RandomSorting
{
    public class RandomSorting : GenericPlugin
    {
        private const int MinDatabaseUpdateBatchSize = 10;
        private const int MaxDatabaseUpdateBatchSize = 5000;
        private const int MinUiBatchDelayMs = 0;
        private const int MaxUiBatchDelayMs = 1000;
        private const int MinMaxAutomaticCleanupLabels = 0;
        private const int MaxMaxAutomaticCleanupLabels = 1000000;

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly object runLock = new object();
        private RandomSortingSettingsViewModel settings { get; set; }
        private CancellationTokenSource activeRunCancellation;
        private PendingRun pendingRun;
        private PendingRun queuedRun;
        private bool hasQueuedRun;
        private bool manualOperationActive;
        private Task workerTask;
        private int nextRunId;

        public override Guid Id { get; } = Guid.Parse("a6af1255-7acc-4b6d-99f1-661065beb1b9");

        public RandomSorting(IPlayniteAPI api) : base(api)
        {
            settings = new RandomSortingSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (settings.Settings.UpdateOnGameStart)
            {
                RequestRun("game-start", true);
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (settings.Settings.UpdateOnStartup)
            {
                RequestRun("startup", true);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            lock (runLock)
            {
                pendingRun = null;
                queuedRun = null;
                hasQueuedRun = false;
                activeRunCancellation?.Cancel();
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new RandomSortingSettingsView();
        }

        public void AssignRandomIdentifiers(LabelType labelType, CancellationToken? token = null)
        {
            RequestRun("manual", false, labelType);
        }

        public void AssignRandomIdentifiersWithProgress(LabelType labelType)
        {
            var options = new GlobalProgressOptions("RandomSorting - Assign Random Identifiers", true)
            {
                IsIndeterminate = true
            };

            try
            {
                var result = PlayniteApi.Dialogs.ActivateGlobalProgress(async progressArgs =>
                {
                    var progress = new ProgressReporter(progressArgs);
                    await PrepareManualOperationAsync(progress, progressArgs.CancelToken).ConfigureAwait(false);

                    try
                    {
                        var snapshot = CreateSettingsSnapshot(labelType);
                        var request = new PendingRun("manual", false, snapshot);
                        var runId = Interlocked.Increment(ref nextRunId);
                        await ExecuteRandomizationAsync(request, runId, progressArgs.CancelToken, progress).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndManualOperation();
                    }
                }, options);

                if (result.Canceled)
                {
                    logger.Info("RandomSorting manual assignment was cancelled.");
                }
                else if (result.Error != null)
                {
                    throw result.Error;
                }
            }
            catch (OperationCanceledException)
            {
                logger.Info("RandomSorting manual assignment was cancelled.");
            }
            catch (Exception ex)
            {
                logger.Error($"RandomSorting manual assignment failed: {ex}");
                PlayniteApi.Dialogs.ShowErrorMessage($"RandomSorting manual assignment failed:\n\n{ex.Message}", "RandomSorting");
            }
        }

        public void CleanupRandomLabelsWithProgress(LabelType labelType)
        {
            var options = new GlobalProgressOptions("RandomSorting - Clean Up Random Labels", true)
            {
                IsIndeterminate = true
            };

            try
            {
                var result = PlayniteApi.Dialogs.ActivateGlobalProgress(async progressArgs =>
                {
                    var progress = new ProgressReporter(progressArgs);
                    await PrepareManualOperationAsync(progress, progressArgs.CancelToken).ConfigureAwait(false);

                    try
                    {
                        var snapshot = CreateSettingsSnapshot(labelType);
                        var runId = Interlocked.Increment(ref nextRunId);
                        await ExecuteCleanupAsync(snapshot, runId, progressArgs.CancelToken, progress).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndManualOperation();
                    }
                }, options);

                if (result.Canceled)
                {
                    logger.Info("RandomSorting manual cleanup was cancelled.");
                }
                else if (result.Error != null)
                {
                    throw result.Error;
                }
            }
            catch (OperationCanceledException)
            {
                logger.Info("RandomSorting manual cleanup was cancelled.");
            }
            catch (Exception ex)
            {
                logger.Error($"RandomSorting manual cleanup failed: {ex}");
                PlayniteApi.Dialogs.ShowErrorMessage($"RandomSorting cleanup failed:\n\n{ex.Message}", "RandomSorting");
            }
        }

        public List<FeatureFilterOption> GetFeatureFilterOptions()
        {
            var options = new List<FeatureFilterOption>
            {
                new FeatureFilterOption { Id = null, Name = "All eligible games" }
            };

            try
            {
                var features = PlayniteApi.Database.Features
                    .ToList()
                    .OrderBy(feature => feature.Name)
                    .Select(feature => new FeatureFilterOption { Id = feature.Id, Name = feature.Name })
                    .ToList();

                options.AddRange(features);

                var selectedFeatureId = settings?.Settings?.RequiredFeatureId;
                if (selectedFeatureId.HasValue && options.All(option => option.Id != selectedFeatureId))
                {
                    options.Add(new FeatureFilterOption
                    {
                        Id = selectedFeatureId,
                        Name = "Selected feature not found"
                    });
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to load feature filter options: {ex.Message}");
            }

            return options;
        }

        private void RequestRun(string trigger, bool automatic, LabelType? labelTypeOverride = null)
        {
            var snapshot = CreateSettingsSnapshot(labelTypeOverride);
            var behavior = automatic ? snapshot.AutomaticRunBehavior : AutomaticRunBehavior.CancelCurrentAndRestart;

            lock (runLock)
            {
                if (manualOperationActive)
                {
                    logger.Info($"RandomSorting trigger '{trigger}' skipped because a manual operation is active.");
                    return;
                }

                var workerIsRunning = workerTask != null && !workerTask.IsCompleted;

                if (workerIsRunning)
                {
                    if (behavior == AutomaticRunBehavior.SkipIfRunning)
                    {
                        logger.Info($"RandomSorting trigger '{trigger}' skipped because a run is already active.");
                        return;
                    }

                    if (behavior == AutomaticRunBehavior.QueueOneRerunAfterCurrent)
                    {
                        queuedRun = new PendingRun(trigger, automatic, snapshot);
                        hasQueuedRun = true;
                        logger.Info($"RandomSorting trigger '{trigger}' queued to run once after the active run finishes.");
                        return;
                    }

                    activeRunCancellation?.Cancel();
                    pendingRun = new PendingRun(trigger, automatic, snapshot);
                    logger.Info($"RandomSorting trigger '{trigger}' requested cancel/restart of the active run.");
                    return;
                }

                pendingRun = new PendingRun(trigger, automatic, snapshot);
                workerTask = Task.Run(() => RunWorkerAsync());
            }
        }

        private async Task RunWorkerAsync()
        {
            while (true)
            {
                PendingRun request;
                CancellationTokenSource runCancellation;

                lock (runLock)
                {
                    if (pendingRun != null)
                    {
                        request = pendingRun;
                        pendingRun = null;
                    }
                    else if (hasQueuedRun)
                    {
                        request = queuedRun;
                        queuedRun = null;
                        hasQueuedRun = false;
                    }
                    else
                    {
                        workerTask = null;
                        return;
                    }

                    runCancellation = new CancellationTokenSource();
                    activeRunCancellation = runCancellation;
                }

                var runId = Interlocked.Increment(ref nextRunId);
                try
                {
                    await ExecuteRandomizationAsync(request, runId, runCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.Info($"RandomSorting run {runId} was cancelled.");
                }
                catch (Exception ex)
                {
                    logger.Error($"RandomSorting run {runId} failed: {ex}");
                }
                finally
                {
                    lock (runLock)
                    {
                        if (ReferenceEquals(activeRunCancellation, runCancellation))
                        {
                            activeRunCancellation = null;
                        }
                    }

                    runCancellation.Dispose();
                }
            }
        }

        private async Task ExecuteRandomizationAsync(PendingRun request, int runId, CancellationToken token, ProgressReporter progress = null)
        {
            var stopwatch = Stopwatch.StartNew();
            var settingsSnapshot = request.Settings;

            logger.Info($"RandomSorting run {runId} started. Trigger={request.Trigger}, LabelType={settingsSnapshot.SelectedLabelType}, " +
                $"RunBehavior={settingsSnapshot.AutomaticRunBehavior}, CollisionFreeLabels={settingsSnapshot.UseCollisionFreeLabels}, " +
                $"Cleanup={settingsSnapshot.CleanupOrphanedRandomLabels}, CleanupOnAutomaticRuns={settingsSnapshot.CleanupOnAutomaticRuns}, " +
                $"IncludeHiddenGames={settingsSnapshot.IncludeHiddenGames}, IncludeUninstalledGames={settingsSnapshot.IncludeUninstalledGames}, " +
                $"MaxAutomaticCleanupLabels={settingsSnapshot.MaxAutomaticCleanupLabels}, ManualBatchSize={settingsSnapshot.DatabaseUpdateBatchSize}, " +
                $"AutomaticBatchSize={settingsSnapshot.AutomaticDatabaseUpdateBatchSize}, EffectiveBatchSize={GetEffectiveBatchSize(settingsSnapshot, request.Automatic)}, " +
                $"UiBatchDelayMs={settingsSnapshot.UiBatchDelayMs}, " +
                $"FeatureFilter={settingsSnapshot.RequiredFeatureId?.ToString() ?? "none"}.");

            var cleanupEnabledForRun = settingsSnapshot.CleanupOrphanedRandomLabels &&
                (!request.Automatic || settingsSnapshot.CleanupOnAutomaticRuns);

            if (settingsSnapshot.CleanupOrphanedRandomLabels && request.Automatic && !settingsSnapshot.CleanupOnAutomaticRuns)
            {
                logger.Info($"RandomSorting run {runId}: cleanup skipped for automatic trigger '{request.Trigger}'.");
            }

            progress?.SetIndeterminate("Capturing Playnite database snapshot...");
            var databaseSnapshot = CaptureDatabaseSnapshot(settingsSnapshot, token);
            logger.Info($"RandomSorting run {runId}: database snapshot captured in {stopwatch.Elapsed}. " +
                $"Games={databaseSnapshot.Games.Count}, Labels={databaseSnapshot.Labels.Count}.");

            progress?.SetIndeterminate("Planning random assignments...");
            var plan = await Task.Run(() => BuildRunPlan(databaseSnapshot, settingsSnapshot, cleanupEnabledForRun, token), token).ConfigureAwait(false);

            if (request.Automatic &&
                cleanupEnabledForRun &&
                plan.LabelIdsToCleanup.Count > settingsSnapshot.MaxAutomaticCleanupLabels)
            {
                logger.Warn($"RandomSorting run {runId}: automatic cleanup skipped because {plan.LabelIdsToCleanup.Count} labels " +
                    $"exceeds the safety cap of {settingsSnapshot.MaxAutomaticCleanupLabels}. Use the manual cleanup button for this maintenance.");
                plan.LabelIdsToCleanup = new List<Guid>();
            }

            logger.Info($"RandomSorting run {runId}: planned {plan.TargetGameCount} target games, " +
                $"{plan.LabelsToCreate.Count} labels to create, {plan.GameUpdates.Count} game updates, " +
                $"{plan.LabelIdsToCleanup.Count} orphaned labels to clean.");

            await ApplyRunPlanAsync(plan, settingsSnapshot, request.Automatic, runId, token, progress).ConfigureAwait(false);

            stopwatch.Stop();
            progress?.Complete($"RandomSorting completed in {FormatElapsed(stopwatch.Elapsed)}.");
            logger.Info($"RandomSorting run {runId} completed in {stopwatch.Elapsed}.");
        }

        private async Task ExecuteCleanupAsync(SettingsSnapshot settingsSnapshot, int runId, CancellationToken token, ProgressReporter progress)
        {
            var stopwatch = Stopwatch.StartNew();

            logger.Info($"RandomSorting cleanup run {runId} started. LabelType={settingsSnapshot.SelectedLabelType}, " +
                $"Prefix={settingsSnapshot.RandomPrefix}, BatchSize={GetEffectiveBatchSize(settingsSnapshot, false)}, " +
                $"UiBatchDelayMs={settingsSnapshot.UiBatchDelayMs}.");

            progress?.SetIndeterminate("Capturing Playnite database snapshot...");
            var databaseSnapshot = CaptureDatabaseSnapshot(settingsSnapshot, token);
            logger.Info($"RandomSorting cleanup run {runId}: database snapshot captured in {stopwatch.Elapsed}. " +
                $"Games={databaseSnapshot.Games.Count}, Labels={databaseSnapshot.Labels.Count}.");

            progress?.SetIndeterminate("Finding unused random labels...");
            var cleanupLabelIds = await Task.Run(() => BuildCleanupLabelIds(databaseSnapshot, settingsSnapshot, token), token).ConfigureAwait(false);
            logger.Info($"RandomSorting cleanup run {runId}: planned {cleanupLabelIds.Count} orphaned labels to clean.");

            var plan = new RunPlan
            {
                TargetGameCount = 0,
                LabelsToCreate = new List<LabelCreatePlan>(),
                GameUpdates = new List<GameUpdatePlan>(),
                LabelIdsToCleanup = cleanupLabelIds,
                PrefixLabelIds = new HashSet<Guid>()
            };

            await ApplyRunPlanAsync(plan, settingsSnapshot, false, runId, token, progress).ConfigureAwait(false);

            stopwatch.Stop();
            progress?.Complete($"RandomSorting cleanup completed in {FormatElapsed(stopwatch.Elapsed)}.");
            logger.Info($"RandomSorting cleanup run {runId} completed in {stopwatch.Elapsed}.");
        }

        private SettingsSnapshot CreateSettingsSnapshot(LabelType? labelTypeOverride = null)
        {
            var currentSettings = settings.Settings;
            return new SettingsSnapshot
            {
                UpdateOnStartup = currentSettings.UpdateOnStartup,
                UpdateOnGameStart = currentSettings.UpdateOnGameStart,
                SelectedLabelType = labelTypeOverride ?? currentSettings.SelectedLabelType,
                RandomPrefix = string.IsNullOrWhiteSpace(currentSettings.RandomPrefix) ? "[Random] " : currentSettings.RandomPrefix,
                IncludeUninstalledGames = currentSettings.IncludeUninstalledGames,
                IncludeHiddenGames = currentSettings.IncludeHiddenGames,
                RequiredFeatureId = currentSettings.RequiredFeatureId,
                AutomaticRunBehavior = currentSettings.AutomaticRunBehavior,
                CleanupOrphanedRandomLabels = currentSettings.CleanupOrphanedRandomLabels,
                CleanupOnAutomaticRuns = currentSettings.CleanupOnAutomaticRuns,
                UseCollisionFreeLabels = currentSettings.UseCollisionFreeLabels,
                DetailedLogging = currentSettings.DetailedLogging,
                DatabaseUpdateBatchSize = Clamp(currentSettings.DatabaseUpdateBatchSize, MinDatabaseUpdateBatchSize, MaxDatabaseUpdateBatchSize),
                AutomaticDatabaseUpdateBatchSize = Clamp(currentSettings.AutomaticDatabaseUpdateBatchSize, MinDatabaseUpdateBatchSize, MaxDatabaseUpdateBatchSize),
                UiBatchDelayMs = Clamp(currentSettings.UiBatchDelayMs, MinUiBatchDelayMs, MaxUiBatchDelayMs),
                MaxAutomaticCleanupLabels = Clamp(currentSettings.MaxAutomaticCleanupLabels, MinMaxAutomaticCleanupLabels, MaxMaxAutomaticCleanupLabels)
            };
        }

        private DatabaseSnapshot CaptureDatabaseSnapshot(SettingsSnapshot settingsSnapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            return PlayniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                token.ThrowIfCancellationRequested();

                var games = PlayniteApi.Database.Games
                    .Select(game => new GameSnapshot
                    {
                        Id = game.Id,
                        Name = game.Name,
                        Hidden = game.Hidden,
                        IsInstalled = game.IsInstalled,
                        FeatureIds = game.FeatureIds?.ToList() ?? new List<Guid>(),
                        LabelIds = GetGameLabelIds(game, settingsSnapshot.SelectedLabelType)
                    })
                    .ToList();

                var labels = settingsSnapshot.SelectedLabelType == LabelType.Tag
                    ? PlayniteApi.Database.Tags.Select(tag => new LabelSnapshot { Id = tag.Id, Name = tag.Name }).ToList()
                    : PlayniteApi.Database.Categories.Select(category => new LabelSnapshot { Id = category.Id, Name = category.Name }).ToList();

                return new DatabaseSnapshot
                {
                    Games = games,
                    Labels = labels
                };
            }, DispatcherPriority.Background);
        }

        private RunPlan BuildRunPlan(DatabaseSnapshot databaseSnapshot, SettingsSnapshot settingsSnapshot, bool cleanupEnabledForRun, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var prefix = settingsSnapshot.RandomPrefix;
            var allPrefixLabelIds = new HashSet<Guid>(
                databaseSnapshot.Labels
                    .Where(label => label.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(label => label.Id));

            var labelRefCounts = BuildLabelRefCounts(databaseSnapshot.Games);
            var labelsByName = databaseSnapshot.Labels
                .GroupBy(label => label.Name)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var labelsToCreate = new List<LabelCreatePlan>();
            var gameUpdates = new List<GameUpdatePlan>();
            var targetGames = databaseSnapshot.Games
                .Where(game => IsEligibleGame(game, settingsSnapshot))
                .ToList();

            var assignedNames = CreateRandomLabelNames(settingsSnapshot, targetGames.Count);

            for (var index = 0; index < targetGames.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                var game = targetGames[index];
                var newLabelName = assignedNames[index];
                LabelSnapshot labelSnapshot;

                if (!labelsByName.TryGetValue(newLabelName, out labelSnapshot))
                {
                    labelSnapshot = new LabelSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Name = newLabelName
                    };

                    labelsByName[newLabelName] = labelSnapshot;
                    labelsToCreate.Add(new LabelCreatePlan
                    {
                        Id = labelSnapshot.Id,
                        Name = labelSnapshot.Name
                    });
                }

                allPrefixLabelIds.Add(labelSnapshot.Id);

                var nextLabelIds = game.LabelIds
                    .Where(labelId => !allPrefixLabelIds.Contains(labelId))
                    .ToList();

                if (!nextLabelIds.Contains(labelSnapshot.Id))
                {
                    nextLabelIds.Add(labelSnapshot.Id);
                }

                if (!SameIdSet(game.LabelIds, nextLabelIds))
                {
                    UpdateRefCounts(labelRefCounts, game.LabelIds, nextLabelIds);
                    gameUpdates.Add(new GameUpdatePlan
                    {
                        GameId = game.Id,
                        GameName = game.Name,
                        DesiredRandomLabelId = labelSnapshot.Id
                    });
                }
            }

            var cleanupLabelIds = cleanupEnabledForRun
                ? GetUnusedPrefixLabelIds(databaseSnapshot.Labels, labelRefCounts, prefix)
                : new List<Guid>();

            return new RunPlan
            {
                TargetGameCount = targetGames.Count,
                LabelsToCreate = labelsToCreate,
                GameUpdates = gameUpdates,
                LabelIdsToCleanup = cleanupLabelIds,
                PrefixLabelIds = allPrefixLabelIds
            };
        }

        private List<Guid> BuildCleanupLabelIds(DatabaseSnapshot databaseSnapshot, SettingsSnapshot settingsSnapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var labelRefCounts = BuildLabelRefCounts(databaseSnapshot.Games);
            token.ThrowIfCancellationRequested();

            return GetUnusedPrefixLabelIds(databaseSnapshot.Labels, labelRefCounts, settingsSnapshot.RandomPrefix);
        }

        private List<Guid> GetUnusedPrefixLabelIds(List<LabelSnapshot> labels, Dictionary<Guid, int> labelRefCounts, string prefix)
        {
            return labels
                .Where(label => label.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Where(label => !labelRefCounts.ContainsKey(label.Id) || labelRefCounts[label.Id] <= 0)
                .Select(label => label.Id)
                .ToList();
        }

        private async Task ApplyRunPlanAsync(RunPlan plan, SettingsSnapshot settingsSnapshot, bool automatic, int runId, CancellationToken token, ProgressReporter progress = null)
        {
            var batchSize = GetEffectiveBatchSize(settingsSnapshot, automatic);
            var pendingLabelCreates = plan.LabelsToCreate.ToDictionary(label => label.Id);
            var standaloneLabelChunks = plan.GameUpdates.Count == 0 ? GetChunkCount(plan.LabelsToCreate.Count, batchSize) : 0;
            var progressMax = standaloneLabelChunks + GetChunkCount(plan.GameUpdates.Count, batchSize) +
                (plan.LabelIdsToCleanup.Count > 0 ? 1 + GetChunkCount(plan.LabelIdsToCleanup.Count, batchSize) : 0);

            if (progress != null)
            {
                progress.StartDeterminate("Applying RandomSorting database updates...", Math.Max(1, progressMax));
            }

            if (plan.LabelsToCreate.Count > 0 && plan.GameUpdates.Count == 0)
            {
                logger.Info($"RandomSorting run {runId}: creating {plan.LabelsToCreate.Count} labels in {standaloneLabelChunks} chunk(s).");

                for (var index = 0; index < plan.LabelsToCreate.Count; index += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    var chunkNumber = index / batchSize + 1;
                    var chunk = plan.LabelsToCreate.Skip(index).Take(batchSize).ToList();
                    var created = InvokeOnUi(() => AddLabels(settingsSnapshot.SelectedLabelType, chunk), token);

                    if (settingsSnapshot.DetailedLogging)
                    {
                        logger.Info($"RandomSorting run {runId}: label chunk {chunkNumber}/{standaloneLabelChunks} created {created} label(s).");
                    }

                    foreach (var label in chunk)
                    {
                        pendingLabelCreates.Remove(label.Id);
                    }

                    progress?.Step($"Created random labels {chunkNumber}/{standaloneLabelChunks} ({created} in this batch).");
                    await DelayBetweenUiChunksAsync(settingsSnapshot, token).ConfigureAwait(false);
                }
            }

            if (plan.GameUpdates.Count > 0)
            {
                var updateChunks = GetChunkCount(plan.GameUpdates.Count, batchSize);
                logger.Info($"RandomSorting run {runId}: applying {plan.GameUpdates.Count} game updates in {updateChunks} chunk(s).");

                for (var index = 0; index < plan.GameUpdates.Count; index += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    var chunkNumber = index / batchSize + 1;
                    var chunk = plan.GameUpdates.Skip(index).Take(batchSize).ToList();
                    var labelsForChunk = chunk
                        .Select(update => update.DesiredRandomLabelId)
                        .Distinct()
                        .Where(labelId => pendingLabelCreates.ContainsKey(labelId))
                        .Select(labelId => pendingLabelCreates[labelId])
                        .ToList();

                    if (labelsForChunk.Count > 0)
                    {
                        var created = InvokeOnUi(() => AddLabels(settingsSnapshot.SelectedLabelType, labelsForChunk), token);

                        foreach (var label in labelsForChunk)
                        {
                            pendingLabelCreates.Remove(label.Id);
                        }

                        if (settingsSnapshot.DetailedLogging)
                        {
                            logger.Info($"RandomSorting run {runId}: game chunk {chunkNumber}/{updateChunks} created {created} needed label(s).");
                        }
                    }

                    var updated = InvokeOnUi(() => UpdateGames(settingsSnapshot.SelectedLabelType, chunk, plan.PrefixLabelIds), token);

                    if (settingsSnapshot.DetailedLogging)
                    {
                        logger.Info($"RandomSorting run {runId}: game chunk {chunkNumber}/{updateChunks} updated {updated} game(s).");
                    }

                    progress?.Step($"Updated games {chunkNumber}/{updateChunks} ({updated} games, {labelsForChunk.Count} labels in this batch).");
                    await DelayBetweenUiChunksAsync(settingsSnapshot, token).ConfigureAwait(false);
                }
            }

            if (pendingLabelCreates.Count > 0)
            {
                logger.Warn($"RandomSorting run {runId}: {pendingLabelCreates.Count} planned label(s) were not needed by any game update.");
            }

            if (plan.LabelIdsToCleanup.Count > 0)
            {
                logger.Info($"RandomSorting run {runId}: verifying {plan.LabelIdsToCleanup.Count} cleanup candidate(s).");
                progress?.Step($"Verifying {plan.LabelIdsToCleanup.Count} cleanup candidate(s)...");
                var verifiedCleanupIds = InvokeOnUi(() => FilterUnusedLabelIds(settingsSnapshot.SelectedLabelType, plan.LabelIdsToCleanup), token);
                var cleanupChunks = GetChunkCount(verifiedCleanupIds.Count, batchSize);
                var removedTotal = 0;
                logger.Info($"RandomSorting run {runId}: cleaning {verifiedCleanupIds.Count} verified orphaned labels in {cleanupChunks} chunk(s).");

                for (var index = 0; index < verifiedCleanupIds.Count; index += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    var chunkNumber = index / batchSize + 1;
                    var chunk = verifiedCleanupIds.Skip(index).Take(batchSize).ToList();
                    var removed = InvokeOnUi(() => RemoveLabels(settingsSnapshot.SelectedLabelType, chunk, settingsSnapshot.RandomPrefix), token);
                    removedTotal += removed;

                    if (settingsSnapshot.DetailedLogging)
                    {
                        logger.Info($"RandomSorting run {runId}: cleanup chunk {chunkNumber}/{cleanupChunks} removed {removed} label(s).");
                    }

                    progress?.Step($"Removed unused random labels {chunkNumber}/{cleanupChunks} ({removed} in this batch).");
                    await DelayBetweenUiChunksAsync(settingsSnapshot, token).ConfigureAwait(false);
                }

                logger.Info($"RandomSorting run {runId}: cleanup removed {removedTotal} orphaned label(s).");
            }

            if (progressMax == 0)
            {
                progress?.Step("No database updates were needed.");
            }
        }

        private int AddLabels(LabelType labelType, List<LabelCreatePlan> labelsToCreate)
        {
            if (labelsToCreate.Count == 0)
            {
                return 0;
            }

            if (labelType == LabelType.Tag)
            {
                var tags = labelsToCreate.Select(label => new Tag { Id = label.Id, Name = label.Name }).ToList();
                using (PlayniteApi.Database.Tags.BufferedUpdate())
                {
                    PlayniteApi.Database.Tags.Add(tags);
                }
            }
            else
            {
                var categories = labelsToCreate.Select(label => new Category { Id = label.Id, Name = label.Name }).ToList();
                using (PlayniteApi.Database.Categories.BufferedUpdate())
                {
                    PlayniteApi.Database.Categories.Add(categories);
                }
            }

            return labelsToCreate.Count;
        }

        private int UpdateGames(LabelType labelType, List<GameUpdatePlan> updates, HashSet<Guid> prefixLabelIds)
        {
            var changedGames = new List<Game>();

            foreach (var update in updates)
            {
                var game = PlayniteApi.Database.Games.Get(update.GameId);
                if (game == null)
                {
                    logger.Warn($"RandomSorting skipped update for missing game '{update.GameName}' ({update.GameId}).");
                    continue;
                }

                var currentLabelIds = GetGameLabelIds(game, labelType);
                var nextLabelIds = currentLabelIds
                    .Where(labelId => !prefixLabelIds.Contains(labelId))
                    .ToList();

                if (!nextLabelIds.Contains(update.DesiredRandomLabelId))
                {
                    nextLabelIds.Add(update.DesiredRandomLabelId);
                }

                if (SameIdSet(currentLabelIds, nextLabelIds))
                {
                    continue;
                }

                SetGameLabelIds(game, labelType, nextLabelIds);
                changedGames.Add(game);
            }

            if (changedGames.Count > 0)
            {
                using (PlayniteApi.Database.Games.BufferedUpdate())
                {
                    PlayniteApi.Database.Games.Update(changedGames);
                }
            }

            return changedGames.Count;
        }

        private List<Guid> FilterUnusedLabelIds(LabelType labelType, List<Guid> labelIds)
        {
            var labelIdsToRemove = new HashSet<Guid>(labelIds);

            foreach (var game in PlayniteApi.Database.Games)
            {
                foreach (var labelId in GetGameLabelIds(game, labelType))
                {
                    if (labelIdsToRemove.Contains(labelId))
                    {
                        labelIdsToRemove.Remove(labelId);
                    }
                }

                if (labelIdsToRemove.Count == 0)
                {
                    break;
                }
            }

            return labelIdsToRemove.ToList();
        }

        private int RemoveLabels(LabelType labelType, List<Guid> labelIds, string prefix)
        {
            if (labelType == LabelType.Tag)
            {
                var tags = labelIds
                    .Select(labelId => PlayniteApi.Database.Tags.Get(labelId))
                    .Where(tag => tag != null && tag.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                if (tags.Count > 0)
                {
                    using (PlayniteApi.Database.Tags.BufferedUpdate())
                    {
                        PlayniteApi.Database.Tags.Remove(tags);
                    }
                }

                return tags.Count;
            }
            else
            {
                var categories = labelIds
                    .Select(labelId => PlayniteApi.Database.Categories.Get(labelId))
                    .Where(category => category != null && category.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                if (categories.Count > 0)
                {
                    using (PlayniteApi.Database.Categories.BufferedUpdate())
                    {
                        PlayniteApi.Database.Categories.Remove(categories);
                    }
                }

                return categories.Count;
            }
        }

        private T InvokeOnUi<T>(Func<T> action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = PlayniteApi.MainView.UIDispatcher.Invoke(action, DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private Task DelayBetweenUiChunksAsync(SettingsSnapshot settingsSnapshot, CancellationToken token)
        {
            return settingsSnapshot.UiBatchDelayMs > 0
                ? Task.Delay(settingsSnapshot.UiBatchDelayMs, token)
                : Task.CompletedTask;
        }

        private bool IsEligibleGame(GameSnapshot game, SettingsSnapshot settingsSnapshot)
        {
            if (!settingsSnapshot.IncludeHiddenGames && game.Hidden)
            {
                return false;
            }

            if (!settingsSnapshot.IncludeUninstalledGames && !game.IsInstalled)
            {
                return false;
            }

            if (settingsSnapshot.RequiredFeatureId.HasValue &&
                !game.FeatureIds.Contains(settingsSnapshot.RequiredFeatureId.Value))
            {
                return false;
            }

            return true;
        }

        private List<string> CreateRandomLabelNames(SettingsSnapshot settingsSnapshot, int targetGameCount)
        {
            var rng = new Random();
            var names = new List<string>(targetGameCount);

            if (settingsSnapshot.UseCollisionFreeLabels)
            {
                var ranks = Enumerable.Range(1, targetGameCount).ToList();
                Shuffle(ranks, rng);

                var width = Math.Max(5, targetGameCount.ToString().Length);
                names.AddRange(ranks.Select(rank => settingsSnapshot.RandomPrefix + rank.ToString("D" + width)));
            }
            else
            {
                for (var index = 0; index < targetGameCount; index++)
                {
                    names.Add(settingsSnapshot.RandomPrefix + rng.Next(10000, 99999));
                }
            }

            return names;
        }

        private void Shuffle<T>(IList<T> items, Random rng)
        {
            for (var index = items.Count - 1; index > 0; index--)
            {
                var swapIndex = rng.Next(index + 1);
                var value = items[index];
                items[index] = items[swapIndex];
                items[swapIndex] = value;
            }
        }

        private Dictionary<Guid, int> BuildLabelRefCounts(List<GameSnapshot> games)
        {
            var counts = new Dictionary<Guid, int>();

            foreach (var game in games)
            {
                foreach (var labelId in game.LabelIds.Distinct())
                {
                    IncrementCount(counts, labelId);
                }
            }

            return counts;
        }

        private void UpdateRefCounts(Dictionary<Guid, int> counts, List<Guid> oldIds, List<Guid> newIds)
        {
            var oldSet = new HashSet<Guid>(oldIds);
            var newSet = new HashSet<Guid>(newIds);

            foreach (var removedId in oldSet.Where(labelId => !newSet.Contains(labelId)))
            {
                if (counts.ContainsKey(removedId))
                {
                    counts[removedId]--;
                }
            }

            foreach (var addedId in newSet.Where(labelId => !oldSet.Contains(labelId)))
            {
                IncrementCount(counts, addedId);
            }
        }

        private void IncrementCount(Dictionary<Guid, int> counts, Guid id)
        {
            if (counts.ContainsKey(id))
            {
                counts[id]++;
            }
            else
            {
                counts[id] = 1;
            }
        }

        private static List<Guid> GetGameLabelIds(Game game, LabelType labelType)
        {
            return (labelType == LabelType.Tag ? game.TagIds : game.CategoryIds)?.ToList() ?? new List<Guid>();
        }

        private static void SetGameLabelIds(Game game, LabelType labelType, List<Guid> labelIds)
        {
            if (labelType == LabelType.Tag)
            {
                game.TagIds = labelIds;
            }
            else
            {
                game.CategoryIds = labelIds;
            }
        }

        private bool SameIdSet(List<Guid> first, List<Guid> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            var firstSet = new HashSet<Guid>(first);
            return firstSet.SetEquals(second);
        }

        private int GetChunkCount(int itemCount, int chunkSize)
        {
            return (int)Math.Ceiling(itemCount / (double)chunkSize);
        }

        private int GetEffectiveBatchSize(SettingsSnapshot settingsSnapshot, bool automatic)
        {
            return automatic
                ? settingsSnapshot.AutomaticDatabaseUpdateBatchSize
                : settingsSnapshot.DatabaseUpdateBatchSize;
        }

        private int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        private async Task PrepareManualOperationAsync(ProgressReporter progress, CancellationToken token)
        {
            Task taskToWait;

            lock (runLock)
            {
                manualOperationActive = true;
                pendingRun = null;
                queuedRun = null;
                hasQueuedRun = false;
                activeRunCancellation?.Cancel();
                taskToWait = workerTask;
            }

            try
            {
                if (taskToWait != null && !taskToWait.IsCompleted)
                {
                    progress?.SetIndeterminate("Waiting for active automatic run to stop...");

                    while (!taskToWait.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                EndManualOperation();
                throw;
            }
        }

        private void EndManualOperation()
        {
            lock (runLock)
            {
                manualOperationActive = false;
            }
        }

        private string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.0}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
        }

        private class ProgressReporter
        {
            private readonly GlobalProgressActionArgs args;
            private int current;
            private int maximum = 1;

            public ProgressReporter(GlobalProgressActionArgs args)
            {
                this.args = args;
            }

            public void SetIndeterminate(string text)
            {
                args.IsIndeterminate = true;
                args.Text = text;
            }

            public void StartDeterminate(string text, int maxValue)
            {
                maximum = Math.Max(1, maxValue);
                current = 0;
                args.IsIndeterminate = false;
                args.ProgressMaxValue = maximum;
                args.CurrentProgressValue = current;
                args.Text = text;
            }

            public void Step(string text)
            {
                current = Math.Min(maximum, current + 1);
                args.IsIndeterminate = false;
                args.ProgressMaxValue = maximum;
                args.CurrentProgressValue = current;
                args.Text = text;
            }

            public void Complete(string text)
            {
                args.IsIndeterminate = false;
                args.ProgressMaxValue = maximum;
                args.CurrentProgressValue = maximum;
                args.Text = text;
            }
        }

        private class PendingRun
        {
            public PendingRun(string trigger, bool automatic, SettingsSnapshot settings)
            {
                Trigger = trigger;
                Automatic = automatic;
                Settings = settings;
            }

            public string Trigger { get; }
            public bool Automatic { get; }
            public SettingsSnapshot Settings { get; }
        }

        private class SettingsSnapshot
        {
            public bool UpdateOnStartup { get; set; }
            public bool UpdateOnGameStart { get; set; }
            public LabelType SelectedLabelType { get; set; }
            public string RandomPrefix { get; set; }
            public bool IncludeUninstalledGames { get; set; }
            public bool IncludeHiddenGames { get; set; }
            public Guid? RequiredFeatureId { get; set; }
            public AutomaticRunBehavior AutomaticRunBehavior { get; set; }
            public bool CleanupOrphanedRandomLabels { get; set; }
            public bool CleanupOnAutomaticRuns { get; set; }
            public bool UseCollisionFreeLabels { get; set; }
            public bool DetailedLogging { get; set; }
            public int DatabaseUpdateBatchSize { get; set; }
            public int AutomaticDatabaseUpdateBatchSize { get; set; }
            public int UiBatchDelayMs { get; set; }
            public int MaxAutomaticCleanupLabels { get; set; }
        }

        private class DatabaseSnapshot
        {
            public List<GameSnapshot> Games { get; set; }
            public List<LabelSnapshot> Labels { get; set; }
        }

        private class GameSnapshot
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public bool Hidden { get; set; }
            public bool IsInstalled { get; set; }
            public List<Guid> FeatureIds { get; set; }
            public List<Guid> LabelIds { get; set; }
        }

        private class LabelSnapshot
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
        }

        private class LabelCreatePlan
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
        }

        private class GameUpdatePlan
        {
            public Guid GameId { get; set; }
            public string GameName { get; set; }
            public Guid DesiredRandomLabelId { get; set; }
        }

        private class RunPlan
        {
            public int TargetGameCount { get; set; }
            public List<LabelCreatePlan> LabelsToCreate { get; set; }
            public List<GameUpdatePlan> GameUpdates { get; set; }
            public List<Guid> LabelIdsToCleanup { get; set; }
            public HashSet<Guid> PrefixLabelIds { get; set; }
        }
    }
}
