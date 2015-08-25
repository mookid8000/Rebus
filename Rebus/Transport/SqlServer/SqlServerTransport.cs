﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Bus;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Persistence.SqlServer;
using Rebus.Threading;
using Rebus.Time;
using IDbConnection = Rebus.Persistence.SqlServer.IDbConnection;

namespace Rebus.Transport.SqlServer
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses SQL Server to do its thing
    /// </summary>
    public class SqlServerTransport : ITransport, IInitializable, IDisposable
    {
        readonly AsyncBottleneck _bottleneck = new AsyncBottleneck(20);

        /// <summary>
        /// Special message priority header that can be used with the <see cref="SqlServerTransport"/>. The value must be an <see cref="Int32"/>
        /// </summary>
        public const string MessagePriorityHeaderKey = "rbs2-msg-priority";

        /// <summary>
        /// Default interval that will be used for <see cref="ExpiredMessagesCleanupInterval"/> unless it is explicitly set to something else
        /// </summary>
        public static readonly TimeSpan DefaultExpiredMessagesCleanupInterval = TimeSpan.FromSeconds(20);

        static ILog _log;

        static SqlServerTransport()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        const string CurrentConnectionKey = "sql-server-transport-current-connection";
        const int RecipientColumnSize = 200;

        readonly HeaderSerializer _headerSerializer = new HeaderSerializer();
        readonly IDbConnectionProvider _connectionProvider;
        readonly string _tableName;
        readonly string _inputQueueName;

        readonly AsyncTask _expiredMessagesCleanupTask;
        bool _disposed;

        /// <summary>
        /// Constructs the transport with the given <see cref="IDbConnectionProvider"/>, using the specified <paramref name="tableName"/> to send/receive messages,
        /// querying for messages with recipient = <paramref name="inputQueueName"/>
        /// </summary>
        public SqlServerTransport(IDbConnectionProvider connectionProvider, string tableName, string inputQueueName)
        {
            _connectionProvider = connectionProvider;
            _tableName = tableName;
            _inputQueueName = inputQueueName;

            ExpiredMessagesCleanupInterval = DefaultExpiredMessagesCleanupInterval;

            _expiredMessagesCleanupTask = new AsyncTask("ExpiredMessagesCleanup", PerformExpiredMessagesCleanupCycle)
            {
                Interval = TimeSpan.FromMinutes(1)
            };
        }

        /// <summary>
        /// Last-resort disposal of resoures - shuts down the 'ExpiredMessagesCleanup' background task
        /// </summary>
        ~SqlServerTransport()
        {
            Dispose(false);
        }

        /// <summary>
        /// Initializes the transport by starting a task that deletes expired messages from the SQL table
        /// </summary>
        public void Initialize()
        {
            _expiredMessagesCleanupTask.Start();
        }

        /// <summary>
        /// Configures the interval between periodic deletion of expired messages. Defaults to <see cref="DefaultExpiredMessagesCleanupInterval"/>
        /// </summary>
        public TimeSpan ExpiredMessagesCleanupInterval { get; set; }

        /// <summary>
        /// Gets the name that this SQL transport will use to query by when checking the messages table
        /// </summary>
        public string Address
        {
            get { return _inputQueueName; }
        }

        /// <summary>
        /// The SQL transport doesn't really have queues, so this function does nothing
        /// </summary>
        public void CreateQueue(string address)
        {
        }

        /// <summary>
        /// Checks if the table with the configured name exists - if not, it will be created
        /// </summary>
        public void EnsureTableIsCreated()
        {
            using (var connection = _connectionProvider.GetConnection().Result)
            {
                var tableNames = connection.GetTableNames();

                if (tableNames.Contains(_tableName, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Info("Database already contains a table named '{0}' - will not create anything", _tableName);
                    return;
                }

                _log.Info("Table '{0}' does not exist - it will be created now", _tableName);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"
CREATE TABLE [dbo].[{0}]
(
	[id] [bigint] IDENTITY(1,1) NOT NULL,
	[recipient] [nvarchar](200) NOT NULL,
	[priority] [int] NOT NULL,
    [expiration] [datetime2] NOT NULL,
    [visible] [datetime2] NOT NULL,
	[headers] [varbinary](max) NOT NULL,
	[body] [varbinary](max) NOT NULL,
    CONSTRAINT [PK_{0}] PRIMARY KEY CLUSTERED 
    (
	    [recipient] ASC,
	    [priority] ASC,
	    [id] ASC
    )
)
", _tableName);

                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"

CREATE NONCLUSTERED INDEX [IDX_RECEIVE_{0}] ON [dbo].[{0}]
(
	[recipient] ASC,
	[priority] ASC,
    [visible] ASC,
    [expiration] ASC,
	[id] ASC
)

", _tableName);

                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"

CREATE NONCLUSTERED INDEX [IDX_EXPIRATION_{0}] ON [dbo].[{0}]
(
    [expiration] ASC
)

", _tableName);

                    command.ExecuteNonQuery();
                }

                connection.Complete().Wait();
            }
        }

        /// <summary>
        /// Sends the given transport message to the specified logical destination address by adding it to the messages table.
        /// </summary>
        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            var connection = await GetConnection(context);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = string.Format(@"
INSERT INTO [{0}]
(
    [recipient],
    [headers],
    [body],
    [priority],
    [visible],
    [expiration]
)
VALUES
(
    @recipient,
    @headers,
    @body,
    @priority,
    dateadd(ss, @visible, getdate()),
    dateadd(ss, @ttlseconds, getdate())
)",
                    _tableName);

                var headers = message.Headers.Clone();

                var priority = GetMessagePriority(headers);
                var initialVisibilityDelay = GetInitialVisibilityDelay(headers);
                var ttlSeconds = GetTtlSeconds(headers);

                // must be last because the other functions on the headers might change them
                var serializedHeaders = _headerSerializer.Serialize(headers);

                command.Parameters.Add("recipient", SqlDbType.NVarChar, RecipientColumnSize).Value = destinationAddress;
                command.Parameters.Add("headers", SqlDbType.VarBinary).Value = serializedHeaders;
                command.Parameters.Add("body", SqlDbType.VarBinary).Value = message.Body;
                command.Parameters.Add("priority", SqlDbType.Int).Value = priority;
                command.Parameters.Add("ttlseconds", SqlDbType.Int).Value = ttlSeconds;
                command.Parameters.Add("visible", SqlDbType.Int).Value = initialVisibilityDelay;

                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Receives the next message by querying the messages table for a message with a recipient matching this transport's <see cref="Address"/>
        /// </summary>
        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            using (await _bottleneck.Enter())
            {
                var connection = await GetConnection(context);

                long? idOfMessageToDelete;
                TransportMessage receivedTransportMessage;

                using (var selectCommand = connection.CreateCommand())
                {
                    selectCommand.CommandText = string.Format(@"
	SET NOCOUNT ON

	;WITH TopCTE AS (
		SELECT	TOP 1
				[id],
				[headers],
				[body]
		FROM	{0} M WITH (ROWLOCK, READPAST)
		WHERE	M.[recipient] = @recipient
		AND		M.[visible] < getdate()
		AND		M.[expiration] > getdate()
		ORDER
		BY		[priority] ASC,
				[id] ASC
	)
	DELETE	FROM TopCTE
	OUTPUT	deleted.[id] as [id],
			deleted.[headers] as [headers],
			deleted.[body] as [body]
						
						", _tableName);

                    selectCommand.Parameters.Add("recipient", SqlDbType.NVarChar, RecipientColumnSize).Value = _inputQueueName;

                    using (var reader = await selectCommand.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync()) return null;

                        var headers = reader["headers"];
                        var headersDictionary = _headerSerializer.Deserialize((byte[])headers);

                        idOfMessageToDelete = (long)reader["id"];
                        var body = (byte[])reader["body"];

                        receivedTransportMessage = new TransportMessage(headersDictionary, body);
                    }
                }

                if (!idOfMessageToDelete.HasValue)
                {
                    return null;
                }


                return receivedTransportMessage;
            }
        }

        int GetInitialVisibilityDelay(Dictionary<string, string> headers)
        {
            string deferredUntilDateTimeOffsetString;

            if (!headers.TryGetValue(Headers.DeferredUntil, out deferredUntilDateTimeOffsetString))
            {
                return 0;
            }

            var deferredUntilTime = deferredUntilDateTimeOffsetString.ToDateTimeOffset();

            headers.Remove(Headers.DeferredUntil);

            return (int)(deferredUntilTime - RebusTime.Now).TotalSeconds;
        }

        static int GetTtlSeconds(Dictionary<string, string> headers)
        {
            const int defaultTtlSecondsAbout60Years = int.MaxValue;

            if (!headers.ContainsKey(Headers.TimeToBeReceived))
                return defaultTtlSecondsAbout60Years;

            var timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
            var timeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);

            return (int)timeToBeReceived.TotalSeconds;
        }

        async Task PerformExpiredMessagesCleanupCycle()
        {
            int results;
            var stopwatch = Stopwatch.StartNew();

            using (var connection = await _connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format("DELETE FROM [{0}] WHERE [recipient] = @recipient AND [expiration] < getdate()", _tableName);
                    command.Parameters.Add("recipient", SqlDbType.NVarChar, RecipientColumnSize).Value = _inputQueueName;

                    results = await command.ExecuteNonQueryAsync();
                }

                await connection.Complete();
            }

            if (results > 0)
            {
                _log.Info("Performed expired messages cleanup in {0} - {1} expired messages with recipient {2} were deleted",
                    stopwatch.Elapsed, results, _inputQueueName);
            }
        }

        class HeaderSerializer
        {
            static readonly Encoding DefaultEncoding = Encoding.UTF8;

            public byte[] Serialize(Dictionary<string, string> headers)
            {
                return DefaultEncoding.GetBytes(JsonConvert.SerializeObject(headers));
            }

            public Dictionary<string, string> Deserialize(byte[] bytes)
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(DefaultEncoding.GetString(bytes));
            }
        }

        int GetMessagePriority(Dictionary<string, string> headers)
        {
            var valueOrNull = headers.GetValueOrNull(MessagePriorityHeaderKey);
            if (valueOrNull == null) return 0;

            try
            {
                return int.Parse(valueOrNull);
            }
            catch (Exception exception)
            {
                throw new FormatException(string.Format("Could not parse '{0}' into an Int32!", valueOrNull), exception);
            }
        }

        Task<IDbConnection> GetConnection(ITransactionContext context)
        {
            return context
                .GetOrAdd(CurrentConnectionKey,
                    async () =>
                    {
                        var dbConnection = await _connectionProvider.GetConnection();
                        context.OnCommitted(async () => await dbConnection.Complete());
                        context.OnDisposed(() =>
                        {
                            dbConnection.Dispose();
                        });
                        return dbConnection;
                    });
        }

        /// <summary>
        /// Shuts down the background timer
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        /// <summary>
        /// Shuts down the background timer
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            try
            {
                _expiredMessagesCleanupTask.Dispose();
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}