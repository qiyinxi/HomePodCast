using System.Runtime.InteropServices;

namespace HomePodCast;

/// <summary>
/// Starting this app again once it has quit (Program.RunGui, after Application.Run has returned): for a new UI language
/// (UI.LanguageMenu) and for 首页's 「立即重启」 after the .NET runtime it started on was replaced (UI/TrayApp.Runtime.cs).
/// The new copy comes back as this one was: the same exe, or the same package through Explorer
/// (<see cref="AppEnvironment.RelaunchCommand"/>); the same environment, so a test profile (HOMEPODCAST_PROFILE) stays
/// one; into the tray when the window was hidden (--tray); connected when it was (--resume=); and as the signed-in
/// user, also when this copy was run as administrator.
///
/// The start is CreateProcess straight from here, not Process.Start: after a .NET update removed the runtime this
/// process started on, nothing it has not loaded yet can be loaded any more (2026-10-13: System.Net.Http), and this
/// needs nothing outside System.Private.CoreLib and this assembly.
/// </summary>
internal static unsafe partial class AppRestart
{
    /// <summary>What the new copy is started for and with.</summary>
    /// <param name="Why">For the log line: "to apply the UI language".</param>
    /// <param name="Tray">Start into the tray (--tray): the window was hidden.</param>
    /// <param name="Connected">Connect at start (--resume=connected) or not (--resume=idle); null: as the settings say (启动后自动连接).</param>
    internal sealed record Request(string Why, bool Tray = false, bool? Connected = null);

    /// <summary>--resume=connected or --resume=idle: what the restarted copy does with the speaker, whatever AutoConnect says.</summary>
    public const string ResumeArgument = "--resume=";

    /// <summary>Set by 「立即重启」 just before it quits: Program.RunGui starts the new copy once the app has quit.</summary>
    public static Request? Requested { get; set; }

    /// <summary>What a restarted copy does with the speaker: connect (true), stay disconnected (false), or as configured (null: an ordinary start).</summary>
    public static bool? Resume(IEnumerable<string> args) =>
        args.LastOrDefault(a => a.StartsWith(ResumeArgument, StringComparison.Ordinal))?[ResumeArgument.Length..] switch
        {
            "connected" => true,
            "idle" => false,
            _ => null,
        };

    /// <summary>The new copy's arguments: --tray, --resume=…</summary>
    internal static string Arguments(Request request)
    {
        var args = new List<string>();
        if (request.Tray) args.Add("--tray");
        if (request.Connected is { } connected) args.Add(ResumeArgument + (connected ? "connected" : "idle"));
        return string.Join(' ', args);
    }

    /// <summary>
    /// What to start. Unpackaged: the exe with <see cref="Arguments"/>. Packaged: the package's application through
    /// Explorer, which passes no arguments on; its runtime is its own (self-contained), so it never restarts for a
    /// replaced runtime, only for a new language, where it never had any.
    /// </summary>
    internal static (string File, string Arguments) Command(Request request, string? packageFamilyName, string exePath, string windowsDirectory)
    {
        var relaunch = AppEnvironment.RelaunchCommand(packageFamilyName, exePath, windowsDirectory);
        return packageFamilyName != null ? relaunch : (relaunch.File, Arguments(request));
    }

    /// <summary>The command for this copy (see <see cref="Command(Request, string?, string, string)"/>).</summary>
    public static (string File, string Arguments) CommandFor(Request request) =>
        Command(request, AppEnvironment.PackageFamilyName, Environment.ProcessPath ?? "HomePodCast.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>The command line CreateProcess gets: the file in quotes (a path has none), then the arguments.</summary>
    internal static string CommandLine(string file, string arguments) =>
        arguments.Length == 0 ? $"\"{file}\"" : $"\"{file}\" {arguments}";

    /// <summary>
    /// This process runs elevated while the signed-in user also has a limited token (UAC on, 「以管理员身份运行」): the new
    /// copy is then started as that user through the shell's process (<see cref="Start"/>). False without UAC, where
    /// everything runs with the full token anyway.
    /// </summary>
    public static bool RunsElevated()
    {
        IntPtr token;
        if (OpenProcessToken(CurrentProcess, TokenQuery, &token) == 0) return false;
        try
        {
            int type = 0, length = 0;
            return GetTokenInformation(token, TokenElevationTypeClass, &type, sizeof(int), &length) != 0 && type == TokenElevationTypeFull;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    /// <summary>
    /// Starts <paramref name="file"/> with <paramref name="arguments"/> and this process's environment; null when it
    /// started, else why not. Never throws.
    /// </summary>
    /// <param name="asShellUser">Start it as the signed-in user: the shell's process (Explorer) becomes its parent, so it gets
    /// the shell's token, not this elevated one. Without a shell to ask, or when that fails, it starts as this process:
    /// back as administrator is better than not back at all.</param>
    public static string? Start(string file, string arguments, bool asShellUser)
    {
        string? throughShell = null;
        if (asShellUser && OpenShellProcess() is var shell && shell != IntPtr.Zero)
        {
            try
            {
                if ((throughShell = Create(file, arguments, shell)) == null) return null;
            }
            finally
            {
                Native.CloseHandle(shell);
            }
        }
        var error = Create(file, arguments, IntPtr.Zero);
        return error == null ? null : throughShell == null ? error : $"{error}; through the shell: {throughShell}";
    }

    /// <summary>One CreateProcess, with <paramref name="parent"/> as the new process's parent unless it is zero; null when it started.</summary>
    private static string? Create(string file, string arguments, IntPtr parent)
    {
        var commandLine = (CommandLine(file, arguments) + "\0").ToCharArray(); // CreateProcessW may write into it
        IntPtr attributes = IntPtr.Zero;
        bool attributesReady = false;
        char* environment = null;
        try
        {
            var info = new StartupInfoEx();
            info.StartupInfo.Size = sizeof(StartupInfo);
            uint flags = CreateNoWindow; // ignored for a window app; a console one (the tests' cmd.exe) gets no window
            if (parent != IntPtr.Zero)
            {
                nuint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, &size); // fails, but says how much it needs
                if (size == 0) return "InitializeProcThreadAttributeList gave no size";
                attributes = Marshal.AllocHGlobal((nint)size);
                attributesReady = InitializeProcThreadAttributeList(attributes, 1, 0, &size) != 0;
                if (!attributesReady || UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeParentProcess, &parent,
                        (nuint)sizeof(IntPtr), IntPtr.Zero, IntPtr.Zero) == 0)
                    return $"the parent process attribute failed with error {Marshal.GetLastPInvokeError()}";
                info.StartupInfo.Size = sizeof(StartupInfoEx);
                info.AttributeList = attributes;
                flags |= ExtendedStartupInfoPresent;
            }
            // This process's environment, also when the parent is the shell: a test profile (HOMEPODCAST_PROFILE) stays one.
            environment = GetEnvironmentStringsW();
            if (environment != null) flags |= CreateUnicodeEnvironment;
            ProcessInformation process;
            fixed (char* application = file, command = commandLine)
            {
                if (CreateProcess(application, command, IntPtr.Zero, IntPtr.Zero, 0, flags, environment, null, &info, &process) == 0)
                    return $"CreateProcess failed with error {Marshal.GetLastPInvokeError()}";
            }
            Native.CloseHandle(process.Thread);
            Native.CloseHandle(process.Process);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (environment != null) FreeEnvironmentStringsW(environment);
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>The process of the desktop's shell (Explorer), opened to be a parent; zero when there is none.</summary>
    private static IntPtr OpenShellProcess()
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero) return IntPtr.Zero;
        uint processId = 0;
        if (GetWindowThreadProcessId(window, &processId) == 0 || processId == 0) return IntPtr.Zero;
        return OpenProcess(ProcessCreateProcess, 0, processId);
    }

    // ---------------------------------------------------------------- Windows (blittable only: nothing to load)

    private static readonly IntPtr CurrentProcess = new(-1);
    private const uint TokenQuery = 0x0008, ProcessCreateProcess = 0x0080;
    private const int TokenElevationTypeClass = 18, TokenElevationTypeFull = 2;
    private const uint CreateUnicodeEnvironment = 0x0000_0400, ExtendedStartupInfoPresent = 0x0008_0000, CreateNoWindow = 0x0800_0000;
    private const nuint ProcThreadAttributeParentProcess = 0x0002_0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    private static partial int CreateProcess(char* applicationName, char* commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        int inheritHandles, uint creationFlags, void* environment, char* currentDirectory, StartupInfoEx* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial char* GetEnvironmentStringsW();

    [LibraryImport("kernel32.dll")]
    private static partial int FreeEnvironmentStringsW(char* environment);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nuint attribute, void* value, nuint size,
        IntPtr previousValue, IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("advapi32.dll")]
    private static partial int OpenProcessToken(IntPtr process, uint access, IntPtr* token);

    [LibraryImport("advapi32.dll")]
    private static partial int GetTokenInformation(IntPtr token, int informationClass, void* information, int length, int* returnLength);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, uint* processId);
}
