using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>Why something of OptiScaler couldn't be done, with a code that the page turns into words.</summary>
    public class OptiScalerException : Exception
    {
        /// <summary>The program started and the bridge never spoke: it isn't loaded in this version.</summary>
        public const string NoAnswer = "noanswer";

        /// <summary>Windows doesn't let the program start: an antivirus, Smart App Control or a rule.</summary>
        public const string NoStart = "nostart";

        /// <summary>The program ended in the middle.</summary>
        public const string Closed = "closed";

        /// <summary>It didn't answer in time, and it may still be at it.</summary>
        public const string Slow = "slow";

        /// <summary>It's changing a game, maybe another one.</summary>
        public const string Busy = "busy";

        /// <summary>The program ended before this question got to it.</summary>
        public const string NotSent = "notsent";

        public string Code { get; }

        public OptiScalerException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>How far a question that takes long has gone.</summary>
    public class OptiScalerProgress
    {
        /// <summary>look, download, extract, install, piece, uninstall or check.</summary>
        public string Step { get; set; }
        public string Item { get; set; }
        public double? Percent { get; set; }

        /// <summary>Something of the game was already changed by this question.</summary>
        public bool Touched { get; set; }

        /// <summary>Nothing of the game is being touched yet: it can be stopped without leaving anything half done.</summary>
        public bool CanStop => !Touched && IsBefore(Step);

        public static bool IsBefore(string step)
        {
            return step == "look" || step == "download" || step == "extract";
        }
    }

    /// <summary>
    /// OptiScaler Client working without a window, with the bridge inside, and the conversation with it: one line of JSON
    /// for each question and each answer, through a pipe that only this user can open.
    /// </summary>
    public sealed class OptiScalerBridge : IDisposable
    {
        private const int Protocol = 1;
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly UTF8Encoding utf8 = new UTF8Encoding(false);

        private readonly Process process;
        private readonly NamedPipeServerStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim asking = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> waiting = new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private Action<OptiScalerProgress> progress;
        private volatile bool touched;
        private volatile string working;
        private int lastId;
        private volatile bool closed;
        private int disposed;

        public string Executable { get; }
        public bool IsClosed => closed;

        /// <summary>It's still at a question, even if nobody waits for its answer anymore.</summary>
        public bool IsBusy => working != null && !closed;

        /// <summary>It's installing in a game, changing one or taking OptiScaler out of it.</summary>
        public bool IsChanging => !closed && (working == "install" || working == "apply" || working == "uninstall");

        private OptiScalerBridge(string executable, Process process, NamedPipeServerStream pipe)
        {
            Executable = executable;
            this.process = process;
            this.pipe = pipe;
            reader = new StreamReader(pipe, utf8);
            writer = new StreamWriter(pipe, utf8) { NewLine = "\n" };
        }

        private static NamedPipeServerStream CreatePipe(string name)
        {
            // Only the person that runs Atrio, and nobody else on the computer.
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        /// <summary>The folder of the data is the usual one, unless a copy is only being tried.</summary>
        public static async Task<OptiScalerBridge> StartAsync(string executable, CancellationToken token, string dataFolder = null)
        {
            if (!File.Exists(OptiScalerClient.BridgePath))
            {
                throw new OptiScalerException(OptiScalerException.NoStart, $"{OptiScalerClient.BridgePath} isn't there.");
            }

            var name = $"atrio-optiscaler-{Guid.NewGuid():N}";
            var secret = Guid.NewGuid().ToString("N");
            var pipe = CreatePipe(name);
            Process process = null;
            OptiScalerBridge bridge = null;
            try
            {
                var info = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    RedirectStandardError = true
                };
                info.EnvironmentVariables["DOTNET_STARTUP_HOOKS"] = OptiScalerClient.BridgePath;
                info.EnvironmentVariables["ATRIO_OPTISCALER_MODE"] = "service";
                info.EnvironmentVariables["ATRIO_OPTISCALER_PIPE"] = name;
                info.EnvironmentVariables["ATRIO_OPTISCALER_TOKEN"] = secret;
                var data = dataFolder ?? OptiScalerClient.DataFolder;
                if (data != null)
                {
                    info.EnvironmentVariables["OPTISCALER_CLIENT_APPDATA"] = data;
                }

                process = new Process { StartInfo = info };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        logger.Debug($"OptiScaler Client: {e.Data}");
                    }
                };
                try
                {
                    process.Start();
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
                {
                    process = null;
                    throw new OptiScalerException(OptiScalerException.NoStart, e.Message);
                }

                process.BeginErrorReadLine();
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(25));
                    var connected = pipe.WaitForConnectionAsync(limit.Token);
                    while (await Task.WhenAny(connected, Task.Delay(200)) != connected)
                    {
                        if (process.HasExited)
                        {
                            limit.Cancel();
                            throw new OptiScalerException(OptiScalerException.NoAnswer, $"The program ended with {process.ExitCode} before speaking.");
                        }
                    }

                    try
                    {
                        await connected;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new OptiScalerException(OptiScalerException.NoAnswer, "The bridge didn't connect.");
                    }
                }

                bridge = new OptiScalerBridge(executable, process, pipe);
                var hello = await bridge.ReadGreetingAsync(token);
                if ((string)hello?["hello"] != secret || (int?)hello["protocol"] != Protocol)
                {
                    throw new OptiScalerException(OptiScalerException.NoAnswer, "The greeting of the bridge isn't the expected one.");
                }

                var ready = bridge;
                var ignored = Task.Run(ready.ReadLoopAsync);
                return bridge;
            }
            catch
            {
                if (bridge != null)
                {
                    bridge.Dispose();
                }
                else
                {
                    pipe.Dispose();
                    Kill(process);
                }

                throw;
            }
        }

        private async Task<JObject> ReadGreetingAsync(CancellationToken token)
        {
            var line = reader.ReadLineAsync();
            if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(15), token)) != line)
            {
                token.ThrowIfCancellationRequested();
                throw new OptiScalerException(OptiScalerException.NoAnswer, "The bridge doesn't say anything.");
            }

            try
            {
                var text = await line;
                return text == null ? null : JObject.Parse(text);
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                throw new OptiScalerException(OptiScalerException.NoAnswer, e.Message);
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    Read(line);
                }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                logger.Debug($"The bridge of OptiScaler closed: {e.Message}");
            }
            finally
            {
                closed = true;
                foreach (var pending in waiting)
                {
                    pending.Value.TrySetException(new OptiScalerException(OptiScalerException.Closed, "OptiScaler Client closed."));
                }
            }
        }

        private void Read(string line)
        {
            JObject message;
            try
            {
                message = JObject.Parse(line);
            }
            catch (JsonException e)
            {
                logger.Warn(e, "A message of the bridge of OptiScaler isn't JSON.");
                return;
            }

            var id = (int?)message["id"] ?? 0;
            if (message["progress"] is JObject step)
            {
                if (id == lastId)
                {
                    var name = (string)step["step"];
                    touched = touched || !OptiScalerProgress.IsBefore(name);
                    progress?.Invoke(new OptiScalerProgress { Step = name, Item = (string)step["item"], Percent = (double?)step["percent"], Touched = touched });
                }

                return;
            }

            if (!waiting.TryRemove(id, out var answer))
            {
                return;
            }

            if (message["error"] is JObject error)
            {
                logger.Info($"OptiScaler Client answered {(string)error["code"]} ({(string)error["type"]}): {(string)error["message"]}");
                answer.TrySetException(new OptiScalerException((string)error["code"] ?? "failed", (string)error["message"]));
            }
            else
            {
                answer.TrySetResult(message["result"] as JObject ?? new JObject());
            }
        }

        /// <summary>One question at a time; the next one waits for its turn.</summary>
        public async Task<JObject> AskAsync(string method, JObject parameters, TimeSpan limit, Action<OptiScalerProgress> report, CancellationToken token)
        {
            if (!await asking.WaitAsync(limit, token))
            {
                // The one before doesn't end. If it isn't changing a game, the program is started again the next time.
                var busy = IsChanging;
                if (!busy)
                {
                    Dispose();
                }

                throw new OptiScalerException(busy ? OptiScalerException.Busy : OptiScalerException.Slow, $"There was no turn for {method}.");
            }

            var abandoned = false;
            try
            {
                if (closed)
                {
                    throw new OptiScalerException(OptiScalerException.NotSent, "OptiScaler Client closed while this question waited for its turn.");
                }

                var id = Interlocked.Increment(ref lastId);
                var answer = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting[id] = answer;
                progress = report;
                touched = false;
                working = method;
                var question = new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() };
                try
                {
                    await writer.WriteLineAsync(question.ToString(Formatting.None));
                    await writer.FlushAsync();
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    waiting.TryRemove(id, out _);
                    throw new OptiScalerException(OptiScalerException.NotSent, e.Message);
                }

                if (closed)
                {
                    // It closed between the first look and the question: nobody is reading anymore.
                    answer.TrySetException(new OptiScalerException(OptiScalerException.Closed, "OptiScaler Client closed."));
                }

                if (await Task.WhenAny(answer.Task, Task.Delay(limit, token)) != answer.Task)
                {
                    // Nobody waits for it, but the program is still at it: the next question goes after its answer.
                    abandoned = true;
                    progress = null;
                    var ignored = answer.Task.ContinueWith(a => Finish(a.Exception), TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                    if (!IsChanging)
                    {
                        // Something in it doesn't end: the next question finds another one.
                        Dispose();
                    }

                    throw new OptiScalerException(OptiScalerException.Slow, $"No answer to {method} in {limit.TotalSeconds:0} s.");
                }

                return await answer.Task;
            }
            finally
            {
                if (!abandoned)
                {
                    Finish(null);
                }
            }
        }

        private void Finish(Exception problem)
        {
            if (problem != null)
            {
                logger.Debug($"A question nobody waited for ended in: {problem.GetBaseException().Message}");
            }

            progress = null;
            working = null;
            asking.Release();
        }

        private static void Kill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
            }
        }

        /// <summary>
        /// Closes the pipe, which ends the program by itself: at once, or before touching the game if it was getting ready
        /// for it, or when it finishes what it's doing if it already was. One that is changing a game is never made to end.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            var leave = IsChanging;
            closed = true;
            try
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
            }

            Task.Run(() =>
            {
                try
                {
                    if (!leave && !process.WaitForExit(4000))
                    {
                        Kill(process);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            });
        }

        /// <summary>Stops it before it touches the game. False when it's too late.</summary>
        public bool Stop()
        {
            if (touched || (IsChanging && working != "install"))
            {
                return false;
            }

            Dispose();
            return true;
        }
    }
}
