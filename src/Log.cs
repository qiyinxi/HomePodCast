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
            if (ToConsole) Console.WriteLine(line);
            _file?.WriteLine(line);
        }
        Written?.Invoke(line);
    }
}
