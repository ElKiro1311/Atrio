using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Atrio;
using Playnite.Commands;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>One value that can be chosen for an option of OptiScaler.</summary>
    public class OptiScalerChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>One option of OptiScaler, with what can be chosen and what is chosen.</summary>
    public class OptiScalerOption : ObservableObject
    {
        private readonly Action<OptiScalerOption> changed;

        public string Key { get; }
        public string Label { get; }
        public string Hint { get; }

        /// <summary>Changing it means installing again: the others only change the file of settings.</summary>
        public bool NeedsInstall { get; }

        /// <summary>It isn't an option of OptiScaler: it says what another one offers to choose from.</summary>
        public bool IsFilter { get; }

        /// <summary>What was chosen when it was read.</summary>
        public string Initial { get; private set; }

        /// <summary>The person chose something: reading it again in the background doesn't take it away.</summary>
        public bool IsTouched { get; private set; }

        private List<OptiScalerChoice> choices = new List<OptiScalerChoice>();
        public List<OptiScalerChoice> Choices
        {
            get => choices;
            private set
            {
                choices = value;
                OnPropertyChanged();
            }
        }

        private OptiScalerChoice selected;
        public OptiScalerChoice Selected
        {
            get => selected;
            set
            {
                // A list that changes takes its selection away for a moment: that isn't a choice.
                if (value == null || value == selected)
                {
                    return;
                }

                selected = value;
                IsTouched = true;
                OnPropertyChanged();
                changed(this);
            }
        }

        private bool isEnabled = true;
        public bool IsEnabled
        {
            get => isEnabled;
            set
            {
                isEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool IsChanged => Selected?.Id != Initial;

        private bool isShown = true;

        /// <summary>One without anything to choose from doesn't show.</summary>
        public bool IsShown
        {
            get => isShown;
            set
            {
                isShown = value;
                OnPropertyChanged();
            }
        }

        public OptiScalerOption(string key, string label, string hint, bool needsInstall, bool isFilter, Action<OptiScalerOption> changed)
        {
            Key = key;
            Label = label;
            Hint = hint;
            NeedsInstall = needsInstall;
            IsFilter = isFilter;
            this.changed = changed;
        }

        /// <param name="partial">What is offered is only a part: what was chosen may not be in it, and it's still the start.</param>
        public void Fill(List<OptiScalerChoice> newChoices, string chosen, bool keepTouched, bool partial = false)
        {
            var wanted = keepTouched && IsTouched ? selected?.Id : chosen;
            if (!keepTouched)
            {
                IsTouched = false;
            }

            // Without anything to choose from there's nothing chosen either.
            Initial = newChoices.Count == 0 ? null : chosen;
            Choices = newChoices;
            selected = newChoices.FirstOrDefault(a => string.Equals(a.Id, wanted, StringComparison.OrdinalIgnoreCase)) ??
                newChoices.FirstOrDefault(a => string.Equals(a.Id, chosen, StringComparison.OrdinalIgnoreCase)) ??
                newChoices.FirstOrDefault();
            if (!partial && selected != null && newChoices.All(a => !string.Equals(a.Id, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                // What was chosen isn't offered anymore: what is shown is the start.
                Initial = selected.Id;
            }
            else if (selected != null && string.Equals(selected.Id, chosen, StringComparison.OrdinalIgnoreCase))
            {
                Initial = selected.Id;
            }

            OnPropertyChanged(nameof(Selected));
        }
    }

    public enum OptiScalerStage
    {
        /// <summary>OptiScaler Client isn't on this computer.</summary>
        NoClient,
        Downloading,
        Starting,
        Ready,

        /// <summary>Installing, changing or taking out.</summary>
        Working,

        /// <summary>Only its own window can be used in this version.</summary>
        OnlyWindow,
        Failed
    }

    /// <summary>The bridge stays a little after being used: the next game finds it already started.</summary>
    internal static class OptiScalerSession
    {
        private static readonly SemaphoreSlim starting = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan idle = TimeSpan.FromMinutes(2);
        private static OptiScalerBridge bridge;
        private static Timer timer;
        private static int users;

        public static async Task<OptiScalerBridge> GetAsync(string executable, CancellationToken token)
        {
            await starting.WaitAsync(token);
            try
            {
                if (bridge != null && (bridge.IsClosed || !string.Equals(bridge.Executable, executable, StringComparison.OrdinalIgnoreCase)))
                {
                    if (bridge.IsChanging)
                    {
                        throw new OptiScalerException(OptiScalerException.Busy, "It's changing a game.");
                    }

                    bridge.Dispose();
                    bridge = null;
                }

                if (bridge == null)
                {
                    bridge = await OptiScalerBridge.StartAsync(executable, token);
                }

                Touch();
                return bridge;
            }
            finally
            {
                starting.Release();
            }
        }

        public static void Begin()
        {
            Interlocked.Increment(ref users);
        }

        public static void End()
        {
            Interlocked.Decrement(ref users);
            Touch();
        }

        private static void Touch()
        {
            if (timer == null)
            {
                timer = new Timer(_ => CloseIdle(), null, idle, Timeout.InfiniteTimeSpan);
            }
            else
            {
                timer.Change(idle, Timeout.InfiniteTimeSpan);
            }
        }

        private static void CloseIdle()
        {
            // Something may be going on in a game whose page isn't showing anymore.
            if (Volatile.Read(ref users) > 0 || bridge?.IsBusy == true)
            {
                Touch();
            }
            else
            {
                Close();
            }
        }

        /// <summary>A game is being changed, maybe one whose page isn't showing anymore.</summary>
        public static bool IsChanging => bridge?.IsChanging == true;

        public static void Close()
        {
            Interlocked.Exchange(ref bridge, null)?.Dispose();
        }
    }

    /// <summary>
    /// The panel of OptiScaler in the page of a game: what the game has, what can be changed, and installing it or taking it
    /// out, with the functions of OptiScaler Client. Everything else is in its own window, one click away.
    /// </summary>
    public class OptiScalerPanel : ObservableObject, IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string Keep = "keep";
        private const string Own = "own";
        private const string Fresh = "fresh";
        private const string OptiFg = "OptiFg";
        private const string None = "none";
        private const string Stopped = "stopped";

        private static readonly Dictionary<Guid, string> platforms = new Dictionary<Guid, string>
        {
            { SteamContent.SteamPluginId, "Steam" },
            { Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4"), "Epic" },
            { Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E"), "GOG" },
            { Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287"), "Xbox" },
            { Guid.Parse("85DD7072-2F20-4E76-A007-41035E390724"), "EA" },
            { Guid.Parse("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD"), "BattleNet" },
            { Guid.Parse("C2F038E5-8B92-4877-91F1-DA9094155FC5"), "Ubisoft" }
        };

        /// <summary>Its games are only played online, with an anti-cheat that starts with Windows.</summary>
        private static readonly Guid riotId = Guid.Parse("91d13c6f-63d3-42ed-a100-6f811a8387ea");

        private readonly GamesCollectionViewEntry game;
        private readonly IDialogsFactory dialogs;
        private readonly IResourceProvider resources;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly Dictionary<string, OptiScalerOption> options = new Dictionary<string, OptiScalerOption>();
        private CancellationTokenSource downloading;
        private OptiScalerBridge bridge;
        private OptiScalerCopy copy;
        private JObject state;
        private bool asked;
        private bool opening;
        private bool filling;
        private Process window;

        public List<OptiScalerOption> Options { get; } = new List<OptiScalerOption>();
        public RelayCommand<object> DownloadClientCommand { get; }
        public RelayCommand<object> UsePackageCommand { get; }
        public RelayCommand<object> InstallCommand { get; }
        public RelayCommand<object> UpdateCommand { get; }
        public RelayCommand<object> UninstallCommand { get; }
        public RelayCommand<object> AllOptionsCommand { get; }
        public RelayCommand<object> OpenClientCommand { get; }
        public RelayCommand<object> OpenFolderCommand { get; }
        public RelayCommand<object> OpenIniCommand { get; }
        public RelayCommand<object> OpenCopiesCommand { get; }
        public RelayCommand<object> OpenGuideCommand { get; }
        public RelayCommand<object> OpenListCommand { get; }
        public RelayCommand<object> CheckUpdatesCommand { get; }
        public RelayCommand<object> StopCommand { get; }
        public RelayCommand<object> RetryCommand { get; }
        public RelayCommand<object> ClientPageCommand { get; }

        /// <summary>The copy of the game that is installed on this computer, null when there's none OptiScaler could go into.</summary>
        public Game Target { get; private set; }
        public bool IsAvailable => Target != null;

        private bool isOpen;
        public bool IsOpen
        {
            get => isOpen;
            set
            {
                if (isOpen == value)
                {
                    return;
                }

                isOpen = value;
                OnPropertyChanged();
                if (value)
                {
                    Open();
                }
            }
        }

        private OptiScalerStage stage = OptiScalerStage.Starting;
        public OptiScalerStage Stage
        {
            get => stage;
            private set
            {
                stage = value;
                Notify();
            }
        }

        public bool ShowsDownload => Stage == OptiScalerStage.NoClient;
        public bool ShowsState => state != null && (Stage == OptiScalerStage.Ready || Stage == OptiScalerStage.Working);
        public bool ShowsProgress => Stage == OptiScalerStage.Downloading || Stage == OptiScalerStage.Working || Stage == OptiScalerStage.Starting;
        public bool ShowsOnlyWindow => Stage == OptiScalerStage.OnlyWindow;
        public bool ShowsFailed => Stage == OptiScalerStage.Failed;
        public bool IsWorking => Stage == OptiScalerStage.Working || Stage == OptiScalerStage.Downloading;
        public bool CanChoose => Stage == OptiScalerStage.Ready;

        private string progressText;
        public string ProgressText
        {
            get => progressText;
            private set
            {
                progressText = value;
                OnPropertyChanged();
            }
        }

        private double progress;
        public double Progress
        {
            get => progress;
            private set
            {
                progress = value;
                OnPropertyChanged();
            }
        }

        private bool isIndeterminate = true;
        public bool IsIndeterminate
        {
            get => isIndeterminate;
            private set
            {
                isIndeterminate = value;
                OnPropertyChanged();
            }
        }

        private bool canStop;
        public bool CanStop
        {
            get => canStop;
            private set
            {
                canStop = value;
                OnPropertyChanged();
            }
        }

        private string message;

        /// <summary>How the last thing went, or why it couldn't be done.</summary>
        public string Message
        {
            get => message;
            private set
            {
                message = value;
                OnPropertyChanged();
            }
        }

        private bool messageIsProblem;
        public bool MessageIsProblem
        {
            get => messageIsProblem;
            private set
            {
                messageIsProblem = value;
                OnPropertyChanged();
            }
        }

        private string installedVersion;

        /// <summary>The version of OptiScaler in the game, for the button. Null without it.</summary>
        public string InstalledVersion
        {
            get => installedVersion;
            private set
            {
                installedVersion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(ButtonHint));
            }
        }

        public bool IsInstalled => !InstalledVersion.IsNullOrEmpty();
        public string ButtonHint => IsInstalled ? string.Format(resources.GetString("LOCAtrioOptiButtonInstalled"), InstalledVersion) : resources.GetString("LOCAtrioOptiButtonHint");
        public string StatusText { get; private set; }
        public string StatusDetail { get; private set; }
        public List<string> DetectedChips { get; private set; } = new List<string>();
        public string GpuText { get; private set; }
        public bool HasAntiCheat { get; private set; }
        public bool NothingDetected { get; private set; }
        public string CompatibilityText { get; private set; }
        public bool CompatibilityIsGood { get; private set; }
        public string CompatibilityNotes { get; private set; }
        public bool HasCompatibility => !CompatibilityText.IsNullOrEmpty();
        public string GuideUrl { get; private set; }
        public string InstallText { get; private set; }
        public bool ShowsInstall { get; private set; }

        /// <summary>Put by hand and without changes: installing it again is possible, but it isn't the main thing to do.</summary>
        public bool ShowsReinstall { get; private set; }
        public string UpdateText { get; private set; }
        public bool ShowsUpdate => !UpdateText.IsNullOrEmpty();
        public bool ShowsUninstall { get; private set; }
        public bool HasIni => !IniPath.IsNullOrEmpty();
        private string IniPath => (string)state?["ini"];

        /// <summary>The person changed something in the settings of OptiScaler, or it's waiting for a version that has it.</summary>
        private bool HasOwn => (int?)state?["own"]?["count"] > 0;

        /// <summary>Copies of the settings that were replaced or taken out.</summary>
        public bool HasCopies => !CopiesPath.IsNullOrEmpty();
        private string CopiesPath => (string)state?["own"]?["copies"];
        public string ClientText { get; private set; }

        /// <summary>A newer copy is there and isn't used: why.</summary>
        public string KeptText { get; private set; }
        public string ClientUpdateText { get; private set; }
        public bool HasClientUpdate => !ClientUpdateText.IsNullOrEmpty();
        public string DownloadText { get; private set; }

        /// <summary>The package the person already downloaded with their browser, the first time.</summary>
        public string PackageText { get; private set; }
        public bool HasPackage => !PackageText.IsNullOrEmpty();

        public OptiScalerPanel(GamesCollectionViewEntry game, IDialogsFactory dialogs, IResourceProvider resources)
        {
            this.game = game;
            this.dialogs = dialogs;
            this.resources = resources;
            DownloadClientCommand = new RelayCommand<object>(_ => DownloadClient(), _ => !IsWorking);
            UsePackageCommand = new RelayCommand<object>(_ => UsePackage(), _ => !IsWorking);
            InstallCommand = new RelayCommand<object>(_ => Install(null), _ => CanChoose);
            UpdateCommand = new RelayCommand<object>(_ => Update(), _ => CanChoose);
            UninstallCommand = new RelayCommand<object>(_ => Uninstall(), _ => CanChoose);
            AllOptionsCommand = new RelayCommand<object>(_ => OpenWindow(true), _ => copy != null && !IsWorking);
            OpenClientCommand = new RelayCommand<object>(_ => OpenWindow(false), _ => copy != null && !IsWorking);
            OpenFolderCommand = new RelayCommand<object>(_ => OpenPath((string)state?["directory"] ?? (Target == null ? null : GetFolder(Target))));
            OpenIniCommand = new RelayCommand<object>(_ => OpenPath(IniPath), _ => HasIni);
            OpenCopiesCommand = new RelayCommand<object>(_ => OpenPath(CopiesPath), _ => HasCopies);
            OpenGuideCommand = new RelayCommand<object>(_ => GlobalCommands.NavigateUrlInside(GuideUrl), _ => !GuideUrl.IsNullOrEmpty());
            OpenListCommand = new RelayCommand<object>(_ => GlobalCommands.NavigateUrlInside(ListUrl));
            CheckUpdatesCommand = new RelayCommand<object>(_ => CheckUpdates(true), _ => !IsWorking);
            StopCommand = new RelayCommand<object>(_ => Stop(), _ => CanStop);
            RetryCommand = new RelayCommand<object>(_ => Retry());
            ClientPageCommand = new RelayCommand<object>(_ => GlobalCommands.NavigateUrl(OptiScalerClient.ProjectUrl));
            AddOption("channel", false, true);
            AddOption("version", true);
            AddOption("injection", true);
            AddOption("output", false);
            AddOption("quality", false);
            AddOption("frames", true);
            AddOption("mode", true);
            AddOption("fsr4", true);
            AddOption("patcher", true);
            AddOption("spoofing", false);
            AddOption("profile", false);
            AddOption("settings", true);
            FindTarget();
        }

        private void AddOption(string key, bool needsInstall, bool isFilter = false)
        {
            var name = char.ToUpperInvariant(key[0]) + key.Substring(1);
            var option = new OptiScalerOption(key, resources.GetString($"LOCAtrioOpti{name}"), resources.GetString($"LOCAtrioOpti{name}Hint"), needsInstall, isFilter, OptionChanged);
            options[key] = option;
            Options.Add(option);
        }

        /// <summary>Only a game installed in a folder of this computer: not a game of a console, and not one of Windows without a folder.</summary>
        public void FindTarget()
        {
            Game found = null;
            if (OptiScalerClient.IsSupported)
            {
                var copies = new[] { game.Game }.Concat(game.Copies.Select(a => a.Game)).Where(a => a != null).Distinct();
                found = copies.FirstOrDefault(a => a.IsInstalled && !a.InstallDirectory.IsNullOrEmpty() && !a.Roms.HasItems() && a.PluginId != riotId && GameUnifier.IsPc(a));
            }

            if (found?.Id != Target?.Id)
            {
                // Another copy of the game, or none: what was read was of another folder.
                Target = found;
                state = null;
                asked = false;
                listRead = false;
                InstalledVersion = null;
                IsOpen = false;
                OnPropertyChanged(nameof(IsAvailable));
            }
        }

        /// <summary>The folder of the game, whole: a game added by hand can have it with variables.</summary>
        private static string GetFolder(Game copy)
        {
            try
            {
                return copy.ExpandVariables(copy.InstallDirectory, true).TrimEnd('\\', '/');
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return copy.InstallDirectory.TrimEnd('\\', '/');
            }
        }

        /// <summary>What the client wrote about this game the last time, without starting it.</summary>
        public async Task LoadRecordAsync(int pause)
        {
            if (!IsAvailable)
            {
                return;
            }

            try
            {
                await Task.Delay(pause, closing.Token);
                var path = GetFolder(Target);
                var version = await Task.Run(() => OptiScalerClient.GetRecordedVersion(path), closing.Token);
                if (state == null)
                {
                    InstalledVersion = version;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to read what OptiScaler Client knows of a game.");
            }
        }

        private JObject GetGame()
        {
            var parameters = new JObject
            {
                ["name"] = Target.Name,
                ["path"] = GetFolder(Target),
                ["platform"] = platforms.TryGetValue(Target.PluginId, out var platform) ? platform : "Manual",
                ["appId"] = Target.GameId
            };
            return parameters;
        }

        private bool IsGameOpen => game.Copies.Any(a => a.Game.IsRunning || a.Game.IsLaunching || OpenGames.IsOpen(a.Game.Id));

        private async void Open()
        {
            if (!IsAvailable || IsWorking || opening)
            {
                return;
            }

            opening = true;
            try
            {
                SetMessage(null, false);
                copy = OptiScalerClient.GetCurrent();
                if (copy == null && OptiScalerClient.GetOwnPackages().Count == 0)
                {
                    await ShowNoClientAsync();
                    return;
                }

                await StartAsync();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to open the panel of OptiScaler.");
            }
            finally
            {
                opening = false;
            }
        }

        private void Retry()
        {
            OptiScalerClient.ForgetProblems();
            Open();
        }

        private async Task ShowNoClientAsync()
        {
            var package = (await Task.Run(() => OptiScalerClient.GetDownloadedPackages())).FirstOrDefault();
            PackageText = package == null ? null : string.Format(resources.GetString("LOCAtrioOptiUsePackage"), Path.GetFileName(package));
            Stage = OptiScalerStage.NoClient;
            CheckUpdates(false);
        }

        /// <summary>The first time, the package that was downloaded by hand is only used when the person says so.</summary>
        private async void UsePackage()
        {
            if (IsWorking || !IsAvailable || opening)
            {
                return;
            }

            opening = true;
            SetMessage(null, false);
            await StartAsync();
            opening = false;
            if (copy == null && !closing.IsCancellationRequested && Message.IsNullOrEmpty())
            {
                SetMessage(resources.GetString("LOCAtrioOptiErrorpackage"), true);
            }
        }

        private async Task StartAsync()
        {
            ProgressText = resources.GetString("LOCAtrioOptiStarting");
            IsIndeterminate = true;
            CanStop = false;
            Stage = OptiScalerStage.Starting;
            try
            {
                copy = await OptiScalerClient.PrepareAsync(closing.Token);
                if (copy == null)
                {
                    await ShowNoClientAsync();
                    return;
                }

                UpdateClientText();
                var contract = OptiScalerClient.GetContract(copy);
                if (contract?.Usable != true)
                {
                    if (contract?.Problem == OptiScalerException.NoStart)
                    {
                        // Windows or the antivirus: its window wouldn't open either.
                        throw new OptiScalerException(OptiScalerException.NoStart, OptiScalerClient.ProgramFile);
                    }

                    Stage = OptiScalerStage.OnlyWindow;
                    CheckUpdates(false);
                    return;
                }

                await ReadStateAsync(state == null, false, false);
                Stage = OptiScalerStage.Ready;
                CheckUpdates(false);
                await ReadMoreAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Fail(e, OptiScalerStage.Failed);
            }
        }

        /// <summary>The versions on GitHub and what the wiki says of the game come after the panel is already there.</summary>
        private async Task ReadMoreAsync()
        {
            if (asked)
            {
                return;
            }

            asked = true;
            try
            {
                await AskAsync("refresh", null, TimeSpan.FromSeconds(60), null);
                if (Stage == OptiScalerStage.Ready)
                {
                    await ReadStateAsync(false, true, true);
                }
            }
            catch (OptiScalerException e)
            {
                // Without internet, what it already knows is enough.
                logger.Debug($"OptiScaler Client couldn't look for versions: {e.Code} {e.Message}");
                asked = false;
            }

            if (Stage == OptiScalerStage.Ready && versions.Channels.Count == 0 && Message.IsNullOrEmpty())
            {
                SetMessage(resources.GetString("LOCAtrioOptiErrornoversions"), true);
            }
        }

        /// <summary>What changes a game goes on until it ends, even if its page isn't showing anymore.</summary>
        private async Task<JObject> AskAsync(string method, JObject parameters, TimeSpan limit, Action<OptiScalerProgress> report)
        {
            var token = report == null ? closing.Token : CancellationToken.None;
            OptiScalerSession.Begin();
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    // While it starts, stopping it is only written down.
                    bridge = null;
                    var ready = await OptiScalerSession.GetAsync(copy.Executable, token);
                    if (report != null && stopping)
                    {
                        throw new OptiScalerException(Stopped, "It was stopped before starting.");
                    }

                    bridge = ready;
                    try
                    {
                        return await ready.AskAsync(method, parameters, limit, report, token);
                    }
                    catch (OptiScalerException e) when (e.Code == OptiScalerException.NotSent && attempt == 0 && !(report != null && stopping))
                    {
                        // The question before this one took the program with it.
                        logger.Debug($"OptiScaler Client starts again for {method}.");
                    }
                }
            }
            finally
            {
                OptiScalerSession.End();
            }
        }

        private async Task ReadStateAsync(bool force, bool ask, bool keepTouched, bool alone = false)
        {
            var parameters = GetGame();
            parameters["force"] = force;
            parameters["ask"] = ask;
            parameters["alone"] = alone;
            var answer = await AskAsync("state", parameters, TimeSpan.FromSeconds(120), null);
            if (!closing.IsCancellationRequested)
            {
                listRead |= ask && OptiScalerClient.GetContract(copy)?.Functions.Contains("compatibility") == true;
                Show(answer, keepTouched);
            }
        }

        private static List<string> Strings(JToken list)
        {
            return (list as JArray)?.Select(a => (string)a).Where(a => !a.IsNullOrEmpty()).ToList() ?? new List<string>();
        }

        private string Word(string prefix, string id)
        {
            // A value of a newer version has no words here yet: it shows with its own name.
            var key = $"LOCAtrioOpti{prefix}{id}";
            var text = resources.GetString(key);
            return text.IsNullOrEmpty() || text.Contains(key) ? id : text;
        }

        private List<OptiScalerChoice> Words(string prefix, IEnumerable<string> ids)
        {
            return ids.Select(a => new OptiScalerChoice { Id = a, Name = Word(prefix, a) }).ToList();
        }

        private void Show(JObject answer, bool keepTouched)
        {
            filling = true;
            try
            {
                state = answer;
                var choices = answer["choices"] as JObject ?? new JObject();
                var chosen = answer["chosen"] as JObject ?? new JObject();
                var installed = (bool?)answer["installed"] == true;
                InstalledVersion = installed ? (string)answer["version"] ?? "?" : null;

                FillVersions(choices, chosen, keepTouched);
                options["injection"].Fill(Strings(choices["injections"]).Select(a => new OptiScalerChoice { Id = a, Name = a }).ToList(), (string)chosen["injection"], keepTouched);
                FillWithOwn("output", Words("Output", Strings(choices["outputs"])), chosen, keepTouched);
                FillWithOwn("quality", Words("Quality", Strings(choices["qualities"]).Where(a => a != "Custom" || (string)chosen["quality"] == "Custom")), chosen, keepTouched);
                FillWithOwn("spoofing", Words("Spoofing", Strings(choices["spoofing"])), chosen, keepTouched);
                options["profile"].Fill(
                    (choices["profiles"] as JArray)?.OfType<JObject>().Select(a => new OptiScalerChoice { Id = (string)a["id"], Name = (string)a["id"] }).ToList() ?? new List<OptiScalerChoice>(),
                    (string)chosen["profile"], keepTouched);
                FillFrames(choices, chosen, keepTouched);
                FillFsr4(choices, chosen, keepTouched);
                FillPatcher(choices, chosen, keepTouched);

                // Only when the person changed something in the settings of the game there's something to say about it.
                options["settings"].Fill(HasOwn ? Words("Settings", new[] { Keep, Fresh }) : new List<OptiScalerChoice>(), Keep, keepTouched);
                if (!keepTouched)
                {
                    startedOver.Clear();
                }
                foreach (var option in Options)
                {
                    option.IsEnabled = option.Choices.Count > 1;
                    option.IsShown = option.Choices.Count > (option.Key == "profile" || option.IsFilter ? 1 : 0);
                }

                ShowDetected(answer);
                ShowCompatibility(answer["compatibility"] as JObject);
                StatusText = (bool?)answer["half"] == true ? resources.GetString("LOCAtrioOptiStatusCut") : installed ?
                    string.Format(resources.GetString("LOCAtrioOptiStatusInstalled"), InstalledVersion) :
                    resources.GetString((bool?)answer["swapped"] == true ? "LOCAtrioOptiStatusSwapped" : "LOCAtrioOptiStatusNone");
                StatusDetail = (bool?)answer["half"] == true ? resources.GetString("LOCAtrioOptiStatusHalf") :
                    !installed ? null :
                    (bool?)answer["managed"] == true ? string.Format(resources.GetString("LOCAtrioOptiStatusAs"), (string)chosen["injection"]) :
                    resources.GetString("LOCAtrioOptiStatusManual");
                ShowsUninstall = installed || (bool?)answer["swapped"] == true || (bool?)answer["half"] == true;
            }
            finally
            {
                filling = false;
            }

            if (keepTouched && options["settings"].Selected?.Id == Fresh)
            {
                UpdateOwn(true);
            }

            UpdateModes();
            UpdateActions();
            Notify();
        }

        /// <summary>An option that the person has set inside the game: it's one more thing to choose, and what is chosen.</summary>
        private void FillWithOwn(string key, List<OptiScalerChoice> list, JObject chosen, bool keepTouched)
        {
            if ((string)chosen[key] == Own)
            {
                list.Insert(0, OwnChoice(key));
            }

            options[key].Fill(list, (string)chosen[key], keepTouched);
        }

        private OptiScalerChoice OwnChoice(string key)
        {
            var summary = GetOwnName(key, (string)state["own"]?["options"]?[key]);
            return new OptiScalerChoice
            {
                Id = Own,
                Name = summary.IsNullOrEmpty() ? resources.GetString("LOCAtrioOptiOwn") : string.Format(resources.GetString("LOCAtrioOptiOwnValue"), summary)
            };
        }

        // The words of OptiScaler for what its menu calls otherwise.
        private static readonly Dictionary<string, string> ownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fsr21", "FSR 2.1" }, { "fsr21_12", "FSR 2.1" }, { "fsr22", "FSR 2.2" }, { "fsr22_12", "FSR 2.2" }, { "fsr31", "FSR 3" },
            { "ffx", "FSR 3/4" }, { "ffx_12", "FSR 3/4" }, { "xess", "XeSS" }, { "xess_12", "XeSS" }, { "dlss", "DLSS" }, { "dlss_12", "DLSS" },
            { "upscaler", "OptiFG" }, { "fsrfg", "FSR" }, { "fsrfg30", "FSR 3.0" }, { "xefg", "XeSS" }, { "dlssg", "DLSS" }, { "nukems", "Nukem" }, { "nvngxfg", "Nukem" }
        };

        /// <summary>What there is, in a few words. Null when there's nothing short to say.</summary>
        private string GetOwnName(string key, string summary)
        {
            if (summary.IsNullOrEmpty())
            {
                return null;
            }

            if (key == "spoofing")
            {
                return summary == "auto" ? null : Word("Spoofing", summary);
            }

            if (key == "quality")
            {
                return double.TryParse(summary, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) ? ratio.ToString("0.00", CultureInfo.InvariantCulture) + "×" : null;
            }

            var names = summary.Split('>', ',').Select(a => ownNames.TryGetValue(a, out var name) ? name : a).ToList();
            return names.Contains("nofg", StringComparer.OrdinalIgnoreCase) ? Word("Frames", "Disabled") : string.Join(key == "frames" ? " → " : ", ", names.Distinct());
        }

        private OptiScalerVersions versions = new OptiScalerVersions(null);

        /// <summary>
        /// The version to install: the chosen one. One put by hand that isn't published can't be, and the recommended one takes
        /// its place when it's of the same type, or the newest of that type.
        /// </summary>
        private string GetVersion()
        {
            var id = options["version"].Selected?.Id;
            if (!IsByHand || versions.Contains(id))
            {
                return id;
            }

            var channel = options["channel"].Selected?.Id;
            var recommended = (string)state?["defaults"]?["version"];
            return !recommended.IsNullOrEmpty() && versions.GetChannel(recommended) == channel ? recommended :
                versions.GetLatest(channel) ?? recommended ?? id;
        }

        /// <summary>The one in the game isn't of Atrio or of the client.</summary>
        private bool IsByHand => (bool?)state?["installed"] == true && (bool?)state?["managed"] != true;

        private void FillVersions(JObject choices, JObject chosen, bool keepTouched)
        {
            versions = new OptiScalerVersions(choices["versions"] as JArray);

            // A version that the person chose keeps its type too.
            var picked = keepTouched && options["version"].IsTouched ? options["version"].Selected?.Id : null;
            var channel = versions.GetChannel(picked ?? (string)chosen["version"]);
            var channels = versions.Channels.ToList();
            if (!channels.Contains(channel))
            {
                // The one in the game is of a type without anything published.
                channels.Add(channel);
            }

            options["channel"].Fill(Words("Channel", channels), channel, keepTouched);
            FillVersionsOfChannel(keepTouched);
        }

        /// <summary>The versions of the chosen type. The one in the game, or the recommended one, is only with the ones of its own.</summary>
        private void FillVersionsOfChannel(bool keepTouched)
        {
            var option = options["version"];
            var channel = options["channel"].Selected?.Id;
            var current = (string)state?["chosen"]?["version"];
            var list = versions.Get(channel).Select(a => new OptiScalerChoice
            {
                Id = a,
                Name = versions.IsLatest(a) ? $"{a}  ({resources.GetString("LOCAtrioOptiLatest")})" : a
            }).ToList();
            if (!current.IsNullOrEmpty() && !versions.Contains(current) && versions.GetChannel(current) == channel)
            {
                list.Insert(0, new OptiScalerChoice { Id = current, Name = IsByHand ? $"{current}  ({resources.GetString("LOCAtrioOptiByHand")})" : current });
            }

            option.Fill(list, current, keepTouched, true);
            option.IsEnabled = list.Count > 1;
        }

        /// <summary>Another type of version: the newest of it is proposed, or the one that is already in the game.</summary>
        private void UpdateVersions()
        {
            filling = true;
            try
            {
                FillVersionsOfChannel(true);
            }
            finally
            {
                filling = false;
            }
        }

        private Dictionary<string, List<string>> modes = new Dictionary<string, List<string>>();

        private void FillFrames(JObject choices, JObject chosen, bool keepTouched)
        {
            var frames = chosen["frames"] as JObject;
            var route = (string)frames?["route"];
            var output = (string)frames?["output"];
            var replacement = (string)frames?["replacement"];
            var off = frames == null || route.IsNullOrEmpty() || route == "Disabled";
            var plain = replacement.IsNullOrEmpty() || replacement == "None";
            var optiFg = !off && plain && route == OptiFg && output == "FsrFg";

            // Choosing the way by hand, or going through DLSS Enabler, is done in the window of the client: here it's only kept.
            var special = !off && !optiFg && ((bool?)frames["advanced"] == true || !plain ||
                (route != "Auto" && output != "Nukem") || output == "DlssG" || output == "DlssGWithNvngx");
            var own = !special && (bool?)frames?[Own] == true;
            modes = new Dictionary<string, List<string>>();
            var list = new List<OptiScalerChoice>();
            if (special)
            {
                list.Add(new OptiScalerChoice { Id = Keep, Name = resources.GetString("LOCAtrioOptiFramesKeep") });
            }
            else if (own)
            {
                list.Add(OwnChoice("frames"));
            }

            list.Add(new OptiScalerChoice { Id = "Disabled", Name = Word("Frames", "Disabled") });
            foreach (var choice in (choices["frames"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var id = (string)choice["id"];
                if (id == "DlssG" || id == "DlssGWithNvngx")
                {
                    continue;
                }

                modes[id] = Strings(choice["modes"]);
                list.Add(new OptiScalerChoice { Id = id, Name = Word("Frames", id) });
            }

            options["frames"].Fill(list, special ? Keep : own ? Own : off ? "Disabled" : optiFg ? OptiFg : output, keepTouched);
            initialMode = special || own || off ? null : (string)frames["mode"];
            if (!keepTouched || !options["mode"].IsTouched)
            {
                options["mode"].Fill(new List<OptiScalerChoice>(), null, false);
            }
        }

        private string initialMode;

        /// <summary>How many frames can be put in between depends on how they are made.</summary>
        private void UpdateModes()
        {
            var option = options["mode"];
            var ids = modes.TryGetValue(options["frames"].Selected?.Id ?? string.Empty, out var found) ? found : new List<string>();
            var previous = option.IsTouched ? option.Selected?.Id : initialMode;
            filling = true;
            try
            {
                var same = options["frames"].Selected?.Id == options["frames"].Initial;
                option.Fill(Words("Mode", ids), same ? initialMode ?? ids.FirstOrDefault() : ids.Contains(previous) ? previous : ids.FirstOrDefault(), option.IsTouched);
                option.IsEnabled = ids.Count > 1;
                option.IsShown = ids.Count > 0;
            }
            finally
            {
                filling = false;
            }
        }

        private void FillFsr4(JObject choices, JObject chosen, bool keepTouched)
        {
            var list = new List<OptiScalerChoice> { new OptiScalerChoice { Id = None, Name = resources.GetString("LOCAtrioOptiNone") } };
            foreach (var version in (choices["fsr4"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var variant = (string)version["variant"];
                list.Add(new OptiScalerChoice { Id = (string)version["id"], Name = variant.IsNullOrEmpty() ? (string)version["id"] : $"{(string)version["id"]}  ({variant.ToUpperInvariant()})" });
            }

            options["fsr4"].Fill(list, (string)chosen["fsr4"] ?? None, keepTouched);
        }

        private void FillPatcher(JObject choices, JObject chosen, bool keepTouched)
        {
            var current = (string)chosen["patcher"] ?? None;
            var list = new List<OptiScalerChoice> { new OptiScalerChoice { Id = None, Name = resources.GetString("LOCAtrioOptiNone") } };
            if (current == "installed")
            {
                list.Add(new OptiScalerChoice { Id = current, Name = resources.GetString("LOCAtrioOptiPatcherInstalled") });
            }

            list.AddRange(Strings(choices["patchers"]).Take(8).Select(a => new OptiScalerChoice { Id = a, Name = a }));
            options["patcher"].Fill(list, current, keepTouched);
        }

        private void ShowDetected(JObject answer)
        {
            var detected = answer["detected"] as JObject;
            var chips = new List<string>();

            // Only what is of the game: the files OptiScaler brings say nothing about it.
            void add(string name, string key, string own)
            {
                var version = (string)detected?[key];
                if (!version.IsNullOrEmpty() && (own == null || (bool?)detected[own] != false))
                {
                    chips.Add($"{name} {version}");
                }
            }

            add("DLSS", "dlss", "dlssOwn");
            add(resources.GetString("LOCAtrioOptiDlssFrames"), "dlssFrames", null);
            add("FSR", "fsr", "fsrOwn");
            add("XeSS", "xess", "xessOwn");
            DetectedChips = chips;
            HasAntiCheat = (bool?)detected?["antiCheat"] == true;
            NothingDetected = chips.Count == 0 && (bool?)answer["installed"] != true;
            var gpu = answer["gpu"] as JObject;
            var generation = (string)gpu?["generation"];
            GpuText = gpu == null ? null : generation.IsNullOrEmpty() ? (string)gpu["name"] : $"{(string)gpu["name"]} ({generation})";
        }

        // The client has already looked for the list on the internet: a game it doesn't find isn't there.
        private bool listRead;

        /// <summary>The list of games of OptiScaler, at the row of this one. Without it, at the top, and not where it was left by another game.</summary>
        private string ListUrl => OptiScalerClient.GetCompatibilityUrl(
            (string)state?["list"], (string)(state?["compatibility"] as JObject)?["name"] ?? (listRead ? null : game.DisplayName));

        private void ShowCompatibility(JObject compatibility)
        {
            GuideUrl = (string)compatibility?["url"];
            if (compatibility == null)
            {
                CompatibilityText = null;
                CompatibilityNotes = null;
                return;
            }

            var status = (string)compatibility["status"];
            CompatibilityIsGood = status == "Compatible";
            CompatibilityText = Word("Compat", status);
            var notes = new List<string>();
            void add(string key, string value)
            {
                if (!value.IsNullOrWhiteSpace())
                {
                    notes.Add(string.Format(resources.GetString(key), value.Trim()));
                }
            }

            add("LOCAtrioOptiCompatInputs", (string)compatibility["inputs"]);
            add("LOCAtrioOptiCompatFile", (string)compatibility["filename"]);
            add("LOCAtrioOptiCompatFrames", (string)compatibility["frames"]);
            if ((bool?)compatibility["patcher"] == true)
            {
                notes.Add(resources.GetString("LOCAtrioOptiCompatPatcher"));
            }

            add("LOCAtrioOptiCompatNotes", (string)compatibility["notes"]);
            var issues = (int?)compatibility["issues"] ?? 0;
            if (issues > 0)
            {
                notes.Add(string.Format(resources.GetString(issues == 1 ? "LOCAtrioOptiCompatIssueOne" : "LOCAtrioOptiCompatIssues"), issues));
            }

            CompatibilityNotes = string.Join("\n", notes);
        }

        private void OptionChanged(OptiScalerOption option)
        {
            if (filling)
            {
                return;
            }

            if (option.Key == "frames")
            {
                UpdateModes();
            }
            else if (option.Key == "channel")
            {
                UpdateVersions();
            }
            else if (option.Key == "settings")
            {
                UpdateOwn(false);
            }

            UpdateActions();
            Notify();
        }

        // What took the place of «as set in the game» in each option.
        private readonly Dictionary<string, string> startedOver = new Dictionary<string, string>();

        /// <summary>Starting over, nothing stays as it's set in the game, unless it's chosen to afterwards.</summary>
        /// <param name="again">It was already starting over, and the game was read again.</param>
        private void UpdateOwn(bool again)
        {
            var fresh = options["settings"].Selected?.Id == Fresh;
            foreach (var option in Options.Where(a => a.Choices.Count > 1 && a.Choices[0].Id == Own))
            {
                if (fresh && option.Selected?.Id == Own && !(again && option.IsTouched))
                {
                    startedOver[option.Key] = option.Choices[1].Id;
                    option.Selected = option.Choices[1];
                }
                else if (!fresh && startedOver.TryGetValue(option.Key, out var other) && option.Selected?.Id == other)
                {
                    option.Selected = option.Choices[0];
                }
            }

            if (!fresh)
            {
                startedOver.Clear();
            }
        }

        private bool IsChanged(bool needsInstall)
        {
            return Options.Any(a => a.NeedsInstall == needsInstall && !a.IsFilter && a.IsChanged);
        }

        // The newer one the button proposes.
        private string updateVersion;

        /// <summary>What the main button does: install, apply only the settings, or install again because the files change.</summary>
        private void UpdateActions()
        {
            var installed = InstalledVersion != null;
            var managed = (bool?)state?["managed"] == true;
            var version = GetVersion();
            var hard = IsChanged(true);
            var soft = IsChanged(false);
            UpdateText = null;
            updateVersion = null;
            ShowsReinstall = false;
            if (!installed)
            {
                ShowsInstall = !version.IsNullOrEmpty();
                InstallText = string.Format(resources.GetString("LOCAtrioOptiInstall"), version);
                return;
            }

            ShowsInstall = hard || soft;
            ShowsReinstall = !managed && !hard && !soft && !version.IsNullOrEmpty();
            var same = string.Equals(version, InstalledVersion, StringComparison.OrdinalIgnoreCase) || (bool?)state?["half"] == true;
            InstallText = managed && !hard ? resources.GetString("LOCAtrioOptiApply") :
                string.Format(resources.GetString(same ? "LOCAtrioOptiReinstall" : "LOCAtrioOptiSwitch"), version);
            updateVersion = managed && !hard && !soft ? versions.GetUpdate(InstalledVersion, (string)state?["defaults"]?["version"]) : null;
            if (updateVersion != null)
            {
                UpdateText = string.Format(resources.GetString("LOCAtrioOptiUpdate"), updateVersion);
            }
        }

        private JObject GetOptions(bool all)
        {
            var result = new JObject();

            // What the person set inside the game and this version doesn't have comes back in one that does, unless they choose otherwise.
            var waiting = all && options["settings"].Selected?.Id != Fresh ? Strings(state?["own"]?["waiting"]) : new List<string>();
            foreach (var option in Options.Where(a => a.Key != "frames" && a.Key != "mode" && !a.IsFilter && a.Selected != null && (all || a.IsChanged)))
            {
                // A value that can't be chosen here (a quality of their own) stays as it is.
                if (!(option.Key == "quality" && option.Selected.Id == "Custom"))
                {
                    result[option.Key] = option.Key == "version" ? GetVersion() : waiting.Contains(option.Key) && !option.IsTouched ? Own : option.Selected.Id;
                }
            }

            var frames = options["frames"].Selected?.Id;
            if (frames != null && frames != Keep && (all || options["frames"].IsChanged || options["mode"].IsChanged))
            {
                var stays = waiting.Contains("frames") && !options["frames"].IsTouched && !options["mode"].IsTouched;
                result["frames"] = new JObject { ["output"] = stays ? Own : frames, ["mode"] = options["mode"].Selected?.Id };
            }

            return result;
        }

        private bool Confirm(string key, params object[] values)
        {
            // «No» is what comes out of pressing Enter or Escape without reading.
            var yes = new MessageBoxOption(resources.GetString("LOCYesLabel"));
            var no = new MessageBoxOption(resources.GetString("LOCNoLabel"), true, true);
            return dialogs.ShowMessage(
                string.Format(resources.GetString(key), values),
                "OptiScaler",
                MessageBoxImage.Warning,
                new List<MessageBoxOption> { yes, no }) == yes;
        }

        private bool IsFree()
        {
            if (IsGameOpen)
            {
                SetMessage(string.Format(resources.GetString("LOCAtrioOptiCloseGame"), game.DisplayName), true);
                return false;
            }

            return true;
        }

        private void Update()
        {
            if (updateVersion != null)
            {
                Install(updateVersion);
            }
        }

        /// <summary>
        /// The game may have been played since it was read, or its settings changed in their file: what wasn't chosen here goes
        /// on as it is now. As it's the beginning of a change, it goes on even if the page isn't showing anymore. False when it
        /// can't be known.
        /// </summary>
        private async Task<bool> LookAgainAsync()
        {
            var target = Target;
            SetMessage(null, false);
            ReportStep(new OptiScalerProgress { Step = "look" });
            stopping = false;
            Stage = OptiScalerStage.Working;
            try
            {
                var parameters = GetGame();
                parameters["force"] = true;
                var answer = await AskAsync("state", parameters, TimeSpan.FromSeconds(120), _ => { });
                if (Target != target)
                {
                    // It's another copy of the game now, or none.
                    Stage = OptiScalerStage.Ready;
                    return false;
                }

                Show(answer, true);
                Stage = OptiScalerStage.Ready;
                return true;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Fail(e, OptiScalerStage.Ready);
                TellAfterwards();
                return false;
            }
        }

        /// <summary>The game has closed: its settings may be other ones.</summary>
        public async void ReadAfterPlaying()
        {
            if (!IsOpen || Stage != OptiScalerStage.Ready || state == null || IsGameOpen)
            {
                return;
            }

            try
            {
                await ReadStateAsync(true, false, true);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // It's read again before changing anything.
                logger.Debug($"The game couldn't be looked at after it closed: {e.Message}");
            }
        }

        private async void Install(string version)
        {
            if (!CanChoose || !IsFree() || !await LookAgainAsync())
            {
                return;
            }

            var installed = InstalledVersion != null;
            var onlySettings = version == null && installed && !IsChanged(true) && (bool?)state?["managed"] == true;
            if (!onlySettings && !installed && HasAntiCheat && !Confirm("LOCAtrioOptiAntiCheatAsk", game.DisplayName))
            {
                return;
            }

            if (installed && (bool?)state?["managed"] != true && (bool?)state?["half"] != true && !Confirm("LOCAtrioOptiReplaceAsk", game.DisplayName, GetReplaced() + GetBeside()))
            {
                return;
            }

            // After one that was cut in the middle, what there was before isn't known here: what isn't chosen now goes on as it was.
            var chosen = GetOptions(!onlySettings && (bool?)state?["half"] != true);
            if (!onlySettings)
            {
                chosen["version"] = version ?? GetVersion();
            }

            await WorkAsync(onlySettings ? "apply" : "install", chosen, TimeSpan.FromMinutes(30), answer =>
            {
                var text = onlySettings ? resources.GetString("LOCAtrioOptiApplied") :
                    string.Format(resources.GetString("LOCAtrioOptiInstalled"), (string)answer["version"]);
                SetMessage(text + GetNotes(answer), false);
            });
        }

        private string GetNotes(JObject answer)
        {
            var notes = new List<string>();
            foreach (var note in Strings(answer["notes"]))
            {
                var parts = note.Split(new[] { ':' }, 2);
                var text = resources.GetString($"LOCAtrioOptiNote{parts[0]}");
                notes.Add(text.IsNullOrEmpty() || text.Contains("LOCAtrioOptiNote") ? note : string.Format(text, parts.Last().Trim()));
                if (parts.Length > 1 && parts[0] != "kept")
                {
                    logger.Warn($"OptiScaler Client: {note}");
                }
            }

            return notes.Count == 0 ? string.Empty : "\n" + string.Join("\n", notes);
        }

        /// <summary>What happens with the settings of the OptiScaler that was put by hand.</summary>
        private string GetReplaced()
        {
            return !HasOwn ? string.Empty : " " + resources.GetString(options["settings"].Selected?.Id == Fresh ? "LOCAtrioOptiReplaceFresh" : "LOCAtrioOptiReplaceKeep");
        }

        /// <summary>The files a game could have too that came with the OptiScaler that was put by hand: they go out with it.</summary>
        private string GetBeside()
        {
            var files = Strings(state?["beside"]);
            return files.Count == 0 ? string.Empty : "\n\n" + string.Format(resources.GetString("LOCAtrioOptiBeside"), string.Join(", ", files));
        }

        private async void Uninstall()
        {
            // What was put by hand has no copy of what was there before.
            var managed = (bool?)state?["managed"] == true || (bool?)state?["half"] == true || InstalledVersion == null;
            if (!CanChoose || !IsFree() || !Confirm(managed ? "LOCAtrioOptiUninstallAsk" : "LOCAtrioOptiUninstallManualAsk", game.DisplayName, GetBeside()))
            {
                return;
            }

            await WorkAsync("uninstall", null, TimeSpan.FromMinutes(10), answer =>
            {
                var remaining = Strings(answer["remaining"]);
                var done = remaining.Count > 0 ? string.Format(resources.GetString("LOCAtrioOptiRemaining"), string.Join(", ", remaining)) :
                    (bool?)answer["undone"] == true ? resources.GetString("LOCAtrioOptiUndone") :
                    resources.GetString(managed ? "LOCAtrioOptiUninstalled" : "LOCAtrioOptiUninstalledManual");
                SetMessage(done + GetNotes(answer), false);
            });
        }

        /// <summary>The page isn't showing anymore: how it went goes to the notifications.</summary>
        private void TellAfterwards()
        {
            if (closing.IsCancellationRequested && !Message.IsNullOrEmpty())
            {
                PlayniteApplication.Current?.Notifications?.Add(new NotificationMessage(
                    $"atrio-optiscaler-{game.Id}",
                    $"OptiScaler, {game.DisplayName}: {Message}",
                    MessageIsProblem ? NotificationType.Error : NotificationType.Info));
            }
        }

        private async Task WorkAsync(string method, JObject chosen, TimeSpan limit, Action<JObject> done)
        {
            SetMessage(null, false);
            stopping = false;
            reached = false;
            ReportStep(new OptiScalerProgress { Step = "look" });
            CanStop = method == "install";
            Stage = OptiScalerStage.Working;
            try
            {
                var folder = GetFolder(Target);
                var answer = await RunAsync(method, chosen, limit);
                if (answer == null)
                {
                    Stage = OptiScalerStage.Ready;
                    return;
                }

                // With this, what was done can be followed one day after.
                logger.Info($"OptiScaler: {method} in {folder}: {(string)answer["version"] ?? "none"}, with {chosen?.ToString(Formatting.None) ?? "{}"}, " +
                    $"notes [{string.Join("; ", Strings(answer["notes"]))}], left [{string.Join(", ", Strings(answer["remaining"]))}].");
                Show(answer, false);
                Stage = OptiScalerStage.Ready;
                done(answer);
                TellAfterwards();
            }
            catch (OperationCanceledException)
            {
                Stage = OptiScalerStage.Ready;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                var stopped = stopping && e is OptiScalerException ended &&
                    (ended.Code == OptiScalerException.Closed || ended.Code == OptiScalerException.NotSent || ended.Code == Stopped);
                stopping = false;
                if (stopped && !reached)
                {
                    SetMessage(resources.GetString("LOCAtrioOptiStopped"), false);
                    Stage = OptiScalerStage.Ready;
                    return;
                }

                if (stopped)
                {
                    // It had just begun with the game when it was stopped, and it goes on to its end by itself.
                    SetMessage(resources.GetString("LOCAtrioOptiTooLate"), true);
                    Stage = OptiScalerStage.Ready;
                }
                else
                {
                    Fail(e, state == null ? OptiScalerStage.Failed : OptiScalerStage.Ready);
                }

                TellAfterwards();
                await ReadAfterFailureAsync();
            }
            finally
            {
                CanStop = false;
            }
        }

        /// <summary>It failed, maybe with part of it done: what there is now is looked at again.</summary>
        private async Task ReadAfterFailureAsync()
        {
            try
            {
                if (Stage == OptiScalerStage.Ready && !closing.IsCancellationRequested)
                {
                    await ReadStateAsync(true, false, true, true);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug($"The game couldn't be looked at again: {e.Message}");
            }
        }

        /// <summary>Null when there was something to ask and the person said no.</summary>
        private async Task<JObject> RunAsync(string method, JObject chosen, TimeSpan limit)
        {
            var parameters = GetGame();
            parameters["options"] = chosen ?? new JObject();
            void report(OptiScalerProgress step)
            {
                reached = reached || step.Touched;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => ReportStep(step)));
            }

            try
            {
                return await AskAsync(method, parameters, limit, report);
            }
            catch (OptiScalerException e) when (e.Code == "leftovers")
            {
                if (!Confirm("LOCAtrioOptiLeftoversAsk", game.DisplayName))
                {
                    return null;
                }

                ((JObject)parameters["options"])["clean"] = true;
                return await AskAsync(method, parameters, limit, report);
            }
        }

        private void ReportStep(OptiScalerProgress step)
        {
            ProgressText = string.Format(resources.GetString($"LOCAtrioOptiStep{step.Step}") ?? "{0}", step.Item);
            IsIndeterminate = step.Percent == null;
            Progress = step.Percent ?? 0;
            CanStop = step.CanStop && Stage == OptiScalerStage.Working;
        }

        private volatile bool stopping;

        // The question in course has begun to change the game.
        private volatile bool reached;

        private void Stop()
        {
            if (Stage == OptiScalerStage.Downloading)
            {
                downloading?.Cancel();
                return;
            }

            if (CanStop)
            {
                stopping = true;
                if (bridge?.Stop() == false)
                {
                    stopping = false;
                    CanStop = false;
                }
            }
        }

        private bool IsClientFree()
        {
            if (OptiScalerSession.IsChanging)
            {
                SetMessage(resources.GetString("LOCAtrioOptiErrorbusy"), true);
                return false;
            }

            return true;
        }

        private async void DownloadClient()
        {
            if (IsWorking || !IsClientFree())
            {
                return;
            }

            var before = Stage;
            SetMessage(null, false);
            downloading = new CancellationTokenSource();
            ProgressText = resources.GetString("LOCAtrioOptiDownloadingClient");
            IsIndeterminate = true;
            Stage = OptiScalerStage.Downloading;
            CanStop = true;
            try
            {
                var latest = await OptiScalerClient.GetLatestAsync(false, downloading.Token);
                if (latest == null)
                {
                    throw new OptiScalerException("noversions", "No version of OptiScaler Client is known.");
                }

                // The one in use can't be replaced while it's working.
                OptiScalerSession.Close();
                var downloaded = await OptiScalerClient.DownloadAsync(
                    latest,
                    a => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsIndeterminate = a >= 100;
                        Progress = a;
                        CanStop = a < 100;
                    })),
                    downloading.Token);
                CanStop = false;
                Stage = OptiScalerStage.Starting;
                asked = false;
                Open();
            }
            catch (OperationCanceledException)
            {
                Stage = before;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Fail(e, copy == null ? OptiScalerStage.NoClient : state == null ? OptiScalerStage.Failed : OptiScalerStage.Ready);
            }
        }

        private void UpdateClientText()
        {
            ClientText = copy == null ? null : $"OptiScaler Client {copy.Version}";
            var unused = copy == null ? null : OptiScalerClient.GetUnused();
            KeptText = unused == null ? null : string.Format(
                resources.GetString(OptiScalerClient.GetContract(unused)?.Problem == null ? "LOCAtrioOptiClientKept" : "LOCAtrioOptiClientUntried"), unused.Version, copy.Version);
            var latest = OptiScalerClient.GetKnownLatest();
            var newer = latest != null && copy != null && OptiScalerClient.ParseVersion(latest.Version) > copy.Number;
            var there = newer && OptiScalerClient.GetCopies().Any(a => a.Version == latest.Version);
            ClientUpdateText = newer && !there ? string.Format(resources.GetString("LOCAtrioOptiClientUpdate"), latest.Version) : null;
            DownloadText = latest == null || latest.Size <= 0 ? resources.GetString("LOCAtrioOptiDownload") :
                string.Format(resources.GetString("LOCAtrioOptiDownloadSize"), DirectUninstall.FormatSize(latest.Size));
            Notify();
        }

        private async void CheckUpdates(bool now)
        {
            UpdateClientText();
            try
            {
                var latest = await OptiScalerClient.GetLatestAsync(now, closing.Token);
                UpdateClientText();
                if (now && !HasClientUpdate && KeptText.IsNullOrEmpty())
                {
                    SetMessage(string.Format(resources.GetString("LOCAtrioOptiClientLatest"), latest?.Version ?? copy?.Version), false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (OptiScalerException e)
            {
                logger.Debug($"No versions of OptiScaler Client: {e.Message}");
                if (now)
                {
                    SetMessage(resources.GetString("LOCAtrioOptiErrornetwork"), true);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to look for versions of OptiScaler Client.");
            }
        }

        /// <summary>The window of the client changes files too: what it left is read when it closes.</summary>
        private async void OpenWindow(bool ofTheGame)
        {
            if (copy == null || (ofTheGame && !IsAvailable) || !IsClientFree())
            {
                return;
            }

            if (OptiScalerClient.IsWindowOpen)
            {
                SetMessage(resources.GetString("LOCAtrioOptiErrorclientopen"), true);
                return;
            }

            try
            {
                SetMessage(null, false);
                JObject parameters = null;
                if (ofTheGame)
                {
                    parameters = GetGame();
                    parameters["language"] = (Localization.CurrentLanguage ?? string.Empty).Split('_')[0];
                }

                // One of them at a time on the same data.
                OptiScalerSession.Close();
                window = OptiScalerClient.OpenWindow(copy, parameters);
                if (window == null)
                {
                    return;
                }

                // What it changes is read when it closes, and each time the person comes back to Atrio meanwhile.
                var there = true;
                while (!closing.IsCancellationRequested && !window.HasExited)
                {
                    await Task.Delay(500, closing.Token);
                    var here = Application.Current?.MainWindow?.IsActive == true;
                    if (here && !there)
                    {
                        await ReadWithWindowAsync();
                    }

                    there = here;
                }

                window = null;
                await ReadAfterWindowAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Fail(e, Stage);
            }
        }

        private async Task ReadWithWindowAsync()
        {
            try
            {
                await ReadAfterWindowAsync();
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !(e is OutOfMemoryException))
            {
                // It's read again when the window closes.
                logger.Debug($"The game couldn't be looked at with the window open: {e.Message}");
            }
        }

        private async Task ReadAfterWindowAsync()
        {
            // The settings that were changed in the window are only read when the program starts.
            if (!OptiScalerSession.IsChanging)
            {
                OptiScalerSession.Close();
            }

            if (IsOpen && Stage == OptiScalerStage.Ready && Target != null)
            {
                await ReadStateAsync(true, false, true);
            }
            else if (!closing.IsCancellationRequested)
            {
                await LoadRecordAsync(0);
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                if (!path.IsNullOrEmpty() && (File.Exists(path) || Directory.Exists(path)))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to open {path}.");
            }
        }

        private void Fail(Exception exception, OptiScalerStage next)
        {
            var code = (exception as OptiScalerException)?.Code ?? "failed";
            if (exception is OptiScalerException)
            {
                logger.Warn($"OptiScaler: {code}: {exception.Message}");
            }
            else
            {
                logger.Error(exception, "Failed with OptiScaler.");
            }

            var key = $"LOCAtrioOptiError{code}";
            var text = resources.GetString(key);
            SetMessage(
                text.IsNullOrEmpty() || text.Contains(key) ? string.Format(resources.GetString("LOCAtrioOptiErrorfailed"), exception.Message) : string.Format(text, exception.Message),
                true);
            Stage = next;
        }

        private void SetMessage(string text, bool problem)
        {
            MessageIsProblem = problem;
            Message = text;
        }

        private void Notify()
        {
            foreach (var name in new[]
            {
                nameof(Stage), nameof(ShowsDownload), nameof(ShowsState), nameof(ShowsProgress), nameof(ShowsOnlyWindow), nameof(ShowsFailed), nameof(IsWorking),
                nameof(CanChoose), nameof(StatusText), nameof(StatusDetail), nameof(DetectedChips), nameof(GpuText), nameof(HasAntiCheat), nameof(NothingDetected),
                nameof(CompatibilityText), nameof(CompatibilityIsGood), nameof(CompatibilityNotes), nameof(HasCompatibility), nameof(GuideUrl), nameof(InstallText),
                nameof(ShowsInstall), nameof(ShowsReinstall), nameof(PackageText), nameof(HasPackage), nameof(UpdateText), nameof(ShowsUpdate), nameof(ShowsUninstall), nameof(HasIni), nameof(HasCopies), nameof(ClientText), nameof(KeptText), nameof(ClientUpdateText),
                nameof(HasClientUpdate), nameof(DownloadText)
            })
            {
                OnPropertyChanged(name);
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>The page goes away; what is being done to a game gets finished by itself.</summary>
        public void Dispose()
        {
            closing.Cancel();
        }
    }
}
