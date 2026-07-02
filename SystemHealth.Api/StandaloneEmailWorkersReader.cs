using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SystemHealth.Api;

internal sealed class StandaloneEmailWorkersReader
{
    private const string CriticalStatus = "Critical";
    private const string DisabledStatus = "Disabled";
    private const string HealthyStatus = "Healthy";
    private const string NeutralStatus = "Neutral";
    private const string RunningStatus = "Running";
    private const string WarningStatus = "Warning";
    private const string PendingMetricLabel = "Pending";
    private const string ProcessingMetricLabel = "Processing";
    private const string SystemQueueWorkerKey = "system-email-queue";
    private const string TriggerQueueWorkerKey = "email-trigger-queue";
    private const string MailchimpWorkerKey = "mailchimp-subscription-sync";

    private readonly SystemHealthOptions _options;

    public StandaloneEmailWorkersReader(SystemHealthOptions options)
    {
        _options = options;
    }

    public async Task<EmailWorkerHealthSnapshotDto> GetAsync(CancellationToken cancellationToken)
    {
        var options = Normalize(_options.EmailWorkers);
        var runtimeSecrets = LoadRuntimeSecrets(options);
        var connectionString = ResolveConnectionString(options, runtimeSecrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return BuildWarning("CRM connection string is not configured for Email Workers.");
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var counts = await ReadCountsAsync(connection, options, cancellationToken);
            var runStates = await ReadRunStatesAsync(connection, options, cancellationToken);
            var deliveryDefaults = await ReadDeliveryDefaultsAsync(connection, options, cancellationToken);

            return BuildSnapshot(
                options.SystemQueue,
                options.TriggerQueue,
                ApplyRuntimeSecrets(options.MailchimpSubscriptionSync, runtimeSecrets),
                deliveryDefaults,
                counts,
                runStates,
                DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return BuildWarning($"Unable to load Email Workers from CRM runtime state. {NormalizeDiagnostic(ex.Message)}");
        }
    }

    private static EmailWorkerHealthSnapshotDto BuildSnapshot(
        SystemEmailQueueWorkerOptions systemQueueOptions,
        EmailTriggerQueueWorkerOptions triggerQueueOptions,
        MailchimpSubscriptionSyncWorkerOptions mailchimpOptions,
        EmailDeliveryDefaultsDto deliveryDefaults,
        EmailWorkerHealthCountsDto counts,
        IReadOnlyDictionary<string, EmailWorkerRunStateDto> runStates,
        DateTime generatedAtUtc)
    {
        var workers = new[]
        {
            BuildSystemQueueWorker(systemQueueOptions, deliveryDefaults, counts, runStates, generatedAtUtc),
            BuildTriggerQueueWorker(triggerQueueOptions, deliveryDefaults, counts, runStates, generatedAtUtc),
            BuildMailchimpWorker(mailchimpOptions, counts, runStates, generatedAtUtc)
        };

        return new EmailWorkerHealthSnapshotDto
        {
            GeneratedAtUtc = generatedAtUtc,
            OverallStatus = BuildOverallStatus(workers),
            Workers = workers
        };
    }

    private static string BuildOverallStatus(IReadOnlyCollection<EmailWorkerHealthItemDto> workers)
    {
        if (workers.Any(worker => string.Equals(worker.Status, CriticalStatus, StringComparison.OrdinalIgnoreCase)))
        {
            return CriticalStatus;
        }

        return workers.Any(worker => string.Equals(worker.Status, WarningStatus, StringComparison.OrdinalIgnoreCase))
            ? WarningStatus
            : HealthyStatus;
    }

    private static EmailWorkerHealthItemDto BuildSystemQueueWorker(
        SystemEmailQueueWorkerOptions options,
        EmailDeliveryDefaultsDto deliveryDefaults,
        EmailWorkerHealthCountsDto counts,
        IReadOnlyDictionary<string, EmailWorkerRunStateDto> runStates,
        DateTime generatedAtUtc)
    {
        runStates.TryGetValue(SystemQueueWorkerKey, out var runState);
        var remainingToday = Math.Max(0, deliveryDefaults.DailySendLimit - counts.SystemSentToday);
        var status = GetBaseStatus(options.Enabled, runState, options.LeaseMinutes, generatedAtUtc);
        if (options.Enabled && (remainingToday == 0 || counts.SystemBlocked > 0 || counts.SystemFailed > 0))
        {
            status = CriticalStatus;
        }

        var worker = CreateWorker(new WorkerSnapshotRequest
        {
            Key = SystemQueueWorkerKey,
            Name = "System Email Queue",
            Enabled = options.Enabled,
            Status = status,
            StatusDetail = GetStatusDetail(status, options.Enabled, runState, remainingToday == 0 ? "Daily send limit reached." : string.Empty),
            PollIntervalSeconds = options.PollIntervalSeconds,
            LeaseMinutes = options.LeaseMinutes,
            BatchSize = deliveryDefaults.WorkerBatchSize,
            RunState = runState,
            Metrics =
            [
                Metric(PendingMetricLabel, counts.SystemPending, NeutralStatus),
                Metric(ProcessingMetricLabel, counts.SystemProcessing, NeutralStatus),
                Metric("Retry scheduled", counts.SystemRetryScheduled, NeutralStatus),
                Metric("Sent today", counts.SystemSentToday, NeutralStatus),
                Metric("Blocked", counts.SystemBlocked, counts.SystemBlocked > 0 ? CriticalStatus : NeutralStatus),
                Metric("Failed", counts.SystemFailed, counts.SystemFailed > 0 ? CriticalStatus : NeutralStatus)
            ]
        });

        worker.DailyLimit = deliveryDefaults.DailySendLimit;
        worker.SentToday = counts.SystemSentToday;
        worker.RemainingToday = remainingToday;
        return worker;
    }

    private static EmailWorkerHealthItemDto BuildTriggerQueueWorker(
        EmailTriggerQueueWorkerOptions options,
        EmailDeliveryDefaultsDto deliveryDefaults,
        EmailWorkerHealthCountsDto counts,
        IReadOnlyDictionary<string, EmailWorkerRunStateDto> runStates,
        DateTime generatedAtUtc)
    {
        runStates.TryGetValue(TriggerQueueWorkerKey, out var runState);
        var status = GetBaseStatus(options.Enabled, runState, options.LeaseMinutes, generatedAtUtc);
        if (options.Enabled && (counts.TriggerError > 0 || counts.TriggerManualReview > 0 || counts.TriggerFailed > 0))
        {
            status = CriticalStatus;
        }

        return CreateWorker(new WorkerSnapshotRequest
        {
            Key = TriggerQueueWorkerKey,
            Name = "Email Trigger Queue",
            Enabled = options.Enabled,
            Status = status,
            StatusDetail = GetStatusDetail(status, options.Enabled, runState, counts.TriggerManualReview > 0 ? "Items need manual review." : string.Empty),
            PollIntervalSeconds = options.PollIntervalSeconds,
            LeaseMinutes = options.LeaseMinutes,
            BatchSize = deliveryDefaults.WorkerBatchSize,
            RunState = runState,
            Metrics =
            [
                Metric(PendingMetricLabel, counts.TriggerPending, NeutralStatus),
                Metric(ProcessingMetricLabel, counts.TriggerProcessing, NeutralStatus),
                Metric("Completed", counts.TriggerCompleted, NeutralStatus),
                Metric("Failed retry", counts.TriggerFailed, counts.TriggerFailed > 0 ? CriticalStatus : NeutralStatus),
                Metric("Manual review", counts.TriggerManualReview, counts.TriggerManualReview > 0 ? CriticalStatus : NeutralStatus),
                Metric("Error", counts.TriggerError, counts.TriggerError > 0 ? CriticalStatus : NeutralStatus)
            ]
        });
    }

    private static EmailWorkerHealthItemDto BuildMailchimpWorker(
        MailchimpSubscriptionSyncWorkerOptions options,
        EmailWorkerHealthCountsDto counts,
        IReadOnlyDictionary<string, EmailWorkerRunStateDto> runStates,
        DateTime generatedAtUtc)
    {
        runStates.TryGetValue(MailchimpWorkerKey, out var runState);
        var worker = CreateWorker(new WorkerSnapshotRequest
        {
            Key = MailchimpWorkerKey,
            Name = "Mailchimp Subscription Sync",
            Enabled = options.Enabled,
            Status = GetBaseStatus(options.Enabled, runState, leaseMinutes: 0, generatedAtUtc),
            StatusDetail = GetStatusDetail(GetBaseStatus(options.Enabled, runState, leaseMinutes: 0, generatedAtUtc), options.Enabled, runState, string.Empty),
            PollIntervalSeconds = options.PollIntervalMinutes * 60,
            LeaseMinutes = 0,
            BatchSize = options.PageSize,
            RunState = runState,
            Metrics =
            [
                Metric("Active Mailchimp unsubscribes", counts.ActiveMailchimpUnsubscribes, NeutralStatus),
                Metric("Active System unsubscribes", counts.ActiveSystemUnsubscribes, NeutralStatus)
            ]
        });

        worker.AudienceCount = options.AudienceIds.Length;
        return worker;
    }

    private static EmailWorkerHealthItemDto CreateWorker(WorkerSnapshotRequest request)
    {
        return new EmailWorkerHealthItemDto
        {
            Key = request.Key,
            Name = request.Name,
            Enabled = request.Enabled,
            Status = request.Status,
            StatusDetail = request.StatusDetail,
            PollIntervalSeconds = request.PollIntervalSeconds,
            LeaseMinutes = request.LeaseMinutes,
            BatchSize = request.BatchSize,
            LastRunStartedAtUtc = request.RunState?.LastRunStartedAtUtc,
            LastRunCompletedAtUtc = request.RunState?.LastRunCompletedAtUtc,
            LastErrorMessage = request.RunState?.LastErrorMessage ?? string.Empty,
            Metrics = request.Metrics
        };
    }

    private static string GetBaseStatus(bool enabled, EmailWorkerRunStateDto? runState, int leaseMinutes, DateTime generatedAtUtc)
    {
        if (!enabled)
        {
            return DisabledStatus;
        }

        if (runState is null)
        {
            return CriticalStatus;
        }

        if (runState.IsRunning)
        {
            return IsRunningLeaseStale(runState, leaseMinutes, generatedAtUtc) ? CriticalStatus : RunningStatus;
        }

        return runState.LastRunSucceeded == false ? CriticalStatus : HealthyStatus;
    }

    private static bool IsRunningLeaseStale(EmailWorkerRunStateDto runState, int leaseMinutes, DateTime generatedAtUtc)
    {
        if (leaseMinutes <= 0 || runState.LastRunStartedAtUtc is null)
        {
            return false;
        }

        var staleAfter = TimeSpan.FromMinutes(Math.Max(leaseMinutes * 2, leaseMinutes + 1));
        return generatedAtUtc - runState.LastRunStartedAtUtc.Value > staleAfter;
    }

    private static string GetStatusDetail(string status, bool enabled, EmailWorkerRunStateDto? runState, string warningDetail)
    {
        if (!enabled)
        {
            return "Worker is disabled in configuration.";
        }

        if (!string.IsNullOrWhiteSpace(warningDetail))
        {
            return warningDetail;
        }

        if (runState is null)
        {
            return "Enabled worker has not recorded a run yet.";
        }

        if (runState.IsRunning)
        {
            return "Worker run is currently in progress.";
        }

        if ((string.Equals(status, WarningStatus, StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, CriticalStatus, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(runState.LastErrorMessage))
        {
            return runState.LastErrorMessage;
        }

        return "Last recorded run completed successfully.";
    }

    private static async Task<EmailWorkerHealthCountsDto> ReadCountsAsync(
        SqlConnection connection,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        var counts = new EmailWorkerHealthCountsDto();
        if (await HasColumnsAsync(connection, "ProcessedEmail", ["EmailWorkerManaged", "EmailWorkerNextAttemptAt", "EmailWorkerLockedUntil", "EmailWorkerBlockedReason", "MailStatusID", "MailSendDate"], options, cancellationToken))
        {
            await ReadSystemQueueCountsAsync(connection, counts, options, cancellationToken);
        }

        if (await HasColumnsAsync(connection, "EmailTriggerQueue", ["QueueStatus"], options, cancellationToken))
        {
            await ReadTriggerQueueCountsAsync(connection, counts, options, cancellationToken);
        }

        if (await HasColumnsAsync(connection, "EmailUnsubscribes", ["IsActive", "SenderType"], options, cancellationToken))
        {
            await ReadUnsubscribeCountsAsync(connection, counts, options, cancellationToken);
        }

        return counts;
    }

    private static async Task ReadSystemQueueCountsAsync(
        SqlConnection connection,
        EmailWorkerHealthCountsDto counts,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    SUM(CASE WHEN MailStatusID = 0 AND (EmailWorkerNextAttemptAt IS NULL OR EmailWorkerNextAttemptAt <= SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Pending,
    SUM(CASE WHEN MailStatusID = 1 OR EmailWorkerLockedUntil > SYSUTCDATETIME() THEN 1 ELSE 0 END) AS Processing,
    SUM(CASE WHEN MailStatusID = 0 AND EmailWorkerNextAttemptAt > SYSUTCDATETIME() THEN 1 ELSE 0 END) AS RetryScheduled,
    SUM(CASE WHEN MailStatusID = 2 AND MailSendDate >= CONVERT(date, GETDATE()) AND MailSendDate < DATEADD(day, 1, CONVERT(date, GETDATE())) THEN 1 ELSE 0 END) AS SentToday,
    SUM(CASE WHEN MailStatusID = 3 AND NULLIF(LTRIM(RTRIM(EmailWorkerBlockedReason)), N'') IS NOT NULL THEN 1 ELSE 0 END) AS Blocked,
    SUM(CASE WHEN MailStatusID = 3 AND NULLIF(LTRIM(RTRIM(EmailWorkerBlockedReason)), N'') IS NULL THEN 1 ELSE 0 END) AS Failed
FROM dbo.ProcessedEmail
WHERE EmailWorkerManaged = 1;";

        await using var command = CreateCommand(sql, connection, options);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        counts.SystemPending = ReadInt(reader, PendingMetricLabel);
        counts.SystemProcessing = ReadInt(reader, ProcessingMetricLabel);
        counts.SystemRetryScheduled = ReadInt(reader, "RetryScheduled");
        counts.SystemSentToday = ReadInt(reader, "SentToday");
        counts.SystemBlocked = ReadInt(reader, "Blocked");
        counts.SystemFailed = ReadInt(reader, "Failed");
    }

    private static async Task ReadTriggerQueueCountsAsync(
        SqlConnection connection,
        EmailWorkerHealthCountsDto counts,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    SUM(CASE WHEN QueueStatus = N'Pending' THEN 1 ELSE 0 END) AS Pending,
    SUM(CASE WHEN QueueStatus = N'Processing' THEN 1 ELSE 0 END) AS Processing,
    SUM(CASE WHEN QueueStatus = N'Completed' THEN 1 ELSE 0 END) AS Completed,
    SUM(CASE WHEN QueueStatus = N'Failed' THEN 1 ELSE 0 END) AS Failed,
    SUM(CASE WHEN QueueStatus = N'ManualReview' THEN 1 ELSE 0 END) AS ManualReview,
    SUM(CASE WHEN QueueStatus = N'Error' THEN 1 ELSE 0 END) AS Error
FROM dbo.EmailTriggerQueue;";

        await using var command = CreateCommand(sql, connection, options);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        counts.TriggerPending = ReadInt(reader, PendingMetricLabel);
        counts.TriggerProcessing = ReadInt(reader, ProcessingMetricLabel);
        counts.TriggerCompleted = ReadInt(reader, "Completed");
        counts.TriggerFailed = ReadInt(reader, "Failed");
        counts.TriggerManualReview = ReadInt(reader, "ManualReview");
        counts.TriggerError = ReadInt(reader, "Error");
    }

    private static async Task ReadUnsubscribeCountsAsync(
        SqlConnection connection,
        EmailWorkerHealthCountsDto counts,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    SUM(CASE WHEN IsActive = 1 AND SenderType = N'Mailchimp' THEN 1 ELSE 0 END) AS ActiveMailchimp,
    SUM(CASE WHEN IsActive = 1 AND SenderType = N'System' THEN 1 ELSE 0 END) AS ActiveSystem
FROM dbo.EmailUnsubscribes;";

        await using var command = CreateCommand(sql, connection, options);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        counts.ActiveMailchimpUnsubscribes = ReadInt(reader, "ActiveMailchimp");
        counts.ActiveSystemUnsubscribes = ReadInt(reader, "ActiveSystem");
    }

    private static async Task<IReadOnlyDictionary<string, EmailWorkerRunStateDto>> ReadRunStatesAsync(
        SqlConnection connection,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnsAsync(connection, "EmailWorkerRunState", ["WorkerKey", "IsRunning", "LastRunStartedAtUtc", "LastRunCompletedAtUtc", "LastRunSucceeded", "LastErrorMessage", "LastUpdatedAtUtc"], options, cancellationToken))
        {
            return new Dictionary<string, EmailWorkerRunStateDto>(StringComparer.OrdinalIgnoreCase);
        }

        const string sql = @"
SELECT
    WorkerKey,
    IsRunning,
    LastRunStartedAtUtc,
    LastRunCompletedAtUtc,
    LastRunSucceeded,
    LastErrorMessage,
    LastUpdatedAtUtc
FROM dbo.EmailWorkerRunState;";

        await using var command = CreateCommand(sql, connection, options);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var records = new Dictionary<string, EmailWorkerRunStateDto>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = new EmailWorkerRunStateDto
            {
                WorkerKey = ReadString(reader, "WorkerKey"),
                IsRunning = Convert.ToBoolean(reader["IsRunning"]),
                LastRunStartedAtUtc = ReadNullableDateTime(reader, "LastRunStartedAtUtc"),
                LastRunCompletedAtUtc = ReadNullableDateTime(reader, "LastRunCompletedAtUtc"),
                LastRunSucceeded = ReadNullableBoolean(reader, "LastRunSucceeded"),
                LastErrorMessage = ReadString(reader, "LastErrorMessage"),
                LastUpdatedAtUtc = ReadNullableDateTime(reader, "LastUpdatedAtUtc")
            };

            records[state.WorkerKey] = state;
        }

        return records;
    }

    private static async Task<EmailDeliveryDefaultsDto> ReadDeliveryDefaultsAsync(
        SqlConnection connection,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnsAsync(connection, "EmailDeliveryDefaults", ["EmailDeliveryDefaultsID", "WorkerBatchSize", "DailySendLimit", "MaximumAttachmentMegabytes", "MaximumImageUploadMegabytes", "MaximumRetryAttempts", "RetryDelayMinutes", "TestModeEnabled", "TestModeRecipient", "LastUpdationDate"], options, cancellationToken))
        {
            return EmailDeliveryDefaultsDto.CreateDefault();
        }

        const string sql = @"
SELECT TOP (1)
    MaximumAttachmentMegabytes,
    MaximumImageUploadMegabytes,
    MaximumRetryAttempts,
    RetryDelayMinutes,
    WorkerBatchSize,
    DailySendLimit,
    TestModeEnabled,
    TestModeRecipient,
    LastUpdationDate
FROM dbo.EmailDeliveryDefaults
WHERE EmailDeliveryDefaultsID = 1;";

        await using var command = CreateCommand(sql, connection, options);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EmailDeliveryDefaultsDto
            {
                MaximumAttachmentMegabytes = ReadInt(reader, "MaximumAttachmentMegabytes"),
                MaximumImageUploadMegabytes = ReadInt(reader, "MaximumImageUploadMegabytes"),
                MaximumRetryAttempts = ReadInt(reader, "MaximumRetryAttempts"),
                RetryDelayMinutes = ReadInt(reader, "RetryDelayMinutes"),
                WorkerBatchSize = ReadInt(reader, "WorkerBatchSize"),
                DailySendLimit = ReadInt(reader, "DailySendLimit"),
                TestModeEnabled = Convert.ToBoolean(reader["TestModeEnabled"]),
                TestModeRecipient = ReadString(reader, "TestModeRecipient"),
                LastUpdated = ReadNullableDateTime(reader, "LastUpdationDate")
            }
            : EmailDeliveryDefaultsDto.CreateDefault();
    }

    private static async Task<bool> HasColumnsAsync(
        SqlConnection connection,
        string tableName,
        IReadOnlyCollection<string> columnNames,
        EmailWorkersOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
WHERE s.name = N'dbo'
  AND t.name = @TableName
  AND c.name IN ({0});";

        var parameters = columnNames.Select((_, index) => $"@Column{index}").ToArray();
        await using var command = CreateCommand(string.Format(sql, string.Join(",", parameters)), connection, options);
        command.Parameters.AddWithValue("@TableName", tableName);
        var index = 0;
        foreach (var columnName in columnNames)
        {
            command.Parameters.AddWithValue(parameters[index], columnName);
            index++;
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == columnNames.Count;
    }

    private static SqlCommand CreateCommand(string sql, SqlConnection connection, EmailWorkersOptions options)
    {
        return new SqlCommand(sql, connection)
        {
            CommandTimeout = options.SqlCommandTimeoutSeconds
        };
    }

    private static RuntimeEmailWorkerSecrets LoadRuntimeSecrets(EmailWorkersOptions options)
    {
        var path = ResolveRuntimeSecretsPath(options);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return RuntimeEmailWorkerSecrets.Empty;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new RuntimeEmailWorkerSecrets(
            ReadString(root, "ConnectionStrings", "CRM"),
            ReadString(root, "EmailWorkers", "MailchimpSubscriptionSync", "ApiKey"),
            ReadStringArray(root, "EmailWorkers", "MailchimpSubscriptionSync", "AudienceIds"));
    }

    private static string ResolveRuntimeSecretsPath(EmailWorkersOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimeSecretsPath))
        {
            return options.RuntimeSecretsPath;
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeSecretsRootPath)
            && !string.IsNullOrWhiteSpace(options.RuntimeSecretsSiteName))
        {
            return Path.Combine(options.RuntimeSecretsRootPath, options.RuntimeSecretsSiteName, "secrets.json");
        }

        return string.Empty;
    }

    private static string ResolveConnectionString(EmailWorkersOptions options, RuntimeEmailWorkerSecrets secrets)
    {
        return !string.IsNullOrWhiteSpace(options.CrmConnectionString)
            ? options.CrmConnectionString
            : secrets.CrmConnectionString;
    }

    private static MailchimpSubscriptionSyncWorkerOptions ApplyRuntimeSecrets(
        MailchimpSubscriptionSyncWorkerOptions options,
        RuntimeEmailWorkerSecrets secrets)
    {
        return new MailchimpSubscriptionSyncWorkerOptions
        {
            Enabled = options.Enabled,
            ApiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? secrets.MailchimpApiKey : options.ApiKey,
            AudienceIds = options.AudienceIds.Length > 0 ? options.AudienceIds : secrets.MailchimpAudienceIds,
            PollIntervalMinutes = options.PollIntervalMinutes,
            PageSize = options.PageSize
        };
    }

    private static EmailWorkersOptions Normalize(EmailWorkersOptions options)
    {
        return new EmailWorkersOptions
        {
            CrmConnectionString = options.CrmConnectionString,
            RuntimeSecretsPath = options.RuntimeSecretsPath,
            RuntimeSecretsRootPath = options.RuntimeSecretsRootPath,
            RuntimeSecretsSiteName = options.RuntimeSecretsSiteName,
            SqlCommandTimeoutSeconds = Math.Clamp(options.SqlCommandTimeoutSeconds, 5, 120),
            SystemQueue = new SystemEmailQueueWorkerOptions
            {
                Enabled = options.SystemQueue.Enabled,
                PollIntervalSeconds = Math.Clamp(options.SystemQueue.PollIntervalSeconds, 5, 3600),
                LeaseMinutes = Math.Clamp(options.SystemQueue.LeaseMinutes, 1, 120)
            },
            TriggerQueue = new EmailTriggerQueueWorkerOptions
            {
                Enabled = options.TriggerQueue.Enabled,
                PollIntervalSeconds = Math.Clamp(options.TriggerQueue.PollIntervalSeconds, 5, 3600),
                LeaseMinutes = Math.Clamp(options.TriggerQueue.LeaseMinutes, 1, 120)
            },
            MailchimpSubscriptionSync = new MailchimpSubscriptionSyncWorkerOptions
            {
                Enabled = options.MailchimpSubscriptionSync.Enabled,
                ApiKey = options.MailchimpSubscriptionSync.ApiKey,
                AudienceIds = options.MailchimpSubscriptionSync.AudienceIds,
                PollIntervalMinutes = Math.Clamp(options.MailchimpSubscriptionSync.PollIntervalMinutes, 15, 1440),
                PageSize = Math.Clamp(options.MailchimpSubscriptionSync.PageSize, 100, 1000)
            }
        };
    }

    private static EmailWorkerHealthMetricDto Metric(string label, int value, string status)
    {
        return new EmailWorkerHealthMetricDto
        {
            Label = label,
            Value = value,
            Status = status
        };
    }

    private static EmailWorkerHealthSnapshotDto BuildWarning(string detail)
    {
        return new EmailWorkerHealthSnapshotDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            OverallStatus = WarningStatus,
            Workers =
            [
                new EmailWorkerHealthItemDto
                {
                    Key = "email-workers-runtime",
                    Name = "Email Workers Runtime",
                    Enabled = false,
                    Status = WarningStatus,
                    StatusDetail = detail,
                    Metrics = Array.Empty<EmailWorkerHealthMetricDto>()
                }
            ]
        };
    }

    private static int ReadInt(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? 0 : Convert.ToInt32(value);
    }

    private static bool? ReadNullableBoolean(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? null : Convert.ToBoolean(value);
    }

    private static DateTime? ReadNullableDateTime(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc);
    }

    private static string ReadString(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
    }

    private static string ReadString(JsonElement root, params string[] path)
    {
        return TryGetElement(root, path, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string[] ReadStringArray(JsonElement root, params string[] path)
    {
        if (!TryGetElement(root, path, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static bool TryGetElement(JsonElement root, IReadOnlyList<string> path, out JsonElement element)
    {
        element = root;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeDiagnostic(string detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record RuntimeEmailWorkerSecrets(
        string CrmConnectionString,
        string MailchimpApiKey,
        string[] MailchimpAudienceIds)
    {
        public static RuntimeEmailWorkerSecrets Empty { get; } = new(string.Empty, string.Empty, Array.Empty<string>());
    }

    private sealed class WorkerSnapshotRequest
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public string Status { get; init; } = string.Empty;
        public string StatusDetail { get; init; } = string.Empty;
        public int PollIntervalSeconds { get; init; }
        public int LeaseMinutes { get; init; }
        public int BatchSize { get; init; }
        public EmailWorkerRunStateDto? RunState { get; init; }
        public IReadOnlyList<EmailWorkerHealthMetricDto> Metrics { get; init; } = Array.Empty<EmailWorkerHealthMetricDto>();
    }
}

internal sealed class EmailWorkersOptions
{
    public string CrmConnectionString { get; set; } = string.Empty;
    public string RuntimeSecretsPath { get; set; } = string.Empty;
    public string RuntimeSecretsRootPath { get; set; } = @"C:\ProgramData\FHX\CRM\secrets";
    public string RuntimeSecretsSiteName { get; set; } = string.Empty;
    public int SqlCommandTimeoutSeconds { get; set; } = 15;
    public SystemEmailQueueWorkerOptions SystemQueue { get; set; } = new();
    public EmailTriggerQueueWorkerOptions TriggerQueue { get; set; } = new();
    public MailchimpSubscriptionSyncWorkerOptions MailchimpSubscriptionSync { get; set; } = new();
}

internal sealed class SystemEmailQueueWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public int LeaseMinutes { get; set; } = 10;
}

internal sealed class EmailTriggerQueueWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
    public int LeaseMinutes { get; set; } = 10;
}

internal sealed class MailchimpSubscriptionSyncWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string[] AudienceIds { get; set; } = Array.Empty<string>();
    public int PollIntervalMinutes { get; set; } = 360;
    public int PageSize { get; set; } = 1000;
}

internal sealed class EmailDeliveryDefaultsDto
{
    public int MaximumAttachmentMegabytes { get; set; }
    public int MaximumImageUploadMegabytes { get; set; }
    public int MaximumRetryAttempts { get; set; }
    public int RetryDelayMinutes { get; set; }
    public int WorkerBatchSize { get; set; }
    public int DailySendLimit { get; set; }
    public bool TestModeEnabled { get; set; }
    public string TestModeRecipient { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }

    public static EmailDeliveryDefaultsDto CreateDefault()
    {
        return new EmailDeliveryDefaultsDto
        {
            MaximumAttachmentMegabytes = 10,
            MaximumImageUploadMegabytes = 2,
            MaximumRetryAttempts = 3,
            RetryDelayMinutes = 15,
            WorkerBatchSize = 25,
            DailySendLimit = 1000,
            TestModeEnabled = false,
            TestModeRecipient = "admin@fhx.co.nz"
        };
    }
}

internal sealed class EmailWorkerHealthCountsDto
{
    public int SystemPending { get; set; }
    public int SystemProcessing { get; set; }
    public int SystemRetryScheduled { get; set; }
    public int SystemSentToday { get; set; }
    public int SystemBlocked { get; set; }
    public int SystemFailed { get; set; }
    public int TriggerPending { get; set; }
    public int TriggerProcessing { get; set; }
    public int TriggerCompleted { get; set; }
    public int TriggerFailed { get; set; }
    public int TriggerManualReview { get; set; }
    public int TriggerError { get; set; }
    public int ActiveMailchimpUnsubscribes { get; set; }
    public int ActiveSystemUnsubscribes { get; set; }
}

internal sealed class EmailWorkerRunStateDto
{
    public string WorkerKey { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public DateTime? LastRunStartedAtUtc { get; set; }
    public DateTime? LastRunCompletedAtUtc { get; set; }
    public bool? LastRunSucceeded { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public DateTime? LastUpdatedAtUtc { get; set; }
}

internal sealed class EmailWorkerHealthSnapshotDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public string OverallStatus { get; set; } = "Healthy";
    public IReadOnlyList<EmailWorkerHealthItemDto> Workers { get; set; } = Array.Empty<EmailWorkerHealthItemDto>();
}

internal sealed class EmailWorkerHealthItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Status { get; set; } = "Unknown";
    public string StatusDetail { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; }
    public int LeaseMinutes { get; set; }
    public int? BatchSize { get; set; }
    public int? DailyLimit { get; set; }
    public int? SentToday { get; set; }
    public int? RemainingToday { get; set; }
    public int? AudienceCount { get; set; }
    public DateTime? LastRunStartedAtUtc { get; set; }
    public DateTime? LastRunCompletedAtUtc { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public IReadOnlyList<EmailWorkerHealthMetricDto> Metrics { get; set; } = Array.Empty<EmailWorkerHealthMetricDto>();
}

internal sealed class EmailWorkerHealthMetricDto
{
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Status { get; set; } = "Neutral";
}
