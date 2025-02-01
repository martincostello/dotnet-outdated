using System.Diagnostics;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// Runs dot net executable.
    /// </summary>
    /// <remarks>
    /// Credit for the stuff happening in here goes to the https://github.com/jaredcnance/dotnet-status project
    /// </remarks>
    public class DotNetRunner : IDotNetRunner
    {
        public RunStatus Run(string workingDirectory, string[] arguments)
        {
            var psi = new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // HACK See https://github.com/dotnet/msbuild/issues/6753
            psi.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
            psi.EnvironmentVariables["MSBUILDENSURESTDOUTFORTASKPROCESSES"] = "1";

            var p = new Process();
            try
            {
                p.StartInfo = psi;
                p.Start();

                var output = new StringBuilder();
                var errors = new StringBuilder();
                var timeSinceLastOutput = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource();
                var outputTask = ConsumeStreamReaderAsync(p.StandardOutput, timeSinceLastOutput, output, false, cts);
                var errorTask = ConsumeStreamReaderAsync(p.StandardError, timeSinceLastOutput, errors, true, cts);
                bool processExited = false;
                const int Timeout = 20_000;

                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (p.HasExited)
                        {
                            processExited = true;
                            break;
                        }

                        // If output has not been received for a while, then
                        // assume that the process has hung and stop waiting.
                        lock (timeSinceLastOutput)
                        {
                            if (timeSinceLastOutput.ElapsedMilliseconds > Timeout)
                            {
                                cts.Cancel();
                                break;
                            }
                        }

                        Thread.Sleep(100);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }

                cts.Cancel();

                if (!processExited)
                {
                    p.Kill(entireProcessTree: true);

                    return new RunStatus(output.ToString(), errors.ToString(), exitCode: -1);
                }

                Task.WaitAll(outputTask, errorTask);

                return new RunStatus(output.ToString(), errors.ToString(), p.ExitCode);
            }
            finally
            {
                p.Dispose();
            }
        }

        private static async Task ConsumeStreamReaderAsync(
            StreamReader reader,
            Stopwatch timeSinceLastOutput,
            StringBuilder lines,
            bool isStdErr,
            CancellationTokenSource cts)
        {
            await Task.Yield();

            try
            {
                string line;
                while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
                {
                    lock (timeSinceLastOutput)
                    {
                        timeSinceLastOutput.Restart();
                    }

                    lines.AppendLine(line);
                    Console.WriteLine($"[std{(isStdErr ? "err" : "out")}] {line}");
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
        }
    }
}
