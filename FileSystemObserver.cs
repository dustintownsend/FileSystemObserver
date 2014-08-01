using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileSystemObserver
{
    public class FileSystemObserver : IFileSystemObserver
    {
        #region FileSystemObserver enum & structs

        enum FileSystemObserverEventType
        {
            ChangedEventType,
            CreatedEventType,
            DeletedEventType,
            RenamedEventType,
            MovedEventType
        }

        struct FileSystemObserverKey
        {
            public string OldFullPath { get; set; }
            public string OldName { get; set; }
            public string FullPath { get; set; }
            public string Name { get; set; }
            public FileSystemObserverEventType EventType { get; set; }
        }

        struct FileSystemObserverValue
        {
            public DateTime Timestamp { get; set; }
            public FileSystemObserverEventType EventType { get; set; }
        }

        #endregion

        #region IFileSystemObserver Implementation

        public event FileSystemEvent ChangedEvent;
        public event FileSystemEvent CreatedEvent;
        public event FileSystemEvent DeletedEvent;
        public event FileSystemRenameEvent RenamedEvent;

        public void Start()
        {
            _fileSystemWatcher.EnableRaisingEvents = true;
        }

        public void Stop()
        {
            if (_fileSystemWatcher != null)
            {
                _fileSystemWatcher.Dispose();
            }
            _fileSystemWatcher.EnableRaisingEvents = false;
        }

        #endregion

        #region Initialize

        public FileSystemObserver(string observablePath, bool includeSubDirectories)
        {
            // build a list of all existing paths
            _existingPaths = Directory.EnumerateFileSystemEntries(observablePath, "*", SearchOption.AllDirectories).ToList();

            // initialize the _fileSystemWatcher instance
            _fileSystemWatcher.Path = observablePath;
            _fileSystemWatcher.IncludeSubdirectories = includeSubDirectories;
            _fileSystemWatcher.Created += new FileSystemEventHandler(OnCreate);
            _fileSystemWatcher.Changed += new FileSystemEventHandler(OnChange);
            _fileSystemWatcher.Deleted += new FileSystemEventHandler(OnDelete);
            _fileSystemWatcher.Renamed += new RenamedEventHandler(OnRename);
            _fileSystemWatcher.NotifyFilter =
                NotifyFilters.Attributes |
                NotifyFilters.CreationTime |
                NotifyFilters.DirectoryName |
                NotifyFilters.FileName |
                NotifyFilters.LastAccess |
                NotifyFilters.LastWrite |
                NotifyFilters.Security |
                NotifyFilters.Size;

            // initialize the timer instance
            _timer = new Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region Fields

        private readonly FileSystemWatcher _fileSystemWatcher = new FileSystemWatcher();

        private readonly Dictionary<FileSystemObserverKey, FileSystemObserverValue> _pendingEvents =
            new Dictionary<FileSystemObserverKey, FileSystemObserverValue>();
        
        private readonly Timer _timer;
        
        private bool _timerStarted = false;
        
        private static List<string> _existingPaths = new List<string>();

        #endregion

        #region Methods

        private bool IsHidden(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return ((File.GetAttributes(path) & FileAttributes.Hidden) == FileAttributes.Hidden);
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return true;
            }
        }

        private bool IsTempFile(string path)
        {
            if (path == "")
                return false;

            if (IsHidden(path))
            {
                return true;
            }
            else
            {
                FileInfo fi = new FileInfo(path);

                // TODO: add more conditions for temp files
                if (fi.Extension == ".tmp")
                {
                    return true;
                }
                else if (fi.Name.StartsWith("~"))
                {
                    return true;
                }
                else if (fi.Name.EndsWith("~"))
                    return true;
                return false;
            }
        }

        private bool PathExists(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool CheckIfWeWantToFireThisEvent(string path1, string path2 = "")
        {
            // need to check any exceptions. For example temp files.

            // check two paths at once. 
            // If either is a temp file then we don't want to fire the event
            if (!IsTempFile(path1) && !IsTempFile(path2))
            {
                FileInfo fi = new FileInfo(path1);
                // now check out additional exceptions

                // Revit project (.RVT) and Revit family (.RFA) create backup files that we don't wish to track
                if (fi.Extension.ToLower() == ".rvt" || fi.Extension.ToLower() == ".rfa")
                {
                    Console.WriteLine("REVIT FILE");
                    // looking for pattern of filename.****.r**
                    if (Regex.IsMatch(fi.Name, @"((?:[a-z][a-z0-9_]*))\.\d{0,9}\.r(.)(.)"))
                    {
                        Console.WriteLine("REVIT BACKUP FILE");
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }

                // we don't want to track these access temp files
                if (fi.Extension == ".laccdb")
                {
                    return false;
                }

                return true;
            }

            // if it made it this far dont fire it
            return false;
        }

        private Dictionary<FileSystemObserverKey, FileSystemObserverEventType>
            FindReadyEvents(Dictionary<FileSystemObserverKey, FileSystemObserverValue> events)
        {
            Dictionary<FileSystemObserverKey, FileSystemObserverEventType> results =
                new Dictionary<FileSystemObserverKey, FileSystemObserverEventType>();

            DateTime now = DateTime.Now;

            // groupBy FullPath
            foreach (var e in events.GroupBy(x => x.Key.FullPath))
            {
                bool hasDeletedEvent = false;
                bool hasCreatedEvent = false;
                bool hasChangedEvent = false;
                bool hasRenamedEvent = false;

                // more than one event exist for a single path, find out why
                foreach (var i in e)
                {
                    switch (i.Key.EventType)
                    {
                        case FileSystemObserverEventType.ChangedEventType:
                            hasChangedEvent = true;
                            break;
                        case FileSystemObserverEventType.CreatedEventType:
                            hasCreatedEvent = true;
                            break;
                        case FileSystemObserverEventType.DeletedEventType:
                            hasDeletedEvent = true;
                            break;
                        case FileSystemObserverEventType.RenamedEventType:
                            hasRenamedEvent = true;
                            break;
                    }
                }

                if (hasDeletedEvent && hasCreatedEvent)
                {
                    // this happens during a save-as (should be flagged as ChangedEvent instead of CreatedEvent & DeletedEvent)
                    results[e.First().Key] = FileSystemObserverEventType.ChangedEventType;

                    lock (_pendingEvents)
                    {
                        // we need to remove the Deleted and Created events from pending events.
                        foreach (var i in e)
                        {
                            _pendingEvents.Remove(i.Key);
                        }
                    }
                }
                else if (hasCreatedEvent)
                {
                    var entry = e.Where(x => x.Key.EventType == FileSystemObserverEventType.CreatedEventType).FirstOrDefault();

                    double diff = now.Subtract(entry.Value.Timestamp).TotalMilliseconds;
                    if (diff >= 75)
                    {
                        results[entry.Key] = FileSystemObserverEventType.CreatedEventType;
                    }
                }
                else if (hasChangedEvent)
                {
                    var entry = e.Where(x => x.Key.EventType == FileSystemObserverEventType.ChangedEventType).FirstOrDefault();

                    double diff = now.Subtract(entry.Value.Timestamp).TotalMilliseconds;
                    if (diff >= 75)
                    {
                        results[entry.Key] = FileSystemObserverEventType.ChangedEventType;
                    }
                }
                else if (hasDeletedEvent)
                {
                    var entry = e.Where(x => x.Key.EventType == FileSystemObserverEventType.DeletedEventType).FirstOrDefault();

                    double diff = now.Subtract(entry.Value.Timestamp).TotalMilliseconds;
                    if (diff >= 75)
                    {
                        results[entry.Key] = FileSystemObserverEventType.DeletedEventType;
                    }

                }
                else if (hasRenamedEvent)
                {
                    var entry = e.Where(x => x.Key.EventType == FileSystemObserverEventType.RenamedEventType).FirstOrDefault();

                    double diff = now.Subtract(entry.Value.Timestamp).TotalMilliseconds;
                    if (diff >= 75)
                    {
                        results[entry.Key] = FileSystemObserverEventType.RenamedEventType;
                    }
                }
            }

            return results;
        }

        private void FireChangedEvent(FileSystemObserverKey key)
        {
            FileSystemEvent evt = ChangedEvent;
            if (evt != null)
            {
                // make sure the path still exist before throwing an event
                if (PathExists(key.FullPath) && CheckIfWeWantToFireThisEvent(key.FullPath))
                {
                    // okay you can fire the Changed event now
                    evt(key.FullPath);
                }
            }
        }

        private void FireDeletedEvent(FileSystemObserverKey key)
        {
            FileSystemEvent evt = DeletedEvent;
            if (evt != null)
            {
                // if the Path still exist on the fileSystem then it wasn't deleted
                // this can happen during the saving file process.
                // Don't thrown this event unless the path doesn't exist
                if (!PathExists(key.FullPath) && CheckIfWeWantToFireThisEvent(key.FullPath))
                {
                    // remove the path from existing paths
                    _existingPaths.Remove(key.FullPath);

                    // okay you can fire the Deleted event now
                    evt(key.FullPath);
                }
            }
        }

        private void FireCreatedEvent(FileSystemObserverKey key)
        {
            FileSystemEvent evt = CreatedEvent;
            if (evt != null)
            {
                // check if the path still exist before firing this created event. this should filter out some temp files
                if (PathExists(key.FullPath) && CheckIfWeWantToFireThisEvent(key.FullPath))
                {
                    // check if path already exist. Can't create a path that already exist.
                    if (!_existingPaths.Contains(key.FullPath))
                    {
                        // need to add this to the existing paths list
                        _existingPaths.Add(key.FullPath);

                        // okay you can fire the Created event now
                        evt(key.FullPath);
                    }
                }
            }
        }

        private void FireRenamedEvent(FileSystemObserverKey key)
        {
            FileSystemRenameEvent evt = RenamedEvent;
            if (evt != null)
            {
                // can't rename to an existing path
                if (!_existingPaths.Contains(key.FullPath) && CheckIfWeWantToFireThisEvent(key.FullPath))
                {
                    // replace the renamed path
                    _existingPaths.Remove(key.OldFullPath);
                    _existingPaths.Add(key.FullPath);

                    // okay you can fire the Renamed event now
                    evt(key.OldFullPath, key.OldName, key.FullPath, key.Name);
                }
            }
        }

        #endregion

        #region Events Handlers

        private void OnTimeout(object state)
        {
            Dictionary<FileSystemObserverKey, FileSystemObserverEventType> events;

            // block other threads from access pending events while processing
            lock (_pendingEvents)
            {
                // get a list of all events that should be thrown
                events = FindReadyEvents(_pendingEvents);

                // remove events that are going to be used now
                foreach (var e in events)
                {
                    _pendingEvents.Remove(e.Key);
                }

                // stop the timer if there are no more events pending
                if (_pendingEvents.Count == 0)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _timerStarted = false;
                }
            }

            var currentPaths =
                Directory.EnumerateFileSystemEntries(_fileSystemWatcher.Path, "*", SearchOption.AllDirectories).ToList();
            var newFiles = currentPaths.Except(_existingPaths).ToList();
            var deletedFiles = _existingPaths.Except(currentPaths).ToList();

            // fire event for each path by EventType
            foreach (var e in events)
            {
                switch (e.Value)
                {
                    case FileSystemObserverEventType.ChangedEventType:
                        FireChangedEvent(e.Key);
                        break;
                    case FileSystemObserverEventType.CreatedEventType:
                        if (newFiles.Contains(e.Key.FullPath))
                        {
                            newFiles.Remove(e.Key.FullPath);
                            FireCreatedEvent(e.Key);
                        }
                        break;
                    case FileSystemObserverEventType.DeletedEventType:
                        if (deletedFiles.Contains(e.Key.FullPath))
                        {
                            deletedFiles.Remove(e.Key.FullPath);
                            FireDeletedEvent(e.Key);
                        }
                        break;
                    case FileSystemObserverEventType.RenamedEventType:
                        if (deletedFiles.Contains(e.Key.OldFullPath))
                        {
                            deletedFiles.Remove(e.Key.OldFullPath);
                            FireRenamedEvent(e.Key);
                        }
                        break;
                }
            }

            // set existingPaths to currentPaths
            _existingPaths = currentPaths;
        }

        private void OnFileSystemObserverEvent(FileSystemObserverKey key, FileSystemObserverValue value)
        {
            // common method for adding events to pendingEvents collection from the On event handlers

            // block other threads from access pending events while processing
            lock (_pendingEvents)
            {
                // save most recent event
                _pendingEvents[key] = value;

                // start timer if not already started
                if (!_timerStarted)
                {
                    _timer.Change(100, 100);
                    _timerStarted = true;
                }
            }
        }

        private void OnChange(object sender, FileSystemEventArgs e)
        {
            // make sure the path exist on the file system. Changes can't be made to paths that don't exist
            if (File.Exists(e.FullPath) || Directory.Exists(e.FullPath))
            {
                // check if the path exist in the _existingPaths collection
                if (!_existingPaths.Contains(e.FullPath))
                {
                    return;
                }

                FileSystemObserverKey key = new FileSystemObserverKey()
                {
                    Name = new FileInfo(e.Name).Name,
                    FullPath = e.FullPath,
                    EventType = FileSystemObserverEventType.ChangedEventType
                };

                FileSystemObserverValue value = new FileSystemObserverValue()
                {
                    EventType = FileSystemObserverEventType.ChangedEventType,
                    Timestamp = DateTime.Now
                };

                OnFileSystemObserverEvent(key, value);
            }
        }

        private void OnDelete(object sender, FileSystemEventArgs e)
        {
            // check if the path exist in the _existingPaths collection
            if (_existingPaths.Contains(e.FullPath))
            {
                FileSystemObserverKey key = new FileSystemObserverKey()
                {
                    Name = new FileInfo(e.Name).Name,
                    FullPath = e.FullPath,
                    EventType = FileSystemObserverEventType.DeletedEventType
                };

                FileSystemObserverValue value = new FileSystemObserverValue()
                {
                    EventType = FileSystemObserverEventType.DeletedEventType,
                    Timestamp = DateTime.Now
                };

                OnFileSystemObserverEvent(key, value);
            }
        }

        private void OnCreate(object sender, FileSystemEventArgs e)
        {
            // make sure the path exist on the file system. Changes can't be made to paths that don't exist
            if (File.Exists(e.FullPath) || Directory.Exists(e.FullPath))
            {
                FileSystemObserverKey key = new FileSystemObserverKey()
                {
                    Name = new FileInfo(e.Name).Name,
                    FullPath = e.FullPath,
                    EventType = FileSystemObserverEventType.CreatedEventType
                };

                FileSystemObserverValue value = new FileSystemObserverValue()
                {
                    EventType = FileSystemObserverEventType.CreatedEventType,
                    Timestamp = DateTime.Now
                };

                OnFileSystemObserverEvent(key, value);
            }
        }

        private void OnRename(object sender, RenamedEventArgs e)
        {
            FileSystemObserverKey key = new FileSystemObserverKey()
            {
                OldFullPath = e.OldFullPath,
                OldName = new FileInfo(e.OldName).Name,
                FullPath = e.FullPath,
                Name = new FileInfo(e.Name).Name,
                EventType = FileSystemObserverEventType.RenamedEventType
            };

            FileSystemObserverValue value = new FileSystemObserverValue()
            {
                EventType = FileSystemObserverEventType.RenamedEventType,
                Timestamp = DateTime.Now
            };

            OnFileSystemObserverEvent(key, value);
        }

        #endregion
    }
}