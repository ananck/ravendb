using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Server.PeriodicBackup;
using Raven.Client.Util;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.PeriodicBackup.Azure;
using Raven.Server.Documents.PeriodicBackup.Aws;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Utils;
using Sparrow.Logging;
using DatabaseSmuggler = Raven.Server.Smuggler.Documents.DatabaseSmuggler;
using System.Collections.Concurrent;
using System.Linq;
using NCrontab.Advanced;
using Raven.Client.Server;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Commands.PeriodicBackup;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.PeriodicBackup
{
    public class LastBackupInfo
    {
        public BackupType Type { get; set; }

        public BackupDestination BackupDestination { get; set; }

        public DateTime LastFullBackup { get; set; }

        public DateTime LastIncrementalBackup { get; set; }
    }

    public enum BackupDestination
    {
        Local,
        Glacier,
        Aws,
        Azure
    }

    public class PeriodicBackupRunner : IDisposable
    {
        private readonly Logger _logger;

        private readonly DocumentDatabase _database;
        private readonly ServerStore _serverStore;
        private readonly CancellationTokenSource _cancellationToken;
        private readonly PathSetting _tempBackupPath;
        private readonly ConcurrentDictionary<long, PeriodicBackup> _periodicBackups
            = new ConcurrentDictionary<long, PeriodicBackup>();
        private readonly List<Task> _inactiveRunningPeriodicBackupsTasks = new List<Task>();

        //interval can be 2^32-2 milliseconds at most
        //this is the maximum interval acceptable in .Net's threading timer
        public readonly TimeSpan MaxTimerTimeout = TimeSpan.FromMilliseconds(Math.Pow(2, 32) - 2);

        private int? _exportLimit; //TODO: remove

        public PeriodicBackupRunner(DocumentDatabase database, ServerStore serverStore)
        {
            _database = database;
            _serverStore = serverStore;
            _logger = LoggingSource.Instance.GetLogger<PeriodicBackupRunner>(_database.Name);
            _cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_database.DatabaseShutdown);

            _tempBackupPath =
                (_database.Configuration.Storage.TempPath ??
                 _database.Configuration.Core.DataDirectory)
                .Combine($"PeriodicBackup-Temp-{_database.Name}");

            if (Directory.Exists(_tempBackupPath.FullPath))
                IOExtensions.DeleteDirectory(_tempBackupPath.FullPath);

            Directory.CreateDirectory(_tempBackupPath.FullPath);
        }

        private Timer GetTimer(
            PeriodicBackupConfiguration configuration,
            PeriodicBackupStatus backupStatus = null)
        {
            if (backupStatus == null)
                backupStatus = new PeriodicBackupStatus();

            var now = SystemTime.UtcNow;
            var lastFullBackup = backupStatus.LastFullBackup ?? now;
            var lastIncrementalBackup = backupStatus.LastIncrementalBackup ?? backupStatus.LastFullBackup ?? now;
            var nextFullBackup = GetNextBackupOccurrence(configuration.FullBackupFrequency, lastFullBackup);
            var nextIncrementalBackup = GetNextBackupOccurrence(configuration.IncrementalBackupFrequency, lastIncrementalBackup);

            if (nextFullBackup == null && nextIncrementalBackup == null)
                return null;

            Debug.Assert(configuration.TaskId != null);

            var nextBackupDateTime = GetNextBackupDateTime(nextFullBackup, nextIncrementalBackup);
            var nextBackupTimeSpan =
                (nextBackupDateTime - now).Ticks <= 0 ?
                    TimeSpan.Zero :
                    nextBackupDateTime - now;

            var isFullBackup = IsFullBackup(backupStatus, configuration, nextFullBackup, nextIncrementalBackup);
            if (_logger.IsInfoEnabled)
                _logger.Info($"Next {(isFullBackup ? "full" : "incremental")} " +
                             $"backup is in {nextBackupTimeSpan.TotalMinutes} minutes");

            var backupTaskDetails = new BackupTaskDetails
            {
                IsFullBackup = isFullBackup,
                TaskId = configuration.TaskId.Value,
                NextBackup = nextBackupTimeSpan
            };

            var isValidTimeSpanForTimer = IsValidTimeSpanForTimer(backupTaskDetails.NextBackup);
            var timer = isValidTimeSpanForTimer ?
                new Timer(TimerCallback, backupTaskDetails, backupTaskDetails.NextBackup, Timeout.InfiniteTimeSpan) :
                new Timer(LongPeriodTimerCallback, backupTaskDetails, MaxTimerTimeout, Timeout.InfiniteTimeSpan);

            return timer;
        }

        private bool IsFullBackup(PeriodicBackupStatus backupStatus, 
            PeriodicBackupConfiguration configuration,
            DateTime? nextFullBackup, DateTime? nextIncrementalBackup)
        {
            if (backupStatus.LastFullBackup == null ||
                backupStatus.NodeTag != _serverStore.NodeTag ||
                backupStatus.BackupType != configuration.BackupType ||
                backupStatus.LastEtag == null)
            {
                // Reasons to start a new full backup:
                // 1. there is no previous full backup, we are going to create one now
                // 2. the node which is responsible for the backup was replaced
                // 3. the backup type changed (e.g. from backup to snapshot)
                // 4. last etag wasn't updated

                return true;
            }

            // 1. there is a full backup setup but the next incremental backup wasn't setup
            // 2. there is a full backup setup and the next full backup is before the incremental one
            return nextFullBackup != null &&
                   (nextIncrementalBackup == null || nextFullBackup <= nextIncrementalBackup);
        }

        private static DateTime GetNextBackupDateTime(DateTime? nextFullBackup, DateTime? nextIncrementalBackup)
        {
            Debug.Assert(nextFullBackup != null || nextIncrementalBackup != null);

            if (nextFullBackup == null)
                return nextIncrementalBackup.Value;

            if (nextIncrementalBackup == null)
                return nextFullBackup.Value;

            var nextBackup = 
                nextFullBackup <= nextIncrementalBackup ? 
                nextFullBackup.Value : 
                nextIncrementalBackup.Value;

            return nextBackup;
        }

        private static DateTime? GetNextBackupOccurrence(string backupFrequency, DateTime now)
        {
            try
            {
                var backupParser = CrontabSchedule.Parse(backupFrequency);
                return backupParser.GetNextOccurrence(now);
            }
            catch (Exception e)
            {
                var exception = e;
                //TODO: error to notification center
                return null;
            }
        }

        private class BackupTaskDetails
        {
            public long TaskId { get; set; }

            public bool IsFullBackup { get; set; }

            public TimeSpan NextBackup { get; set; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsValidTimeSpanForTimer(TimeSpan nextBackupTimeSpan)
        {
            return nextBackupTimeSpan < MaxTimerTimeout;
        }

        private void TimerCallback(object backupTaskDetails)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            var backupDetails = (BackupTaskDetails)backupTaskDetails;

            PeriodicBackup periodicBackup;
            if (ShouldRunBackupAfterTimerCallback(backupDetails, out periodicBackup) == false)
                return;

            CreateBackupTask(periodicBackup, backupDetails);
        }

        private void CreateBackupTask(PeriodicBackup periodicBackup, BackupTaskDetails backupDetails)
        {
            periodicBackup.RunningTask = Task.Run(async () =>
            {
                Debug.Assert(periodicBackup.Configuration.TaskId != null);
                var backupStatus = GetBackupStatus(periodicBackup.Configuration.TaskId.Value);
                backupStatus.TaskId = periodicBackup.Configuration.TaskId.Value;

                try
                {
                    await RunPeriodicBackup(periodicBackup.Configuration,
                        backupStatus, backupDetails.IsFullBackup);
                }
                finally
                {
                    if (_cancellationToken.IsCancellationRequested == false &&
                        periodicBackup.Disposed == false)
                    {
                        periodicBackup.BackupTimer.Dispose();
                        periodicBackup.BackupTimer = GetTimer(periodicBackup.Configuration, backupStatus);
                    }
                }
            }, _database.DatabaseShutdown);
        }

        private DatabaseRecord GetDatabaseRecord()
        {
            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                return _serverStore.Cluster.ReadDatabase(context, _database.Name);
            }
        }

        private void LongPeriodTimerCallback(object backupTaskDetails)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            var backupDetails = (BackupTaskDetails)backupTaskDetails;

            PeriodicBackup periodicBackup;
            if (ShouldRunBackupAfterTimerCallback(backupDetails, out periodicBackup) == false)
                return;

            var remainingInterval = backupDetails.NextBackup - MaxTimerTimeout;
            var shouldExecuteTimer = remainingInterval.TotalMilliseconds <= 0;
            if (shouldExecuteTimer)
            {
                CreateBackupTask(periodicBackup, backupDetails);
                return;
            }

            backupDetails.NextBackup = remainingInterval;
            var nextBackupTimeSpan = IsValidTimeSpanForTimer(remainingInterval) ? remainingInterval : MaxTimerTimeout;
            periodicBackup.BackupTimer.Change(nextBackupTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private bool ShouldRunBackupAfterTimerCallback(BackupTaskDetails backupInfo, out PeriodicBackup periodicBackup)
        {
            if (_periodicBackups.TryGetValue(backupInfo.TaskId, out periodicBackup) == false)
            {
                // periodic backup doesn't exist anymore
                return false;
            }

            if (periodicBackup.Disposed)
            {
                // this periodic backup was canceled
                return false;
            }

            var databaseRecord = GetDatabaseRecord();
            var taskStatus = GetTaskStatus(databaseRecord, periodicBackup.Configuration);
            return taskStatus == TaskStatus.ActiveByCurrentNode;
        }

        private async Task RunPeriodicBackup(
            PeriodicBackupConfiguration configuration,
            PeriodicBackupStatus status,
            bool isFullBackup)
        {
            try
            {
                var totalSw = Stopwatch.StartNew();
                DocumentsOperationContext context;
                using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
                using (var tx = context.OpenReadTransaction())
                {
                    var backupToLocalFolder = CanBackupUsing(configuration.LocalSettings);

                    var now = SystemTime.UtcNow.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture);
                    var backupDirectory = backupToLocalFolder
                        ? GetLocalFolderPath(configuration, now)
                        : _tempBackupPath;

                    if (Directory.Exists(backupDirectory.FullPath) == false)
                        Directory.CreateDirectory(backupDirectory.FullPath);

                    if (status.LocalBackup == null)
                        status.LocalBackup = new LocalBackup();

                    // check if we need to do a new full backup
                    if (isFullBackup ||
                        status.LastFullBackup == null || // no full backup was previously performed
                        status.NodeTag != _serverStore.NodeTag || // last backup was performed by a different node
                        status.BackupType != configuration.BackupType || // backup type has changed
                        status.LastEtag == null || // last document etag wasn't updated
                        backupToLocalFolder && DirectoryContainsFullBackupOrSnapshot(status.LocalBackup.BackupDirectory, configuration.BackupType) == false)
                        // the local folder has a missing full backup
                    {
                        isFullBackup = true;

                        status.LocalBackup.TempFolderUsed = backupToLocalFolder == false;
                        status.LocalBackup.BackupDirectory = backupToLocalFolder ? backupDirectory.FullPath : null;
                    }

                    if (_logger.IsInfoEnabled)
                    {
                        var fullBackupText = "a " + (configuration.BackupType == BackupType.Backup ? "full backup" : "snapshot");
                        _logger.Info($"Creating {(isFullBackup ? fullBackupText : "an incremental backup")}");
                    }

                    if (isFullBackup == false)
                    {
                        var currentLastEtag = DocumentsStorage.ReadLastEtag(tx.InnerTransaction);
                        // no-op if nothing has changed
                        if (currentLastEtag == status.LastEtag)
                            return;
                    }

                    var startDocumentEtag = isFullBackup == false ? status.LastEtag : null;
                    var fileName = GetFileName(isFullBackup, backupDirectory.FullPath, now, configuration.BackupType, out string backupFilePath);

                    var lastEtag = CreateLocalBackupOrSnapshot(configuration,
                        isFullBackup, status, backupFilePath, startDocumentEtag, context, tx);

                    if (isFullBackup == false &&
                        lastEtag == status.LastEtag)
                    {
                        // no-op if nothing has changed

                        if (_logger.IsInfoEnabled)
                            _logger.Info("Periodic backup returned prematurely, " +
                                         "nothing has changed since last backup");
                        return;
                    }

                    try
                    {
                        await UploadToServer(configuration, status, backupFilePath, fileName, isFullBackup);
                    }
                    finally
                    {
                        // if user did not specify local folder we delete temporary file.
                        if (backupToLocalFolder == false)
                        {
                            IOExtensions.DeleteFile(backupFilePath);
                        }
                    }

                    status.BackupType = configuration.BackupType;
                    status.LastEtag = lastEtag;
                }

                totalSw.Stop();

                if (_logger.IsInfoEnabled)
                {
                    var fullBackupText = "a " + (configuration.BackupType == BackupType.Backup ? " full backup" : " snapshot");
                    _logger.Info($"Successfully created {(isFullBackup ? fullBackupText : "an incremental backup")} " +
                                 $"in {totalSw.ElapsedMilliseconds:#,#;;0} ms");
                }

                status.DurationInMs = totalSw.ElapsedMilliseconds;
                status.NodeTag = _serverStore.NodeTag;
                
                await WriteStatus(status);

                _exportLimit = null;
            }
            catch (OperationCanceledException)
            {
                // shutting down, probably
            }
            catch (ObjectDisposedException)
            {
                // shutting down, probably
            }
            catch (Exception e)
            {
                _exportLimit = 100;
                const string message = "Error when performing periodic backup";

                if (_logger.IsOperationsEnabled)
                    _logger.Operations(message, e);

                _database.NotificationCenter.Add(AlertRaised.Create("Periodic Backup",
                    message,
                    AlertType.PeriodicBackup,
                    NotificationSeverity.Error,
                    details: new ExceptionDetails(e)));
            }
        }

        private PathSetting GetLocalFolderPath(PeriodicBackupConfiguration configuration, string now)
        {
            var localFolderPath = new PathSetting(configuration.LocalSettings.FolderPath);
            return localFolderPath
                .Combine($"{now}.ravendb-{_database.Name}-{_serverStore.NodeTag}-{configuration.BackupType.ToString().ToLower()}");
        }

        private string GetFileName(
            bool isFullBackup,
            string backupFolder,
            string now,
            BackupType backupType, 
            out string backupFilePath)
        {
            string fileName;
            if (isFullBackup)
            {
                // create filename for full backup/snapshot
                fileName = $"{now}.ravendb-{GetFullBackupName(backupType)}";
                backupFilePath = Path.Combine(backupFolder, fileName);
                if (File.Exists(backupFilePath))
                {
                    var counter = 1;
                    while (true)
                    {
                        fileName = $"{now} - {counter}.${GetFullBackupExtension(backupType)}";
                        backupFilePath = Path.Combine(backupFolder, fileName);

                        if (File.Exists(backupFilePath) == false)
                            break;

                        counter++;
                    }
                }
            }
            else
            {
                // create filename for incremental backup
                fileName = $"{now}-0.${Constants.Documents.PeriodicBackup.IncrementalBackupExtension}";
                backupFilePath = Path.Combine(backupFolder, fileName);
                if (File.Exists(backupFilePath))
                {
                    var counter = 1;
                    while (true)
                    {
                        fileName = $"{now}-{counter}.${Constants.Documents.PeriodicBackup.IncrementalBackupExtension}";
                        backupFilePath = Path.Combine(backupFolder, fileName);

                        if (File.Exists(backupFilePath) == false)
                            break;

                        counter++;
                    }
                }
            }

            return fileName;
        }

        private long CreateLocalBackupOrSnapshot(PeriodicBackupConfiguration configuration, 
            bool isFullBackup, PeriodicBackupStatus status, string backupFilePath,
            long? startDocumentEtag, DocumentsOperationContext context, DocumentsTransaction tx)
        {
            long lastEtag;
            var exception = new Reference<Exception>();
            using (status.LocalBackup.Update(isFullBackup, exception))
            {
                try
                {
                    if (configuration.BackupType == BackupType.Backup ||
                        configuration.BackupType == BackupType.Snapshot && isFullBackup == false)
                    {
                        // smuggler backup
                        var result = CreateBackup(backupFilePath, startDocumentEtag, context);
                        lastEtag = result.GetLastEtag();
                    }
                    else
                    {
                        // snapshot backup
                        lastEtag = DocumentsStorage.ReadLastEtag(tx.InnerTransaction);
                        _database.FullBackupTo(backupFilePath);
                    }
                }
                catch (Exception e)
                {
                    exception.Value = e;
                    throw;
                }
            }
            return lastEtag;
        }

        private static string GetFullBackupExtension(BackupType type)
        {
            switch (type)
            {
                case BackupType.Backup:
                    return Constants.Documents.PeriodicBackup.FullBackupExtension;
                case BackupType.Snapshot:
                    return Constants.Documents.PeriodicBackup.SnapshotExtension;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private string GetFullBackupName(BackupType type)
        {
            switch (type)
            {
                case BackupType.Backup:
                    return "full-backup";
                case BackupType.Snapshot:
                    return "snapshot";
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private SmugglerResult CreateBackup(string backupFilePath, 
            long? startDocsEtag, DocumentsOperationContext context)
        {
            SmugglerResult result;
            using (var file = File.Open(backupFilePath, FileMode.CreateNew))
            {
                var smugglerSource = new DatabaseSource(_database, startDocsEtag ?? 0);
                var smugglerDestination = new StreamDestination(file, context, smugglerSource);
                var smuggler = new DatabaseSmuggler(
                    smugglerSource,
                    smugglerDestination,
                    _database.Time,
                    new DatabaseSmugglerOptions
                    {
                        RevisionDocumentsLimit = _exportLimit
                    },
                    token: _cancellationToken.Token);

                result = smuggler.Execute();
            }
            return result;
        }

        private static bool DirectoryContainsFullBackupOrSnapshot(string fullPath, BackupType backupType)
        {
            if (Directory.Exists(fullPath) == false)
                return false;

            var files = Directory.GetFiles(fullPath);
            if (files.Length == 0)
                return false;

            var backupExtension = GetFullBackupExtension(backupType);
            return files.Any(file =>
            {
                var extension = Path.GetExtension(file);
                return backupExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
            });
        }

        private async Task WriteStatus(PeriodicBackupStatus status)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            var command = new UpdatePeriodicBackupStatusCommand(_database.Name)
            {
                PeriodicBackupStatus = status
            };

            var etag = await _serverStore.SendToLeaderAsync(command.ToJson());

            if (_logger.IsInfoEnabled)
                _logger.Info($"Periodic backup status with task id {status.TaskId} was updated");

            await _serverStore.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, etag);
        }

        private async Task UploadToServer(
            PeriodicBackupConfiguration configuration,
            PeriodicBackupStatus backupStatus,
            string backupPath, string fileName, bool isFullBackup)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            var tasks = new List<Task>();

            if (CanBackupUsing(configuration.S3Settings))
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (backupStatus.UploadToS3 == null)
                        backupStatus.UploadToS3 = new UploadToS3();

                    var exception = new Reference<Exception>();
                    using (backupStatus.UploadToS3.Update(isFullBackup, exception))
                    {
                        try
                        {
                            await UploadToS3(configuration.S3Settings, backupPath, fileName, isFullBackup, configuration.BackupType);
                        }
                        catch (Exception e)
                        {
                            exception.Value = e;
                            throw;
                        }
                    }
                }));
            }

            if (CanBackupUsing(configuration.GlacierSettings))
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (backupStatus.UploadToGlacier == null)
                        backupStatus.UploadToGlacier = new UploadToGlacier();

                    var exception = new Reference<Exception>();
                    using (backupStatus.UploadToGlacier.Update(isFullBackup, exception))
                    {
                        try
                        {
                            await UploadToGlacier(configuration.GlacierSettings, backupPath, fileName);
                        }
                        catch (Exception e)
                        {
                            exception.Value = e;
                            throw;
                        }
                    }
                }));
            }

            if (CanBackupUsing(configuration.AzureSettings))
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (backupStatus.UploadToAzure == null)
                        backupStatus.UploadToAzure = new UploadToAzure();

                    var exception = new Reference<Exception>();
                    using (backupStatus.UploadToAzure.Update(isFullBackup, exception))
                    {
                        try
                        {
                            await UploadToAzure(configuration.AzureSettings, backupPath, fileName, isFullBackup, configuration.BackupType);
                        }
                        catch (Exception e)
                        {
                            exception.Value = e;
                            throw;
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        private static bool CanBackupUsing(BackupSettings settings)
        {
            return settings != null &&
                   settings.Disabled == false &&
                   settings.HasSettings();
        }

        private async Task UploadToS3(S3Settings settings, string backupPath, string fileName, bool isFullBackup, BackupType backupType)
        {
            if (settings.AwsAccessKey == Constants.Documents.Encryption.DataCouldNotBeDecrypted ||
                settings.AwsSecretKey == Constants.Documents.Encryption.DataCouldNotBeDecrypted)
            {
                throw new InvalidOperationException("Could not decrypt the AWS access settings, " +
                                                    "if you are running on IIS, " +
                                                    "make sure that load user profile is set to true.");
            }

            using (var client = new RavenAwsS3Client(settings.AwsAccessKey, settings.AwsSecretKey, settings.AwsRegionName ?? RavenAwsClient.DefaultRegion))
            using (var fileStream = File.OpenRead(backupPath))
            {
                var key = CombinePathAndKey(settings.RemoteFolderName, fileName);
                await client.PutObject(settings.BucketName, key, fileStream, new Dictionary<string, string>
                {
                    {"Description", GetArchiveDescription(isFullBackup, backupType)}
                }, 60 * 60);

                if (_logger.IsInfoEnabled)
                    _logger.Info(string.Format("Successfully uploaded backup {0} to S3 bucket {1}, " +
                                               "with key {2}", fileName, settings.BucketName, key));
            }
        }

        private async Task UploadToGlacier(GlacierSettings settings, string backupPath, string fileName)
        {

            if (settings.AwsAccessKey == Constants.Documents.Encryption.DataCouldNotBeDecrypted ||
                settings.AwsSecretKey == Constants.Documents.Encryption.DataCouldNotBeDecrypted)
            {
                throw new InvalidOperationException("Could not decrypt the AWS access settings, " +
                                                    "if you are running on IIS, " +
                                                    "make sure that load user profile is set to true.");
            }

            using (var client = new RavenAwsGlacierClient(settings.AwsAccessKey, settings.AwsSecretKey, settings.AwsRegionName ?? RavenAwsClient.DefaultRegion))
            using (var fileStream = File.OpenRead(backupPath))
            {
                var archiveId = await client.UploadArchive(settings.VaultName, fileStream, fileName, 60 * 60);
                if (_logger.IsInfoEnabled)
                    _logger.Info($"Successfully uploaded backup {fileName} to Glacier, archive ID: {archiveId}");
            }
        }

        private async Task UploadToAzure(AzureSettings settings, string backupPath, string fileName, bool isFullBackup, BackupType backupType)
        {
            if (settings.StorageAccount == Constants.Documents.Encryption.DataCouldNotBeDecrypted ||
                settings.StorageKey == Constants.Documents.Encryption.DataCouldNotBeDecrypted)
            {
                throw new InvalidOperationException("Could not decrypt the Azure access settings, " +
                                                    "if you are running on IIS, " +
                                                    "make sure that load user profile is set to true.");
            }

            using (var client = new RavenAzureClient(settings.StorageAccount, settings.StorageKey, settings.StorageContainer))
            {
                await client.PutContainer();
                using (var fileStream = File.OpenRead(backupPath))
                {
                    var key = CombinePathAndKey(settings.RemoteFolderName, fileName);
                    await client.PutBlob(key, fileStream, new Dictionary<string, string>
                    {
                        {"Description", GetArchiveDescription(isFullBackup, backupType)}
                    });

                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Successfully uploaded backup {fileName} " +
                                     $"to Azure container {settings.StorageContainer}, with key {key}");
                }
            }
        }

        private static string CombinePathAndKey(string path, string fileName)
        {
            return string.IsNullOrEmpty(path) == false ? path + "/" + fileName : fileName;
        }

        private string GetArchiveDescription(bool isFullBackup, BackupType backupType)
        {
            var fullBackupText = backupType == BackupType.Backup ? "Full backup" : "A snapshot";
            return $"{(isFullBackup ? fullBackupText : "Incremental backup")} for db {_database.Name} at {SystemTime.UtcNow}";
        }

        public void Dispose()
        {
            using (_cancellationToken)
            {
                _cancellationToken.Cancel();

                foreach (var periodicBackup in _periodicBackups)
                {
                    periodicBackup.Value.DisableFutureBackups();

                    var task = periodicBackup.Value.RunningTask;
                    WaitForTaskCompletion(task);
                }

                foreach (var task in _inactiveRunningPeriodicBackupsTasks)
                {
                    WaitForTaskCompletion(task);
                }
            }
        }

        private void WaitForTaskCompletion(Task task)
        {
            try
            {
                task?.Wait();
            }
            catch (ObjectDisposedException)
            {
                // shutting down, probably
            }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception e)
            {
                if (_logger.IsInfoEnabled)
                    _logger.Info("Error when disposing periodic backup runner task", e);
            }
        }

        private PeriodicBackupStatus GetBackupStatus(long taskId)
        {
            var status = _database.ConfigurationStorage.PeriodicBackupStorage.GetPeriodicBackupStatus(taskId);
            return status ?? new PeriodicBackupStatus();
        }

        public void UpdateConfigurations(DatabaseRecord databaseRecord)
        {
            if (databaseRecord.PeriodicBackups == null)
            {
                foreach (var periodicBackup in _periodicBackups)
                {
                    periodicBackup.Value.DisableFutureBackups();

                    TryAddInactiveRunningPeriodicBackups(periodicBackup.Value.RunningTask);
                }
                return;
            }

            var allBackupTaskIds = new List<long>();
            foreach (var periodicBackup in databaseRecord.PeriodicBackups)
            {
                var newBackupTaskId = periodicBackup.Key;
                allBackupTaskIds.Add(newBackupTaskId);

                var newConfiguration = periodicBackup.Value;
                var taskState = GetTaskStatus(databaseRecord, newConfiguration);

                UpdatePeriodicBackups(newBackupTaskId, newConfiguration, taskState);

                var deletedBackupTaskIds = _periodicBackups.Keys.Except(allBackupTaskIds).ToList();
                foreach (var deletedBackupId in deletedBackupTaskIds)
                {
                    PeriodicBackup deletedBackup;
                    if (_periodicBackups.TryRemove(deletedBackupId, out deletedBackup) == false)
                        continue;

                    // stopping any future backups
                    // currently running backups will continue to run
                    deletedBackup.DisableFutureBackups();
                    TryAddInactiveRunningPeriodicBackups(deletedBackup.RunningTask);
                }
            }
        }

        private void UpdatePeriodicBackups(long taskId, 
            PeriodicBackupConfiguration newConfiguration,
            TaskStatus taskState)
        {
            Debug.Assert(taskId == newConfiguration.TaskId);

            PeriodicBackup existingBackupState;
            if (_periodicBackups.TryGetValue(taskId, out existingBackupState) == false)
            {
                var newPeriodicBackup = new PeriodicBackup
                {
                    Configuration = newConfiguration
                };

                if (taskState == TaskStatus.ActiveByCurrentNode)
                    newPeriodicBackup.BackupTimer = GetTimer(newConfiguration);

                _periodicBackups.TryAdd(taskId, newPeriodicBackup);
                return;
            }

            if (existingBackupState.Configuration.Equals(newConfiguration))
            {
                // the username/password for the cloud backups
                // or the backup frequency might have changed,
                // and it will be reloaded on the next backup re-scheduling
                existingBackupState.Configuration = newConfiguration;
                return;
            }

            // the backup configuration changed
            existingBackupState.BackupTimer?.Dispose();
            TryAddInactiveRunningPeriodicBackups(existingBackupState.RunningTask);
            _periodicBackups.TryRemove(taskId, out _);

            var periodicBackup = new PeriodicBackup
            {
                Configuration = newConfiguration
            };

            if (taskState == TaskStatus.ActiveByCurrentNode)
                periodicBackup.BackupTimer = GetTimer(newConfiguration);

            _periodicBackups.TryAdd(taskId, periodicBackup);
        }

        private enum TaskStatus
        {
            Disabled,
            ActiveByCurrentNode,
            ActiveByOtherNode
        }

        private TaskStatus GetTaskStatus(
            DatabaseRecord databaseRecord, 
            PeriodicBackupConfiguration configuration)
        {
            if (configuration.Disabled)
                return TaskStatus.Disabled;

            var whoseTaskIsIt = databaseRecord.Topology.WhoseTaskIsIt(configuration);
            if (whoseTaskIsIt == null)
                return TaskStatus.Disabled;

            if (whoseTaskIsIt == _serverStore.NodeTag)
                return TaskStatus.ActiveByCurrentNode;

            if (_logger.IsInfoEnabled)
                _logger.Info("Backup job is skipped because it is managed " +
                             $"by '{whoseTaskIsIt}' node and not the current node ({_serverStore.NodeTag})");

            return TaskStatus.ActiveByOtherNode;
        }

        private void TryAddInactiveRunningPeriodicBackups(Task runningTask)
        {
            if (runningTask == null ||
                runningTask.IsCompleted)
                return;

            _inactiveRunningPeriodicBackupsTasks.Add(runningTask);
        }
    }
}