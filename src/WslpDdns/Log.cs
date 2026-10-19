namespace WslpDdns;

/// <summary>
/// Writes log lines to stderr. Under systemd each line gets an sd-daemon "&lt;N&gt;" priority prefix,
/// so journald records real severities (e.g. <c>journalctl -u wslp-ddns -p warning</c>).
/// </summary>
static class Log
{
    static readonly bool ToJournal = StderrIsJournal();

    public static void Info(string message) => Write(6, message);
    public static void Warn(string message) => Write(4, message);
    public static void Error(string message) => Write(3, message);

    static void Write(int priority, string message) =>
        Console.Error.WriteLine(ToJournal ? $"<{priority}>{message}" : message);

    // systemd sets JOURNAL_STREAM=<dev>:<inode> for services; matching the inode against fd 2
    // keeps the prefixes out of an interactive terminal that merely inherited the variable.
    static bool StderrIsJournal()
    {
        var stream = Environment.GetEnvironmentVariable("JOURNAL_STREAM");
        if (stream is null)
            return false;
        try
        {
            var inode = stream[(stream.IndexOf(':') + 1)..];
            return new FileInfo("/proc/self/fd/2").LinkTarget == $"socket:[{inode}]";
        }
        catch (IOException)
        {
            return false;
        }
    }
}
