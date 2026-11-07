using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>
    /// Butler is the engine of the itch app, public and documented: https://docs.itch.ovh/butlerd/master
    /// It comes with the app and uses its data, so it installs what the app would, and the app sees it.
    /// </summary>
    internal class ButlerClient : IDisposable
    {
        private const string ListenMessage = "butlerd/listen-notification";

        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(10);
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<long, TaskCompletionSource<JToken>>();
        private readonly object writeLock = new object();
        private Process process;
        private TcpClient connection;
        private StreamWriter writer;
        private long lastRequest;
        private int disposed;

        /// <summary>From 0 to 1.</summary>
        public event Action<double> ProgressChanged;

        /// <summary>Answers to the questions of a game with a license of its own.</summary>
        public Func<bool> AcceptsLicense { get; set; }

        private static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch");

        public static string Executable
        {
            get
            {
                var version = Path.Combine(UserFolder, "broth", "butler", ".chosen-version");
                if (!File.Exists(version))
                {
                    return null;
                }

                var file = Path.Combine(UserFolder, "broth", "butler", "versions", File.ReadAllText(version).Trim(), "butler.exe");
                return File.Exists(file) ? file : null;
            }
        }

        public static string Database
        {
            get
            {
                var file = Path.Combine(UserFolder, "db", "butler.db");
                return File.Exists(file) ? file : null;
            }
        }

        /// <summary>Null if the itch app isn't on this computer.</summary>
        public static async Task<ButlerClient> StartAsync()
        {
            if (Executable == null || Database == null)
            {
                return null;
            }

            var client = new ButlerClient();
            try
            {
                await client.ConnectAsync().ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private async Task ConnectAsync()
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Executable,
                    // It ends with Atrio even if Atrio doesn't get to say goodbye.
                    Arguments = $"daemon --json --dbpath \"{Database}\" --transport tcp --keep-alive --destiny-pid={Process.GetCurrentProcess().Id}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var listening = new TaskCompletionSource<JObject>();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.StartsWith("{", StringComparison.Ordinal) == true && e.Data.Contains(ListenMessage))
                {
                    try
                    {
                        listening.TrySetResult(JObject.Parse(e.Data));
                    }
                    catch (JsonException failure)
                    {
                        listening.TrySetException(failure);
                    }
                }
            };

            process.ErrorDataReceived += (_, __) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (await Task.WhenAny(listening.Task, Task.Delay(startPatience)).ConfigureAwait(false) != listening.Task)
            {
                throw new TimeoutException("Butler didn't start.");
            }

            var details = listening.Task.Result;
            var address = details["tcp"]["address"].ToString().Split(':');
            connection = new TcpClient();
            await connection.ConnectAsync(address[0], int.Parse(address[1])).ConfigureAwait(false);
            writer = new StreamWriter(connection.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            new Thread(ReadMessages) { IsBackground = true }.Start();
            await CallAsync("Meta.Authenticate", new { secret = details["secret"].ToString() }).ConfigureAwait(false);
        }

        private void Write(object message)
        {
            lock (writeLock)
            {
                writer.WriteLine(JsonConvert.SerializeObject(message));
            }
        }

        private void ReadMessages()
        {
            try
            {
                using (var reader = new StreamReader(connection.GetStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        HandleMessage(JObject.Parse(line));
                    }
                }
            }
            catch (Exception e)
            {
                // This thread is nobody's: anything that escapes from here would close the program.
                logger.Debug($"The connection with butler ended: {e.Message}");
            }

            foreach (var request in pending.Values)
            {
                request.TrySetException(new IOException("Butler went away."));
            }
        }

        private void HandleMessage(JObject message)
        {
            var method = message["method"]?.ToString();
            var id = message["id"];
            if (method == null)
            {
                if (id != null && pending.TryRemove(id.Value<long>(), out var request))
                {
                    if (message["error"] != null)
                    {
                        request.TrySetException(new InvalidOperationException(message["error"].ToString(Formatting.None)));
                    }
                    else
                    {
                        request.TrySetResult(message["result"]);
                    }
                }
            }
            else if (id != null)
            {
                Answer(id, method);
            }
            else if (method == "Progress")
            {
                ProgressChanged?.Invoke(message["params"]["progress"].Value<double>());
            }
        }

        private void Answer(JToken id, string method)
        {
            switch (method)
            {
                case "AcceptLicense":
                    Write(new { jsonrpc = "2.0", id, result = new { accept = AcceptsLicense?.Invoke() == true } });
                    break;
                case "ExternalUploadsAreBad":
                    Write(new { jsonrpc = "2.0", id, result = new { whatever = true } });
                    break;
                case "PickUpload":
                    Write(new { jsonrpc = "2.0", id, result = new { index = 0 } });
                    break;
                default:
                    logger.Info($"Butler asked {method}, which Atrio doesn't answer.");
                    Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Not supported." } });
                    break;
            }
        }

        public Task<JToken> CallAsync(string method, object parameters = null)
        {
            var id = Interlocked.Increment(ref lastRequest);
            var request = new TaskCompletionSource<JToken>();
            pending[id] = request;
            Write(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
            return request.Task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (writer != null)
                {
                    Write(new { jsonrpc = "2.0", id = Interlocked.Increment(ref lastRequest), method = "Meta.Shutdown", @params = new { } });
                }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException)
            {
                logger.Debug("Butler was already gone.");
            }

            connection?.Close();
            try
            {
                if (process?.HasExited == false && !process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                logger.Debug($"Butler ended on its own: {e.Message}");
            }

            process?.Dispose();
        }
    }

    /// <summary>Installs a game of itch.io with the engine of its app, with no windows involved.</summary>
    public class ItchDirectInstallController : DirectInstallController
    {
        private static readonly string[] playableUploads = { "default", "html", "flash", "unity", "java" };

        public ItchDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        /// <summary>Butler gives them from best to worst for this computer. Soundtracks and books aren't the game.</summary>
        public static JToken PickUpload(JArray uploads)
        {
            return uploads.FirstOrDefault(a => a["type"]?.ToString() == "default" && a["platforms"]?["windows"] != null) ??
                   uploads.FirstOrDefault(a => playableUploads.Contains(a["type"]?.ToString()));
        }

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            using (var butler = await ButlerClient.StartAsync().ConfigureAwait(false))
            {
                if (butler == null)
                {
                    return false;
                }

                // Off this thread: cancelling comes from the interface, and butler can take a moment to leave.
                Cancellation.Register(() => Task.Run(() => butler.Dispose()));
                butler.AcceptsLicense = () => DirectInstall.AcceptsStoreTerms;
                butler.ProgressChanged += progress => DirectInstallProgress.Set(Game.Id, (int)(progress * 100));
                var profiles = (JArray)(await butler.CallAsync("Profile.List").ConfigureAwait(false))["profiles"];
                if (profiles.Count == 0)
                {
                    Logger.Info("No profile in the itch app to install directly.");
                    return false;
                }

                await butler.CallAsync("Profile.UseSavedLogin", new { profileId = profiles[0]["id"].Value<long>() }).ConfigureAwait(false);
                var game = (await butler.CallAsync("Fetch.Game", new { gameId = long.Parse(Game.GameId) }).ConfigureAwait(false))["game"];
                var uploads = (JArray)(await butler.CallAsync("Game.FindUploads", new { game }).ConfigureAwait(false))["uploads"];
                var upload = PickUpload(uploads);
                if (upload == null)
                {
                    Logger.Info($"itch.io has nothing to download for {Game.GameId} on this computer.");
                    return false;
                }

                var locations = (JArray)(await butler.CallAsync("Install.Locations.List").ConfigureAwait(false))["installLocations"];
                var queued = await butler.CallAsync("Install.Queue", new
                {
                    game,
                    upload,
                    installLocationId = locations[0]["id"].ToString(),
                    reason = "install",
                    noCave = false,
                    queueDownload = false
                }).ConfigureAwait(false);

                Logger.Info($"Installing {Game.GameId} of itch.io.");
                await butler.CallAsync("Install.Perform", new { id = queued["id"].ToString(), stagingFolder = queued["stagingFolder"].ToString() }).ConfigureAwait(false);
                Finish(queued["installFolder"].ToString());
                return true;
            }
        }
    }
}
