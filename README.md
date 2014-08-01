FileSystemObserver
==================
Implementation of FileSystemWatcher that catches Created, Changed, Renamed and Deleted events.

This implementation attempts to handle the quirkiness of FileSystemWatcher
- filters out multiple events fired for a single file/folder change
- Ignore temp files that are created during a save process.
- throws Changed event for a SaveAs instead of Deleted and Created

Example usage

FileSystemObserver fso = new FileSystemObserver(path, true);
fso.ChangedEvent += fso_ChangedEvent;
fso.CreatedEvent += fso_CreatedEvent;
fso.DeletedEvent += fso_DeletedEvent;
fso.RenamedEvent += fso_RenamedEvent;
fso.Start();


void fso_RenamedEvent(string oldFullPath, string oldName, string fullPath, string name)
{
  Console.WriteLine(string.Format("RenamedEvent: {0} | {1}", oldName, name));
}

void fso_DeletedEvent(string path)
{
  Console.WriteLine(string.Format("DeletedEvent: {0}", path));
}

void fso_CreatedEvent(string path)
{
  Console.WriteLine(string.Format("CreatedEvent: {0}", path));
}

void fso_ChangedEvent(string path)
{
  Console.WriteLine(string.Format("ChangedEvent: {0}", path));
}
