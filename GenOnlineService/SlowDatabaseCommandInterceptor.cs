/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
*/

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GenOnlineService
{
    // Logs slow commands with operation, duration and outcome only, never SQL or parameters.
    public sealed class SlowDatabaseCommandInterceptor : DbCommandInterceptor
    {
        private readonly ILogger<SlowDatabaseCommandInterceptor> _logger;
        private readonly TimeSpan _threshold;

        public SlowDatabaseCommandInterceptor(IConfiguration configuration, ILogger<SlowDatabaseCommandInterceptor> logger)
        {
            _logger = logger;
            _threshold = TimeSpan.FromMilliseconds(Math.Max(1, configuration.GetValue<double?>("OpenTelemetry:SlowDatabaseCommandMilliseconds") ?? 500));
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Check(eventData.ExecuteMethod, "success", eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Check(eventData.ExecuteMethod, "error", eventData.Duration);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Check(eventData.ExecuteMethod, "error", eventData.Duration);
            return Task.CompletedTask;
        }

        private void Check(DbCommandMethod method, string outcome, TimeSpan duration)
        {
            if (duration >= _threshold)
            {
                _logger.LogWarning("Slow database {DatabaseOperation} command took {ElapsedMilliseconds} ms with outcome {Outcome}",
                    method, duration.TotalMilliseconds, outcome);
            }
        }
    }
}
