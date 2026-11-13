namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    // These tests cover returning useful output after notification registration fails,
    // and closing the terminal before waiting when startup really must fail.
    // The two script-based tests use real terminal output and exit collection, with forced
    // notification errors. The other tests supply collection results to check each branch.
    // Cleanup's blocking wait is replaced with a bounded real wait so failures cannot hang it.
    [TestClass]
    // Watch and HoldSweep belong to the shared reaper. Do not run other tests alongside
    // these replacements; each test restores them in finally, including on assertion failure.
    [DoNotParallelize]
    public class MacStartupTests
    {
        [TestMethod]
        public async Task EsrchWithUnreadOutput_ReturnsConnectionAndReportsActualExitOnce()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Inconclusive("macOS can defer child exit until terminal output is drained");
            }

            // Repeat with a fresh child and marker file, not with a longer script or a sleep.
            // The marker tells us output was written; it does not claim the child has exited.
            for (int iteration = 0; iteration < 5; iteration++)
            {
                string ready = Path.Combine(Path.GetTempPath(), $"porta-startup-{Guid.NewGuid():N}");
                int pid = 0;
                int watches = 0;
                IPtyConnection? terminal = null;
                var exits = new ConcurrentQueue<int>();
                var provider = new Mac.PtyProvider();
                // This provider constructs the connection synchronously on the calling thread.
                // Capture only its child; calls for other children still use the real function.
                int startupThread = Environment.CurrentManagedThreadId;
                var originalWatch = Unix.PtyReaper.Instance.Watch;
                Unix.PtyReaper.Instance.Watch = (queue, p) =>
                {
                    if (Environment.CurrentManagedThreadId != startupThread || (pid != 0 && p != pid))
                    {
                        return originalWatch(queue, p);
                    }

                    Volatile.Write(ref pid, p);
                    Interlocked.Increment(ref watches);
                    // printf precedes touch in the script, so this waits for actual output,
                    // rather than guessing how long process startup takes. Five seconds is
                    // a failure limit, not a delay the script must outlast.
                    SpinWait.SpinUntil(() => File.Exists(ready), 5000).Should().BeTrue();
                    // Native failure has two parts: -1 says it failed, and errno says why.
                    // Replace both attempts, otherwise the retry could succeed and skip the fix.
                    Marshal.SetLastPInvokeError(3); // ESRCH, including the retry after waitpid returns zero.
                    return -1;
                };
                // Register inserts the entry before invoking Watch. Hold the sweep until the
                // startup thread identifies its child, then hold only that child.
                Unix.PtyReaper.Instance.HoldSweep = p => Volatile.Read(ref pid) == 0 || p == Volatile.Read(ref pid);
                try
                {
                    // Nothing reads the terminal until startup returns. On macOS the unread
                    // output can keep exit pending even after the child starts exiting.
                    terminal = await provider.StartTerminalAsync(
                        Options($"printf 'startup-output'; touch '{ready}'; exit 23"),
                        new System.Diagnostics.TraceSource("startup-test"), CancellationToken.None);
                    terminal.ProcessExited += (_, e) => exits.Enqueue(e.ExitCode);
                    watches.Should().Be(2, "both watch attempts fail while the child has unread output");
                    // Read through end-of-file: returning a connection is not enough if output
                    // was discarded. Then let the existing periodic check report the real exit.
                    using var reader = new StreamReader(terminal.ReaderStream);
                    (await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                        .Should().Be("startup-output");
                    Unix.PtyReaper.Instance.HoldSweep = null;
                    (await Task.Run(() => terminal.WaitForExit(10000))).Should().BeTrue();
                    terminal.ExitCode.Should().Be(23);
                    // Wait for two more completed periodic checks before counting callbacks.
                    // This gives duplicate reporting a chance to show up without a fixed sleep.
                    int sweep = Unix.PtyReaper.Instance.Sweeps;
                    (await WaitUntilAsync(() => Unix.PtyReaper.Instance.Sweeps >= sweep + 2)).Should().BeTrue();
                    exits.Should().Equal(23);
                }
                finally
                {
                    Unix.PtyReaper.Instance.Watch = originalWatch;
                    Unix.PtyReaper.Instance.HoldSweep = null;
                    terminal?.Dispose();
                    // If construction failed, the provider owns cleanup; never signal a collected pid.
                    File.Delete(ready);
                }
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void EsrchWithoutConfirmedOutput_RejectsStartup(bool invalidDescriptor)
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Inconclusive("macOS output queue query");
            }

            RequireUnixReaper();
            // No child is spawned here. A synthetic pid identifies the replacement calls;
            // a real, empty terminal pair tests the actual output query without replacing it.
            const int pid = int.MaxValue;
            openpty(out int controller, out int slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero).Should().Be(0);
            var reaper = Unix.PtyReaper.Instance;
            int watches = 0;
            reaper.HoldSweep = p => p == pid;
            var originalWatch = reaper.Watch;
            reaper.Watch = (queue, p) =>
            {
                if (p != pid)
                {
                    return originalWatch(queue, p);
                }

                watches++;
                Marshal.SetLastPInvokeError(3);
                return -1;
            };
            try
            {
                // Both cases must refuse startup: a successful query with zero bytes, and
                // a failed query on -1. Neither confirms useful output is waiting to be read.
                int query = Unix.NativeIo.pty_output_bytes(invalidDescriptor ? -1 : controller, out int bytes);
                int queryError = Marshal.GetLastPInvokeError();
                if (invalidDescriptor)
                {
                    query.Should().Be(-1);
                    queryError.Should().Be(9);
                }
                else
                {
                    query.Should().Be(0);
                    bytes.Should().Be(0);
                }

                // Returning zero from the collection attempt means the exit is not ready.
                // That forces Register to try notifications again before checking for output.
                Action register = () => reaper.Register(pid, (int child, ref int status) => 0,
                    _ => Assert.Fail("no exit status was collected"), invalidDescriptor ? -1 : controller);
                register.Should().Throw<InvalidOperationException>().WithMessage("*errno 3*");
                watches.Should().Be(2);
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.Watch = originalWatch;
                reaper.HoldSweep = null;
                close(controller);
                close(slave);
            }
        }

        // ESRCH is 3; EMFILE (too many open files) is 24. An unrelated error on either
        // notification attempt must still reject startup, naming the retry's error.
        [TestMethod]
        [DataRow(3, 24)]
        [DataRow(24, 3)]
        [DataRow(24, 24)]
        public void UnrelatedWatchErrors_StillThrowWithRetryError(int firstError, int retryError)
        {
            RequireUnixReaper();

            const int pid = int.MaxValue;
            int watches = 0;
            int attempts = 0;
            var reaper = Unix.PtyReaper.Instance;
            reaper.HoldSweep = p => p == pid;
            var originalWatch = reaper.Watch;
            reaper.Watch = (queue, p) =>
            {
                if (p != pid)
                {
                    return originalWatch(queue, p);
                }

                Marshal.SetLastPInvokeError(++watches == 1 ? firstError : retryError);
                return -1;
            };
            try
            {
                Action register = () => reaper.Register(pid, (int child, ref int status) =>
                {
                    attempts++;
                    // A collection call can overwrite the saved native error. Deliberately
                    // give it a different error so the final message must use the watch result.
                    Marshal.SetLastPInvokeError(10); // waitpid must not erase the watch error.
                    return 0;
                }, _ => Assert.Fail("a still-running child has no exit status"));
                register.Should().Throw<InvalidOperationException>().WithMessage($"*errno {retryError}*");
                watches.Should().Be(2);
                attempts.Should().Be(1);
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.Watch = originalWatch;
                reaper.HoldSweep = null;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedWatch_CollectedOrDeferred_DoesNotRearm(bool deferred)
        {
            RequireUnixReaper();

            const int pid = int.MaxValue;
            int watches = 0;
            int reports = 0;
            var reaper = Unix.PtyReaper.Instance;
            reaper.HoldSweep = p => p == pid;
            var originalWatch = reaper.Watch;
            reaper.Watch = (queue, p) =>
            {
                if (p != pid)
                {
                    return originalWatch(queue, p);
                }

                watches++;
                Marshal.SetLastPInvokeError(3);
                return -1;
            };
            try
            {
                reaper.Register(pid, (int child, ref int status) =>
                {
                    // A positive pid means collection succeeded. Deferred means the connection
                    // will collect and report the exit itself. Neither needs another watch.
                    // Unix wait status stores a normal exit code in bits 8 through 15.
                    status = 23 << 8;
                    return deferred ? Unix.PtyReaper.Deferred : child;
                }, status =>
                {
                    status.Should().Be(23 << 8);
                    reports++;
                });
                watches.Should().Be(1);
                reports.Should().Be(deferred ? 0 : 1);
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.Watch = originalWatch;
                reaper.HoldSweep = null;
            }
        }

        [TestMethod]
        public async Task UnexpectedStartupFailure_WithUnreadOutput_ClosesBeforeWaiting()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Inconclusive("macOS terminal close unblocks exit with unread output");
            }

            RequireUnixReaper();
            string ready = Path.Combine(Path.GetTempPath(), $"porta-cleanup-{Guid.NewGuid():N}");
            int watchedPid = 0;
            int pid = 0;
            int fd = -1;
            int watches = 0;
            int startupThread = Environment.CurrentManagedThreadId;
            var provider = new Mac.PtyProvider();
            // As in the successful-startup test, hold periodic collection until we identify
            // the child. The marker is created after output is written, which we leave unread.
            Unix.PtyReaper.Instance.HoldSweep = p => Volatile.Read(ref watchedPid) == 0 || p == Volatile.Read(ref watchedPid);
            var originalWatch = Unix.PtyReaper.Instance.Watch;
            Unix.PtyReaper.Instance.Watch = (queue, p) =>
            {
                if (Environment.CurrentManagedThreadId != startupThread || (watchedPid != 0 && p != watchedPid))
                {
                    return originalWatch(queue, p);
                }

                Volatile.Write(ref watchedPid, p);
                watches++;
                SpinWait.SpinUntil(() => File.Exists(ready), 5000).Should().BeTrue();
                // Force an error outside our allowed ESRCH case. This makes the real
                // connection constructor throw and exercises the provider's cleanup.
                Marshal.SetLastPInvokeError(24); // EMFILE: a real constructor refusal, not the ESRCH fast-exit case.
                return -1;
            };
            int waits = 0;
            bool controllerClosed = false;
            // Inspect the descriptor at the exact point cleanup would normally block in
            // waitpid. Use a bounded real wait instead, so a regression cannot hang the suite.
            provider.WaitForCleanup = (int controller, int child, ref int status, int options) =>
            {
                fd = controller;
                pid = child;
                waits++;
                // fcntl command 1 is F_GETFD: inspect whether this descriptor is still open.
                // -1 with EBADF (9) means close has already happened before this wait.
                int descriptorState = fcntl(fd, 1);
                int descriptorError = Marshal.GetLastPInvokeError();
                controllerClosed = descriptorState == -1 && descriptorError == 9;
                controllerClosed.Should().BeTrue("the controller must be closed before cleanup waits");
                options.Should().Be(0, "production cleanup requests a blocking wait");
                // Exercise real collection without allowing a regression to block the test runner.
                return WaitForChildBounded(child, ref status);
            };
            try
            {
                Func<Task> start = () => provider.StartTerminalAsync(
                    Options($"printf 'unread-output'; touch '{ready}'; exit 31"),
                    new System.Diagnostics.TraceSource("startup-test"), CancellationToken.None);
                await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*errno 24*");
                watches.Should().Be(2);
                pid.Should().Be(watchedPid);
                waits.Should().Be(1);
                int closed = fcntl(fd, 1);
                int closeError = Marshal.GetLastPInvokeError();
                closed.Should().Be(-1, "cleanup owns and closes the terminal descriptor");
                closeError.Should().Be(9); // EBADF
                int status = 0;
                // A second collection attempt must say ECHILD (10): cleanup already
                // collected this child, rather than merely killing it and leaving it behind.
                int collected = waitpid(pid, ref status, 1);
                int waitError = Marshal.GetLastPInvokeError();
                collected.Should().Be(-1, "cleanup collected its child");
                waitError.Should().Be(10); // ECHILD
            }
            finally
            {
                Unix.PtyReaper.Instance.Watch = originalWatch;
                Unix.PtyReaper.Instance.HoldSweep = null;
                // This test's controller is still ours only when cleanup never closed it.
                if (fd >= 0 && !controllerClosed)
                {
                    close(fd);
                }

                // Only signal a known child whose exit is still pending. Once collected,
                // its pid can be reused for an unrelated process and must not be signalled.
                if (pid > 0)
                {
                    int status = 0;
                    if (waitpid(pid, ref status, 1) == 0)
                    {
                        kill(pid, 9);
                        WaitForChildBounded(pid, ref status);
                    }
                }

                File.Delete(ready);
            }
        }

        // Test-only replacement for a blocking wait: WNOHANG (1) returns zero while exit
        // is pending. Retry that result and EINTR (4, interrupted by a signal), with a
        // ten-second limit. Return real status/errors; never manufacture a successful exit.
        private static int WaitForChildBounded(int pid, ref int status)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int result = waitpid(pid, ref status, 1);
                int error = Marshal.GetLastPInvokeError();
                if (result > 0 || (result < 0 && error != 4))
                {
                    Marshal.SetLastPInvokeError(error);
                    return result;
                }

                clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the test's child must be collected within the bound");
                Thread.Sleep(10);
            }
        }

        // The synthetic registration tests can also run on supported Linux kernels.
        // Unsupported platforms are skipped, not counted as successful tests of this behaviour.
        private static void RequireUnixReaper()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Unix reaper registration");
            }

            if (!Unix.PtyReaper.Instance.IsSupported)
            {
                Assert.Inconclusive("this kernel cannot report child exits");
            }
        }

        // Wait for an observed condition, not a guessed duration. The short delay avoids
        // busy-waiting; the timeout makes a missing condition fail instead of hanging.
        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!condition())
                {
                    await Task.Delay(20, timeout.Token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // UseAsyncIo selects the shared reaper whose registration we are testing.
        // /bin/sh interprets the supplied command, including the output and marker sequence.
        private static PtyOptions Options(string command) =>
            new PtyOptions
            {
                Name = "mac-startup",
                Cols = 80,
                Rows = 24,
                Cwd = Environment.CurrentDirectory,
                App = "/bin/sh",
                CommandLine = new[] { "-c", command },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = true,
            };

        [DllImport("libc", SetLastError = true)]
        private static extern int openpty(out int controller, out int slave, IntPtr name, IntPtr term, IntPtr size);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int command);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
