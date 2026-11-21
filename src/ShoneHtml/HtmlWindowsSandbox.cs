using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShoneHtml;

internal static class HtmlWindowsSandbox
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const nuint ProcThreadAttributeAllApplicationPackagesPolicy = 0x0002000f;
    private const nuint ProcThreadAttributeJobList = 0x0002000d;
    private const uint ProcessCreationAllApplicationPackagesOptOut = 1;
    private const uint JobObjectLimitProcessTime = 0x00000002;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectCpuRateControlEnable = 0x00000001;
    private const uint JobObjectCpuRateControlHardCap = 0x00000004;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int TokenIsAppContainer = 29;

    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    internal static HtmlWindowsSandboxProcess Start(string executablePath, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!IsSupported)
            throw new PlatformNotSupportedException("LPAC process creation requires Windows 10 version 1809 or newer.");
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Sandbox worker executable was not found.", fullPath);

        const string profileName = "ShoneHtml.Renderer.LPAC";
        IntPtr appContainerSid = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr securityCapabilities = IntPtr.Zero;
        IntPtr handleArray = IntPtr.Zero;
        IntPtr policy = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        IntPtr jobList = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        bool attributesInitialized = false;
        Stream? input = null;
        Stream? output = null;
        try
        {
            var hr = CreateAppContainerProfile(profileName, profileName, profileName, IntPtr.Zero, 0, out appContainerSid);
            if (hr == unchecked((int)0x800700b7))
            {
                hr = DeriveAppContainerSidFromAppContainerName(profileName, out appContainerSid);
            }
            if (hr < 0)
                throw new Win32Exception(hr & 0xffff, "Create or derive renderer AppContainer SID failed.");

            using var fromWorker = new HtmlSandboxPipe(PipeDirection.In);
            using var toWorker = new HtmlSandboxPipe(PipeDirection.Out);
            var inheritedHandles = new[] { fromWorker.ChildHandle, toWorker.ChildHandle };
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * inheritedHandles.Length);
            Marshal.Copy(inheritedHandles, 0, handleArray, inheritedHandles.Length);

            securityCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = appContainerSid }, securityCapabilities, false);
            policy = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(policy, unchecked((int)ProcessCreationAllApplicationPackagesOptOut));

            jobHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw NativeFailure("CreateJobObjectW");
            ConfigureJob(jobHandle);
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, jobHandle);

            const uint attributeCount = 4;
            nuint attributeListBytes = 0;
            InitializeProcThreadAttributeListNative(IntPtr.Zero, attributeCount, 0, ref attributeListBytes);
            if (attributeListBytes == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList size query failed.");
            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListBytes));
            if (!InitializeProcThreadAttributeListNative(attributeList, attributeCount, 0, ref attributeListBytes))
                throw NativeFailure("InitializeProcThreadAttributeList");
            attributesInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handleArray,
                    (nuint)(IntPtr.Size * inheritedHandles.Length), IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeSecurityCapabilities, securityCapabilities,
                    (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeAllApplicationPackagesPolicy, policy,
                    sizeof(uint), IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeJobList, jobList, (nuint)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw NativeFailure("UpdateProcThreadAttribute");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x00000100,
                    StdInput = toWorker.ChildHandle,
                    StdOutput = fromWorker.ChildHandle,
                    StdError = IntPtr.Zero
                },
                AttributeList = attributeList
            };
            var startupPtr = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            try
            {
                Marshal.StructureToPtr(startup, startupPtr, false);
                var commandLine = new StringBuilder(Quote(fullPath) + (arguments.Length == 0 ? "" : " " + arguments));
                var minimalEnvironment = CreateMinimalEnvironment(appContainerSid);
                environment = minimalEnvironment;
                if (!CreateProcessW(fullPath, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | 0x08000000,
                        environment, Path.GetDirectoryName(fullPath), startupPtr, out var processInfo))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed for the LPAC worker.");
                processHandle = processInfo.Process;
                threadHandle = processInfo.Thread;
            }
            finally
            {
                Marshal.FreeHGlobal(startupPtr);
            }

            if (!IsProcessInJob(processHandle, jobHandle, out var inJob)) throw NativeFailure("IsProcessInJob");
            if (!inJob) throw new InvalidOperationException("The suspended renderer is not in its configured Job Object.");
            input = fromWorker.TakeHostStream();
            output = toWorker.TakeHostStream();
            TokenState token;
            try { token = GetVerifiedTokenState(processHandle); }
            catch (Win32Exception exception) { throw new IOException($"LPAC token verification failed ({exception.NativeErrorCode}). {exception.Message}", exception); }
            var matchesProfile = StringComparer.Ordinal.Equals(token.AppContainerSid, SidToString(appContainerSid));
            if (!token.IsAppContainer || !token.IsLpac || token.CapabilityCount != 0 || !matchesProfile)
                throw new InvalidOperationException($"Windows did not create the requested capability-free LPAC token (AppContainer={token.IsAppContainer}, LPAC={token.IsLpac}, capabilities={token.CapabilityCount}, matching profile={matchesProfile}).");
            if (ResumeThread(threadHandle) == uint.MaxValue)
                throw NativeFailure("ResumeThread");

            var result = new HtmlWindowsSandboxProcess(fullPath, processHandle, threadHandle, jobHandle,
                input, output, appContainerSid);
            processHandle = threadHandle = jobHandle = appContainerSid = IntPtr.Zero;
            input = output = null;
            return result;
        }
        catch
        {
            if (processHandle != IntPtr.Zero) TerminateProcess(processHandle, 1);
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            input?.Dispose();
            output?.Dispose();
            throw;
        }
        finally
        {
            if (attributesInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (appContainerSid != IntPtr.Zero) FreeSid(appContainerSid);
            if (securityCapabilities != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilities);
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (policy != IntPtr.Zero) Marshal.FreeHGlobal(policy);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    internal static async Task<HtmlWindowsSandboxProcess> StartAsync(string executable, string mode, CancellationToken token)
    {
        var launching = Task.Run(() => Start(executable, mode), token);
        try { return await launching.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = launching.ContinueWith(static pending =>
            {
                if (pending.IsCompletedSuccessfully) pending.Result.Dispose();
                else _ = pending.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static async Task<string> VerifyAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var worker = Path.Combine(AppContext.BaseDirectory, "ShoneHtml.Worker", "ShoneHtml.Worker.exe");
        using var probeDeadline = new CancellationTokenSource(limit);
        var token = probeDeadline.Token;
        using (var process = await StartAsync(worker, "--renderer", token).ConfigureAwait(false))
        {
            var wire = new HtmlRendererProtocol(process.Input, process.Output);
            var hello = await wire.ReadAsync(token).ConfigureAwait(false);
            if (hello.Type != HtmlRendererMessage.Hello || hello.Document != 0 || hello.Correlation != 0)
                throw new InvalidDataException("Worker did not send a valid renderer handshake.");
            process.Kill();
            if (await process.WaitForExitAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Renderer did not report forced termination.");
        }
        await HtmlWindowsSandboxProbe.VerifyAsync(worker, token).ConfigureAwait(false);
        return "LPAC worker verified: Hello, token, Job limits, file/network denial, handle isolation, forced termination.";
    }

    internal static async Task RunWorkerAsync(string[] args, Func<Stream, Stream, Task> render)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(render);
        if (args is not ["--renderer"] and not [HtmlWindowsSandboxProbe.Mode])
            throw new ArgumentException("Worker must be started by the sandbox launcher.", nameof(args));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The renderer worker is Windows-only.");
        using var token = GetCurrentTokenState();
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        if (args is [HtmlWindowsSandboxProbe.Mode])
            await HtmlWindowsSandboxProbe.RunAsync(input, output).ConfigureAwait(false);
        else
            await render(input, output).ConfigureAwait(false);
    }

    private static void ConfigureJob(IntPtr job)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                PerProcessUserTimeLimit = 1_200_000_000,
                LimitFlags = JobObjectLimitProcessTime | JobObjectLimitActiveProcess | JobObjectLimitProcessMemory |
                             JobObjectLimitJobMemory | JobObjectLimitKillOnJobClose,
                ActiveProcessLimit = 1
            },
            ProcessMemoryLimit = (UIntPtr)(256UL * 1024 * 1024),
            JobMemoryLimit = (UIntPtr)(384UL * 1024 * 1024)
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            throw NativeFailure("SetInformationJobObject(extended limits)");
        var cpu = new JobObjectCpuRateControlInformation { ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap, CpuRate = 2000 };
        if (!SetInformationJobObject(job, JobObjectCpuRateControlInformationClass, ref cpu, (uint)Marshal.SizeOf<JobObjectCpuRateControlInformation>()))
            throw NativeFailure("SetInformationJobObject(CPU rate)");
        if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformationClass, out JobObjectExtendedLimitInformation actual,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(), IntPtr.Zero) ||
            !QueryInformationJobObject(job, JobObjectCpuRateControlInformationClass, out JobObjectCpuRateControlInformation actualCpu,
                (uint)Marshal.SizeOf<JobObjectCpuRateControlInformation>(), IntPtr.Zero))
            throw NativeFailure("QueryInformationJobObject");
        if (actual.BasicLimitInformation.LimitFlags != limits.BasicLimitInformation.LimitFlags ||
            actual.BasicLimitInformation.PerProcessUserTimeLimit != limits.BasicLimitInformation.PerProcessUserTimeLimit ||
            actual.BasicLimitInformation.ActiveProcessLimit != limits.BasicLimitInformation.ActiveProcessLimit ||
            actual.ProcessMemoryLimit != limits.ProcessMemoryLimit || actual.JobMemoryLimit != limits.JobMemoryLimit ||
            actualCpu.ControlFlags != cpu.ControlFlags || actualCpu.CpuRate != cpu.CpuRate)
            throw new InvalidOperationException("Windows did not apply the configured renderer Job limits.");
    }

    private static Win32Exception NativeFailure(string stage) =>
        new(Marshal.GetLastWin32Error(), $"{stage} failed.");

    private static TokenState GetVerifiedTokenState(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x000a, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
            return new(ReadTokenFlag(token, TokenIsAppContainer), IsLessPrivilegedAppContainer(token),
                ReadTokenSid(token), ReadTokenGroupCount(token, 30));
    }

    private static SafeAccessTokenHandle GetCurrentTokenState()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x000a, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!ReadTokenFlag(token, TokenIsAppContainer) || !IsLessPrivilegedAppContainer(token) ||
            ReadTokenGroupCount(token, 30) != 0)
        {
            token.Dispose();
            throw new InvalidOperationException("Renderer worker is not running in LPAC.");
        }
        return token;
    }

    private static bool IsLessPrivilegedAppContainer(SafeAccessTokenHandle token)
    {
        if (!ConvertStringSidToSidW("S-1-15-2-1", out var allPackagesSid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!ConvertStringSidToSidW("S-1-15-2-2", out var restrictedPackagesSid))
        {
            LocalFree(allPackagesSid);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!DuplicateTokenEx(token, 0x000c, IntPtr.Zero, 1, 2, out var membershipToken))
        {
            LocalFree(allPackagesSid);
            LocalFree(restrictedPackagesSid);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx for LPAC membership verification failed.");
        }
        using (membershipToken)
        try
        {
            if (!CheckTokenMembershipEx(membershipToken, allPackagesSid, 0x00000003, out var hasAllPackages))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CheckTokenMembershipEx(ALL APPLICATION PACKAGES) failed.");
            if (!CheckTokenMembershipEx(membershipToken, restrictedPackagesSid, 0x00000003, out var hasRestrictedPackages))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CheckTokenMembershipEx(ALL RESTRICTED APPLICATION PACKAGES) failed.");
            return !hasAllPackages && hasRestrictedPackages;
        }
        finally
        {
            LocalFree(allPackagesSid);
            LocalFree(restrictedPackagesSid);
        }
    }

    private static string ReadTokenSid(SafeAccessTokenHandle token)
    {
        var buffer = ReadTokenInformation(token, 31);
        try { return SidToString(Marshal.ReadIntPtr(buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string SidToString(IntPtr sid)
    {
        if (!ConvertSidToStringSidW(sid, out var text)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(text) ?? throw new InvalidOperationException("Could not format AppContainer SID."); }
        finally { LocalFree(text); }
    }

    private static uint ReadTokenGroupCount(SafeAccessTokenHandle token, int informationClass)
    {
        var buffer = ReadTokenInformation(token, informationClass);
        try { return unchecked((uint)Marshal.ReadInt32(buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr ReadTokenInformation(SafeAccessTokenHandle token, int informationClass)
    {
        GetTokenInformationBuffer(token, informationClass, IntPtr.Zero, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        if (!GetTokenInformationBuffer(token, informationClass, buffer, size, out _))
        {
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return buffer;
    }

    private static bool ReadTokenFlag(SafeAccessTokenHandle token, int informationClass)
    {
        if (!GetTokenInformation(token, informationClass, out var value, sizeof(uint), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetTokenInformation(class {informationClass}) failed.");
        return value != 0;
    }

    private static string Quote(string value)
    {
        if (value.Contains('"')) throw new ArgumentException("Executable path contains an invalid quote.", nameof(value));
        return "\"" + value + "\"";
    }

    private static IntPtr CreateMinimalEnvironment(IntPtr appContainerSid)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot)) throw new InvalidOperationException("Windows system directory is unavailable.");
        var profileRoot = GetAppContainerFolder(appContainerSid);
        var drive = Path.GetPathRoot(systemRoot);
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPDATA"] = Path.Combine(profileRoot, "RoamingState"),
            ["LOCALAPPDATA"] = Path.Combine(profileRoot, "LocalState"),
            ["PATH"] = Path.Combine(systemRoot, "System32") + ";" + systemRoot,
            ["SystemRoot"] = systemRoot,
            ["TEMP"] = Path.Combine(profileRoot, "TempState"),
            ["TMP"] = Path.Combine(profileRoot, "TempState"),
            ["USERPROFILE"] = profileRoot,
            ["windir"] = systemRoot
        };
        var block = new StringBuilder();
        if (!string.IsNullOrEmpty(drive)) block.Append('=').Append(drive[0]).Append(":=").Append(drive).Append('\0');
        foreach (var pair in values) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static string GetAppContainerFolder(IntPtr sid)
    {
        var hr = GetAppContainerFolderPath(SidToString(sid), out var path);
        if (hr < 0) throw new Win32Exception(hr & 0xffff, "GetAppContainerFolderPath failed.");
        try { return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException("AppContainer folder path was empty."); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeListNative(IntPtr list, nuint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string appContainerSid, out IntPtr path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobObjectExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobObjectCpuRateControlInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobObjectExtendedLimitInformation information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobObjectCpuRateControlInformation information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, int length, out int returnLength);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);
    [DllImport("kernelbase.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CheckTokenMembershipEx(SafeAccessTokenHandle token, IntPtr sidToCheck, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool isMember);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);

    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int Cb; public string? Reserved; public string? Desktop; public string? Title; public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars; public uint FillAttribute; public uint Flags; public ushort ShowWindow; public ushort Reserved2; public IntPtr Reserved2Pointer; public IntPtr StdInput; public IntPtr StdOutput; public IntPtr StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectCpuRateControlInformation { public uint ControlFlags; public uint CpuRate; }
    private readonly record struct TokenState(bool IsAppContainer, bool IsLpac, string AppContainerSid, uint CapabilityCount);
}

internal sealed class HtmlWindowsSandboxProcess : IDisposable
{
    private IntPtr _process;
    private IntPtr _thread;
    private IntPtr _job;
    private IntPtr _sid;

    internal HtmlWindowsSandboxProcess(string executablePath, IntPtr process, IntPtr thread, IntPtr job,
        Stream input, Stream output, IntPtr sid)
    {
        ExecutablePath = executablePath;
        _process = process;
        _thread = thread;
        _job = job;
        Input = input;
        Output = output;
        _sid = sid;
        ProcessId = GetProcessId(process);
    }

    internal string ExecutablePath { get; }
    internal uint ProcessId { get; }
    internal Stream Input { get; }
    internal Stream Output { get; }

    internal Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_process == IntPtr.Zero) throw new ObjectDisposedException(nameof(HtmlWindowsSandboxProcess));
        var handle = _process;
        return Task.Run(() =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wait = WaitForSingleObject(handle, 100);
                if (wait == 0) break;
                if (wait == 0xffffffff) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (wait != 258) throw new InvalidOperationException($"Unexpected process wait result {wait}.");
            }
            if (!GetExitCodeProcess(handle, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)exitCode);
        }, cancellationToken);
    }

    internal void Kill()
    {
        if (_process != IntPtr.Zero && !TerminateProcess(_process, 1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 5 && error != 87) throw new Win32Exception(error);
        }
    }

    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        if (_process != IntPtr.Zero)
        {
            var wait = WaitForSingleObject(_process, 5_000);
            if (wait == 258)
            {
                TerminateProcess(_process, 1);
                WaitForSingleObject(_process, uint.MaxValue - 1);
            }
            if (_thread != IntPtr.Zero) CloseHandle(_thread);
            CloseHandle(_process);
        }
        else if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
        }
        if (_sid != IntPtr.Zero) FreeSid(_sid);
        _thread = _process = _job = _sid = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);

}
