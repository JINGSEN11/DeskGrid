using DeskGrid.Core.Services;
using DeskGrid.Win32.Interop;

namespace DeskGrid.Win32.Services;

/// <summary>
/// Out-of-process parent-handle watchdog that blocks with zero CPU on the main DeskGrid
/// process handle via Win32 WaitForMultipleObjects / WaitForSingleObject.
/// If the main process is force-terminated (e.g., TerminateProcess from Task Manager) or crashes,
/// this watchdog wakes immediately and restores all Windows native taskbars within milliseconds.
/// </summary>
public static class TaskbarWatchdog
{
    public static int Run(TaskbarWatchdogOptions options)
    {
        EventWaitHandle? cancelEvent = null;
        nint hParentProcess = 0;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.CancelEventName))
                EventWaitHandle.TryOpenExisting(options.CancelEventName, out cancelEvent);

            hParentProcess = NativeMethods.OpenProcess(
                NativeMethods.SYNCHRONIZE,
                bInheritHandle: false,
                (uint)options.MainProcessId);

            if (hParentProcess == 0)
            {
                // Parent process already exited before we opened its handle.
                if (cancelEvent?.WaitOne(0) != true)
                    TaskbarService.RestoreTaskbar(options.ConfigDirectory);
                return 0;
            }

            bool parentExited;
            if (cancelEvent is not null)
            {
                var safeWaitHandle = cancelEvent.SafeWaitHandle;
                var refAdded = false;
                try
                {
                    safeWaitHandle.DangerousAddRef(ref refAdded);
                    var handles = new[] { hParentProcess, safeWaitHandle.DangerousGetHandle() };
                    var waitResult = NativeMethods.WaitForMultipleObjects(
                        (uint)handles.Length,
                        handles,
                        bWaitAll: false,
                        NativeMethods.INFINITE);

                    // Index 0 = main process exited; Index 1 = explicit cancellation from clean restore
                    parentExited = waitResult == NativeMethods.WAIT_OBJECT_0;
                }
                finally
                {
                    if (refAdded)
                        safeWaitHandle.DangerousRelease();
                }
            }
            else
            {
                var waitResult = NativeMethods.WaitForSingleObject(hParentProcess, NativeMethods.INFINITE);
                parentExited = waitResult == NativeMethods.WAIT_OBJECT_0;
            }

            if (parentExited && cancelEvent?.WaitOne(0) != true)
            {
                TaskbarService.RestoreTaskbar(options.ConfigDirectory);
            }

            return 0;
        }
        catch
        {
            // Last-resort safeguard: if not explicitly cancelled and sentinel remains, restore taskbars.
            if (cancelEvent?.WaitOne(0) != true && TaskbarService.HasSentinelFile(options.ConfigDirectory))
                TaskbarService.RestoreTaskbar(options.ConfigDirectory);
            return 0;
        }
        finally
        {
            if (hParentProcess != 0)
                NativeMethods.CloseHandle(hParentProcess);
            cancelEvent?.Dispose();
        }
    }
}
