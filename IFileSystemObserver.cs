// adapted from http://spin.atomicobject.com/2010/07/08/consolidate-multiple-filesystemwatcher-events/

namespace FileSystemObserver
{
    public interface IFileSystemObserver
    {
        event FileSystemEvent ChangedEvent;
        event FileSystemEvent CreatedEvent;
        event FileSystemEvent DeletedEvent;
        event FileSystemRenameEvent RenamedEvent;
        void Start();
        void Stop();
    }

    public delegate void FileSystemEvent(string fullPath);
    public delegate void FileSystemRenameEvent(string oldFullPath, string oldName, 
                                                string fullPath, string name);
}