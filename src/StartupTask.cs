using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast;

/// <summary>Windows.ApplicationModel.StartupTaskState.</summary>
public enum StartupTaskState
{
    /// <summary>Off; the app may turn it on.</summary>
    Disabled = 0,

    /// <summary>The user turned it off in Windows (Settings → Apps → Startup, Task Manager): only they can turn it on again.</summary>
    DisabledByUser = 1,

    Enabled = 2,

    /// <summary>Off by an administrator's policy.</summary>
    DisabledByPolicy = 3,

    /// <summary>On by an administrator's policy.</summary>
    EnabledByPolicy = 4,
}

/// <summary>
/// The packaged app's startup task (Windows.ApplicationModel.StartupTask; the task is declared in
/// packaging/msix/AppxManifest.xml) and how the running copy was activated (Windows.ApplicationModel.AppInstance),
/// called through their WinRT ABI: an activation factory, vtable slots and polling the async operations. No Windows SDK
/// projection, so the target framework stays net10.0-windows and the MSI and zip don't carry it. The GUIDs and slots
/// are those of windows.applicationmodel.h, windows.applicationmodel.activation.h and asyncinfo.h in Windows SDK
/// 10.0.26100.0. Only the packaged app has a startup task: anywhere else these throw (no crash). Every call blocks
/// for a few milliseconds and runs on its own MTA thread pool thread: call it off the UI thread.
/// </summary>
internal static unsafe partial class StartupTaskApi
{
    private const string StartupTaskClass = "Windows.ApplicationModel.StartupTask";
    private const string AppInstanceClass = "Windows.ApplicationModel.AppInstance";
    private static readonly Guid IidStartupTaskStatics = new("ee5b60bd-a148-41a7-b26e-e8b88a1e62f8");
    private static readonly Guid IidAppInstanceStatics = new("9d11e77f-9ea6-47af-a6ec-46784c5ba254");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    // vtable slots: IUnknown 0-2, IInspectable 3-5, then each interface's own methods in order.
    private const int StaticsGetAsync = 7;                                           // IStartupTaskStatics: GetForCurrentPackageAsync, GetAsync
    private const int TaskRequestEnableAsync = 6, TaskDisable = 7, TaskGetState = 8; // IStartupTask: RequestEnableAsync, Disable, get_State, get_TaskId
    private const int OperationGetResults = 8;                                       // IAsyncOperation<T>: put_Completed, get_Completed, GetResults
    private const int InfoGetStatus = 7, InfoGetErrorCode = 8;                       // IAsyncInfo: get_Id, get_Status, get_ErrorCode, Cancel, Close
    private const int InstanceGetActivatedEventArgs = 7;                             // IAppInstanceStatics: get_RecommendedInstance, GetActivatedEventArgs, …
    private const int ArgsGetKind = 6;                                               // IActivatedEventArgs: get_Kind, …

    private const int AsyncCompleted = 1, AsyncCanceled = 2, AsyncError = 3;      // AsyncStatus (0 = Started)
    private const int ActivationKindStartupTask = 1020;
    private const int RoInitMultithreaded = 1;

    /// <summary>The task's state.</summary>
    public static StartupTaskState GetState(string taskId) => OnMta(() => WithTask(taskId, State));

    /// <summary>
    /// Turns the task on, unless the user turned it off in Windows or a policy rules it (then the state says so). A
    /// desktop app's request is granted without asking. Returns the state afterwards.
    /// </summary>
    public static StartupTaskState RequestEnable(string taskId) => OnMta(() => WithTask(taskId, task =>
    {
        IntPtr operation;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(task, TaskRequestEnableAsync))(task, &operation), "RequestEnableAsync");
        try
        {
            Wait(operation);
            int state;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(operation, OperationGetResults))(operation, &state), "RequestEnableAsync result");
            return (StartupTaskState)state;
        }
        finally
        {
            Marshal.Release(operation);
        }
    }));

    /// <summary>Turns the task off. Returns the state afterwards.</summary>
    public static StartupTaskState Disable(string taskId) => OnMta(() => WithTask(taskId, task =>
    {
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(task, TaskDisable))(task), "Disable");
        return State(task);
    }));

    /// <summary>This process was started by the startup task (AppInstance.GetActivatedEventArgs: ActivationKind.StartupTask).</summary>
    public static bool LaunchedByStartupTask() => OnMta(() =>
    {
        IntPtr statics = Factory(AppInstanceClass, IidAppInstanceStatics);
        try
        {
            IntPtr args;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, InstanceGetActivatedEventArgs))(statics, &args), "GetActivatedEventArgs");
            if (args == IntPtr.Zero) return false;
            try
            {
                int kind;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, ArgsGetKind))(args, &kind), "ActivationKind");
                return kind == ActivationKindStartupTask;
            }
            finally
            {
                Marshal.Release(args);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
    });

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs <paramref name="call"/> on a thread pool thread with the Windows Runtime initialized (MTA); rethrows its exception.</summary>
    private static T OnMta<T>(Func<T> call) => Task.Run(() =>
    {
        int init = RoInitialize(RoInitMultithreaded);
        try
        {
            return call();
        }
        finally
        {
            if (init >= 0) RoUninitialize(); // S_OK or S_FALSE; not after RPC_E_CHANGED_MODE
        }
    }).GetAwaiter().GetResult();

    private static T WithTask<T>(string taskId, Func<IntPtr, T> use)
    {
        IntPtr statics = Factory(StartupTaskClass, IidStartupTaskStatics);
        try
        {
            IntPtr task = GetTask(statics, taskId);
            try
            {
                return use(task);
            }
            finally
            {
                Marshal.Release(task);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>The runtime class's activation factory, as <paramref name="iid"/> (its statics interface).</summary>
    private static IntPtr Factory(string className, Guid iid)
    {
        Check(WindowsCreateString(className, (uint)className.Length, out var name), "WindowsCreateString");
        try
        {
            Check(RoGetActivationFactory(name, iid, out var factory), $"RoGetActivationFactory {className}");
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>IStartupTaskStatics.GetAsync(taskId), awaited: the task (an IStartupTask).</summary>
    private static IntPtr GetTask(IntPtr statics, string taskId)
    {
        Check(WindowsCreateString(taskId, (uint)taskId.Length, out var id), "WindowsCreateString");
        IntPtr operation;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, StaticsGetAsync))(statics, id, &operation), "StartupTask.GetAsync");
        }
        finally
        {
            WindowsDeleteString(id); // the callee keeps its own copy
        }
        try
        {
            Wait(operation);
            IntPtr task;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, OperationGetResults))(operation, &task), "StartupTask.GetAsync result");
            return task != IntPtr.Zero ? task : throw new InvalidOperationException($"the package has no startup task \"{taskId}\"");
        }
        finally
        {
            Marshal.Release(operation);
        }
    }

    private static StartupTaskState State(IntPtr task)
    {
        int state;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(task, TaskGetState))(task, &state), "StartupTask.State");
        return (StartupTaskState)state;
    }

    /// <summary>Polls the operation's IAsyncInfo until it is done; throws its error, or after <paramref name="timeoutMs"/>.</summary>
    private static void Wait(IntPtr operation, int timeoutMs = 10_000)
    {
        Check(Marshal.QueryInterface(operation, in IidAsyncInfo, out var info), "IAsyncInfo");
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                int status, error;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, InfoGetStatus))(info, &status), "IAsyncInfo.Status");
                switch (status)
                {
                    case AsyncCompleted:
                        return;
                    case AsyncError:
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, InfoGetErrorCode))(info, &error), "IAsyncInfo.ErrorCode");
                        Check(error < 0 ? error : unchecked((int)0x80004005), "the operation"); // E_FAIL if it gave no error
                        return;
                    case AsyncCanceled:
                        throw new OperationCanceledException("the operation was canceled");
                }
                if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("the operation did not finish");
                Thread.Sleep(5);
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var text = Marshal.GetExceptionForHR(hr)?.Message ?? "failed";
        if (!text.Contains($"{hr:X8}", StringComparison.OrdinalIgnoreCase)) text = $"{text.TrimEnd('.', ' ')} (0x{hr:X8})";
        throw new COMException($"{what}: {text}", hr);
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();
}
