namespace HomePodCast;

public static class Log
{
    private static readonly object Lock = new();
    private static StreamWriter? _file;

    public static bool Verbose { get; set; }
    public static bool ToConsole { get; set; } = true;

    public static void OpenFile(string path)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                File.Move(path, path + ".old", overwrite: true);
            _file = new StreamWriter(path, append: true) { AutoFlush = true };
        }
    }

    public static void CloseFile()
    {
        lock (Lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>Start and end of a <see cref="Name"/> in a log line (the console shows the name without them).</summary>
    public const char NameStart = '‹', NameEnd = '›';

    /// <summary>
    /// A speaker, group member or device name for a log line, marked so that the diagnostic export removes it whatever it
    /// is (DiagnosticReports.Redact): every log line that names one goes through here.
    /// </summary>
    public static string Name(object? name)
    {
        // A marker or a line break inside the name would end the marked span early.
        string text = (name?.ToString() ?? "").Replace(NameStart, '<').Replace(NameEnd, '>').Replace('\r', ' ').Replace('\n', ' ');
        return $"{NameStart}{text}{NameEnd}";
    }

    /// <summary>A log line without the <see cref="Name"/> markers.</summary>
    internal static string Plain(string line) =>
        line.IndexOf(NameStart) < 0 && line.IndexOf(NameEnd) < 0 ? line : line.Replace(NameStart.ToString(), "").Replace(NameEnd.ToString(), "");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Debug(string msg)
    {
        if (Verbose) Write("DEBUG", msg);
    }

    /// <summary>Every line as it is written (tests check what reaches the log).</summary>
    internal static event Action<string>? Written;

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {msg}";
        lock (Lock)
        {
            if (ToConsole) Console.WriteLine(Plain(line));
            _file?.WriteLine(line);
        }
        Written?.Invoke(line);
    }
}
