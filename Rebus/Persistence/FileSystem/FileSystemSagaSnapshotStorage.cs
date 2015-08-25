﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Auditing.Sagas;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Sagas;

namespace Rebus.Persistence.FileSystem
{
    /// <summary>
    /// Implementation of <see cref="ISagaSnapshotStorage"/> that writes saga data snapshots as JSON text to a directory in the file system
    /// </summary>
    public class FileSystemSagaSnapshotStorage : ISagaSnapshotStorage, IInitializable
    {
        static ILog _log;

        static FileSystemSagaSnapshotStorage()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly string _snapshotDirectory;

        /// <summary>
        /// Constructs the snapshot storage which will write saga data snapshots to files using file names on the form "ID-REVISION.json"
        /// </summary>
        public FileSystemSagaSnapshotStorage(string snapshotDirectory)
        {
            if (snapshotDirectory == null) throw new ArgumentNullException("snapshotDirectory");

            _snapshotDirectory = snapshotDirectory;
        }

        public void Initialize()
        {
            if (Directory.Exists(_snapshotDirectory)) return;

            _log.Info("Saga snapshot directory '{0}' does not exist - creating it!", _snapshotDirectory);

            Directory.CreateDirectory(_snapshotDirectory);

            var writabilityCheckFilePath = Path.Combine(_snapshotDirectory, "rebus.writability.check.txt");

            try
            {
                File.WriteAllText(writabilityCheckFilePath, "RBS2!1");
            }
            catch (Exception exception)
            {
                var message = string.Format("Could not write dummy file to saga snapshot directory '{0}' - is it writable for the {1} / {2} account?",
                    _snapshotDirectory, Environment.UserDomainName, Environment.UserName);

                throw new IOException(message, exception);
            }
            finally
            {
                try
                {
                    File.Delete(writabilityCheckFilePath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public async Task Save(ISagaData sagaData, Dictionary<string, string> sagaAuditMetadata)
        {
            var jsonText = JsonConvert.SerializeObject(new Snapshot
            {
                Data = sagaData,
                Metadata = sagaAuditMetadata
            }, Formatting.Indented);

            var snapshotFilePath = Path.Combine(_snapshotDirectory, string.Format("{0:N}-{1}.json", sagaData.Id, sagaData.Revision));

            using (var file = File.OpenWrite(snapshotFilePath))
            {
                using (var writer = new StreamWriter(file, Encoding.UTF8))
                {
                    await writer.WriteAsync(jsonText);
                }
            }
        }

        class Snapshot
        {
            public Dictionary<string, string> Metadata { get; set; }
            public ISagaData Data { get; set; }
        }
    }
}