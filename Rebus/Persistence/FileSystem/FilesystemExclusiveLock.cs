﻿using System;
using System.IO;
using System.Threading;
using Rebus.Logging;

namespace Rebus.Persistence.FileSystem
{
    class FileSystemExclusiveLock : IDisposable
    {
        readonly FileStream _fileStream;
        bool _disposed;

        public FileSystemExclusiveLock(string pathToLock, ILog log)
        {
            EnsureTargetFile(pathToLock, log);

            var success = false;

            //Unfortunately this is the only filesystem locking api that .net exposes
            //You can P/Invoke into better ones but thats not cross-platform
            while (!success)
            {
                try
                {
                    _fileStream = new FileStream(pathToLock, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    // Oh and there's no async version!
#if net46
                    // No more 'Lock' available in .NET Standard Library, so in good faith ..., it will be back in .NET Standard 2.0 & .NET Core 1.2
                    _fileStream.Lock(0, 1);
#endif
                    success = true;
                }
                catch (IOException)
                {
                    success = false;
                    //Have I mentioned that I hate this algorithm?
                    //This basically just causes the thread to yield to the scheduler
                    //we'll be back here more than 1 tick from now
                    Thread.Sleep(TimeSpan.FromTicks(1));
                }
            }
        }

        static void EnsureTargetFile(string pathToLock, ILog log)
        {
            try
            {
                var directoryName = Path.GetDirectoryName(pathToLock);
                if (!Directory.Exists(directoryName))
                {
                    log.Info("Directory {0} does not exist - creating it now", pathToLock);
                    Directory.CreateDirectory(directoryName);
                }
            }
            catch (IOException)
            {
                //Someone else did this under us
            }
            try
            {
                if (!File.Exists(pathToLock))
                {
                    File.WriteAllText(pathToLock, "A");
                }
            }
            catch (IOException)
            {
                //Someone else did this under us
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
#if net46
                // No more 'Lock' available in .NET Standard Library, so in good faith ..., it will be back in .NET Standard 2.0 & .NET Core 1.2
                _fileStream.Unlock(0, 1);
#endif
                _fileStream.Dispose();
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
