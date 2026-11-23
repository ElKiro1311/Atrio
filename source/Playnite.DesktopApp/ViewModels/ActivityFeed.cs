using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Converters;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>One store whose posts can be shown.</summary>
    public class ActivitySourceChoice : ObservableObject
    {
        private readonly Action<ActivitySourceChoice> chosen;

        public ActivitySource Source { get; }
        public string Name => Source.StoreName;

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    OnPropertyChanged();
                    if (value)
                    {
                        chosen(this);
                    }
                }
            }
        }

        public ActivitySourceChoice(ActivitySource source, bool selected, Action<ActivitySourceChoice> chosen)
        {
            Source = source;
            isSelected = selected;
            this.chosen = chosen;
        }
    }

    /// <summary>One line of the activity of a game: a post of its store, or something the person did.</summary>
    public class ActivityEntry : ObservableObject
    {
        public DateTime? Date { get; set; }

        /// <summary>The day, only in the first line of each one.</summary>
        public string DayText { get; set; }
        public bool HasDay => !DayText.IsNullOrEmpty();
        public ActivityPost Post { get; set; }
        public bool IsPost => Post != null;
        public string Label { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string ImageUrl { get; set; }

        /// <summary>The whole picture, for when there's no small one.</summary>
        public string FullImageUrl { get; set; }

        private string likesText;
        public string LikesText
        {
            get => likesText;
            set
            {
                likesText = value;
                OnPropertyChanged();
            }
        }

        private string commentsText;
        public string CommentsText
        {
            get => commentsText;
            set
            {
                commentsText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>It's read here, or on the page of its store when that's the only place that has it.</summary>
        public string ReadText { get; set; }

        /// <summary>Published after the last time it was played.</summary>
        public bool IsNew { get; set; }

        /// <summary>A patch or an update: what changes the game.</summary>
        public bool IsPatch { get; set; }

        /// <summary>The sign of what the person did, for the lines without a post.</summary>
        public string Glyph { get; set; }
        public List<UnlockedAchievement> Achievements { get; set; }

        /// <summary>It shows the same, apart from its counts: the line there is stays where it is.</summary>
        public bool IsSame(ActivityEntry other)
        {
            return other != null && Date == other.Date && DayText == other.DayText && Post?.Id == other.Post?.Id && Post?.Html == other.Post?.Html &&
                Label == other.Label && Title == other.Title && Text == other.Text && ImageUrl == other.ImageUrl &&
                IsNew == other.IsNew && IsPatch == other.IsPatch && Glyph == other.Glyph &&
                (Achievements?.Count ?? 0) == (other.Achievements?.Count ?? 0) &&
                (Achievements == null || Achievements.Select(a => a.Name).SequenceEqual(other.Achievements.Select(a => a.Name)));
        }
    }

    /// <summary>
    /// The activity of a game, like the one Steam shows under its title: the posts and the notes of the patches of the store
    /// the person uses for it, with what they did in the game in between.
    /// </summary>
    public class ActivityFeed : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const int AchievementsShown = 10;

        /// <summary>The ones that fit under the title; the others are made a moment later, one by one.</summary>
        private const int FirstEntries = 3;
        private const int PatchPages = 5;

        /// <summary>For how long a post is something new for who hasn't played since.</summary>
        private static readonly TimeSpan newLifetime = TimeSpan.FromDays(180);

        private readonly GamesCollectionViewEntry game;
        private readonly IResourceProvider resources;
        private readonly CancellationToken token;
        private readonly Func<ICollection<Guid>, Task<List<UnlockedAchievement>>> readAchievements;
        private readonly List<ActivityPost> posts = new List<ActivityPost>();
        private List<PlayerDay> days = new List<PlayerDay>();
        private ActivitySourceChoice current;
        private bool hasMore;
        private int next;
        private bool fromSteam;
        private bool loaded;
        private bool daysLoaded;
        private int ticket;

        // One for each store that is chosen: the new one doesn't wait for the one before.
        private SemaphoreSlim loading = new SemaphoreSlim(1, 1);

        public List<ActivitySourceChoice> Sources { get; }
        public bool HasSources => Sources.Count > 1;
        public RelayCommand<object> MoreCommand { get; }
        public RelayCommand<ActivityEntry> OpenCommand { get; }

        /// <summary>The lines as they are on the screen, on their way to be the ones wanted.</summary>
        public ObservableCollection<ActivityEntry> Entries { get; } = new ObservableCollection<ActivityEntry>();
        private List<ActivityEntry> wanted = new List<ActivityEntry>();

        private bool onlyPatches;

        /// <summary>Only what changes the game: its patches and its updates.</summary>
        public bool OnlyPatches
        {
            get => onlyPatches;
            set
            {
                if (onlyPatches != value)
                {
                    onlyPatches = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowsAll));
                    Rebuild();
                    if (value)
                    {
                        FindPatches();
                    }
                }
            }
        }

        public bool ShowsAll
        {
            get => !OnlyPatches;
            set => OnlyPatches = !value;
        }

        private bool isLoading;
        public bool IsLoading
        {
            get => isLoading;
            private set
            {
                isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }

        public bool CanLoadMore => hasMore && !IsLoading;

        private string noteText;

        /// <summary>Where the posts come from, when it isn't the store of the game.</summary>
        public string NoteText
        {
            get => noteText;
            private set
            {
                noteText = value;
                OnPropertyChanged();
            }
        }

        private string emptyText;
        public string EmptyText
        {
            get => emptyText;
            private set
            {
                emptyText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The choice stays while only the patches show, even in a store without any: it's the way back.</summary>
        public bool HasPatches => OnlyPatches || posts.Any(a => a.IsPatch);

        public ActivityFeed(
            GamesCollectionViewEntry game,
            IResourceProvider resources,
            Func<ICollection<Guid>, Task<List<UnlockedAchievement>>> readAchievements,
            CancellationToken token)
        {
            this.game = game;
            this.resources = resources;
            this.readAchievements = readAchievements;
            this.token = token;
            StoreActivity.Language = Localization.CurrentLanguage;
            StoreActivity.VideoText = resources.GetString("LOCAtrioActivityVideo");
            StoreActivity.WhatsNewText = resources.GetString("LOCAtrioActivityWhatsNew");

            // The copy the person uses comes first: its store is the one that shows.
            var copies = new[] { game.Game }.Concat(game.Copies.Select(a => a.Game)).Where(a => a != null).Distinct().ToList();
            var names = game.Copies.GroupBy(a => a.Game.Id).ToDictionary(a => a.Key, a => a.First().StoreName);
            Sources = StoreActivity.GetSources(copies, a => names.TryGetValue(a.Id, out var name) ? name : a.Source?.Name ?? string.Empty).
                Select((a, i) => new ActivitySourceChoice(a, i == 0, Choose)).
                ToList();
            current = Sources.FirstOrDefault();
            MoreCommand = new RelayCommand<object>(_ => LoadMore(), _ => CanLoadMore);
            OpenCommand = new RelayCommand<ActivityEntry>(Open);
            ShowKnown();
        }

        /// <summary>What was seen a moment ago comes with the page; what is on the disk, and the stores, right after.</summary>
        private void ShowKnown()
        {
            posts.Clear();
            hasMore = false;
            next = 0;
            loaded = false;
            NoteText = null;
            EmptyText = null;
            Entries.Clear();
            var known = current == null ? null : StoreActivity.GetFromMemory(current.Source);
            if (known != null)
            {
                Take(known);
            }

            Rebuild();
        }

        /// <summary>False when there was nothing new in it.</summary>
        private bool Take(ActivityPage page)
        {
            var added = page.Posts.Where(a => a.Id.IsNullOrEmpty() || posts.All(b => b.Id != a.Id)).ToList();
            posts.AddRange(added);

            // A store that answers the same again has no more, whatever it says.
            hasMore = page.HasMore && added.Count > 0;
            next = page.Next;
            fromSteam = page.FromSteam;
            loaded = true;
            return added.Count > 0;
        }

        /// <summary>The newest ones again: what was loaded further back stays when they reach it.</summary>
        private void Renew(ActivityPage page)
        {
            var reaches = posts.Count > page.Posts.Count && page.Posts.Any(a => posts.Any(b => b.Id == a.Id));
            var older = reaches ? posts.Where(a => page.Posts.All(b => b.Id != a.Id)).ToList() : new List<ActivityPost>();
            var more = hasMore;
            var from = next + page.Posts.Count + older.Count - posts.Count;
            posts.Clear();
            Take(page);
            if (reaches)
            {
                posts.AddRange(older);
                hasMore = more;
                next = from;
            }
        }

        /// <summary>Asks the store and what the person did. It never fails: without internet the page shows what it has.</summary>
        public async Task LoadAsync(int pause)
        {
            try
            {
                if (!loaded && current != null)
                {
                    var source = current.Source;
                    var known = await Task.Run(() => StoreActivity.GetKnown(source), token);
                    if (known != null && !loaded && current?.Source == source)
                    {
                        Take(known);
                        Rebuild();
                    }
                }

                var days = LoadDaysAsync(pause);
                await Task.Delay(pause, token);
                await RefreshAsync();
                await days;
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>The time played and the achievements of each day. False when the page went away.</summary>
        private async Task<bool> LoadDaysAsync(int pause)
        {
            try
            {
                await Task.Delay(pause, token);
                var ids = new HashSet<Guid>(game.Copies.Select(a => a.Game.Id)) { game.Id };
                var sessions = await Task.Run(() => PlayerActivity.GetSessions(ids), token);
                var achievements = readAchievements == null ? null : await readAchievements(ids);
                token.ThrowIfCancellationRequested();
                days = PlayerActivity.Group(sessions, achievements);
                daysLoaded = true;
                Rebuild();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read what was done in {game.Name}.");
            }

            return true;
        }

        /// <summary>After playing: what was new isn't anymore, and there's one more session, written down a moment later.</summary>
        public async void UpdatePlayed()
        {
            try
            {
                Rebuild();
                if (daysLoaded)
                {
                    await LoadDaysAsync(3000);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to update the activity of {game.Name}.");
            }
        }

        private async Task RefreshAsync()
        {
            var source = current?.Source;
            if (source == null)
            {
                Rebuild();
                return;
            }

            var own = ticket;
            var turn = loading;
            await turn.WaitAsync(token);
            try
            {
                var known = await Task.Run(() => StoreActivity.GetKnown(source), token);
                if (own != ticket)
                {
                    return;
                }

                if (StoreActivity.IsFresh(known))
                {
                    if (!loaded)
                    {
                        Take(known);
                    }
                }
                else
                {
                    IsLoading = true;
                    EmptyText = null;
                    var page = await StoreActivity.GetAsync(source, 0, false, token);
                    token.ThrowIfCancellationRequested();

                    // The ones of Steam only because the store doesn't answer now don't take the place of its own.
                    if (own == ticket && (!page.IsPassing || (page.Posts.Count > 0 && (posts.Count == 0 || fromSteam))))
                    {
                        Renew(page);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read the activity of {game.Name}.");
            }
            finally
            {
                turn.Release();
                if (own == ticket)
                {
                    IsLoading = false;
                    Rebuild();
                }
            }

            if (OnlyPatches && own == ticket)
            {
                FindPatches();
            }
        }

        private async void Choose(ActivitySourceChoice choice)
        {
            foreach (var other in Sources.Where(a => a != choice))
            {
                other.IsSelected = false;
            }

            if (current == choice)
            {
                return;
            }

            current = choice;
            ticket++;
            loading = new SemaphoreSlim(1, 1);
            IsLoading = false;
            try
            {
                ShowKnown();
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to show the activity of {game.Name} in another store.");
            }
        }

        private async void LoadMore()
        {
            if (await LoadMoreAsync() && OnlyPatches)
            {
                FindPatches();
            }
        }

        private async Task<bool> LoadMoreAsync()
        {
            var source = current?.Source;
            if (source == null || !CanLoadMore)
            {
                return false;
            }

            var own = ticket;
            var turn = loading;
            var entered = false;
            try
            {
                await turn.WaitAsync(token);
                entered = true;
                if (own != ticket || !hasMore)
                {
                    return false;
                }

                IsLoading = true;
                var page = await StoreActivity.GetAsync(source, next, fromSteam, token);
                token.ThrowIfCancellationRequested();
                return own == ticket && Take(page);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read older activity of {game.Name}.");
                return false;
            }
            finally
            {
                if (entered)
                {
                    turn.Release();
                }

                if (own == ticket)
                {
                    IsLoading = false;
                    Rebuild();
                }
            }
        }

        /// <summary>A store that writes a lot may have its last patch some pages away: a few are looked through.</summary>
        private async void FindPatches()
        {
            for (var i = 0; i < PatchPages && OnlyPatches && posts.Count(a => a.IsPatch) < StoreActivity.PageSize / 2; i++)
            {
                if (!await LoadMoreAsync())
                {
                    break;
                }
            }
        }

        /// <summary>Puts the list together again with what is known.</summary>
        private void Rebuild()
        {
            var culture = Localization.ApplicationLanguageCultureInfo;
            var lastPlayed = game.Copies.Select(a => a.Game.LastActivity).Where(a => a != null).DefaultIfEmpty().Max();
            var list = new List<ActivityEntry>();
            DateTime? last = null;
            foreach (var post in posts)
            {
                // One without a date goes right after the one before it, in the order of its store.
                last = post.Date ?? last;
                if (!OnlyPatches || post.IsPatch)
                {
                    list.Add(CreatePost(post, last, lastPlayed, culture));
                }
            }

            if (!OnlyPatches)
            {
                // What the person did goes up to where the posts go: older things wait for the older posts.
                var limit = hasMore ? posts.Select(a => a.Date).Where(a => a != null).DefaultIfEmpty().Min() : null;
                list.AddRange(CreateDays(culture).Where(a => limit == null || a.Date >= limit));
            }

            // Stable: the posts of the same moment stay as their store orders them. The first ones without a date are the last
            // thing it published.
            list = list.OrderByDescending(a => a.Date ?? (a.IsPost ? DateTime.MaxValue : DateTime.MinValue)).ToList();
            string day = null;
            foreach (var entry in list)
            {
                var text = entry.Post != null && entry.Post.Date == null ? null : GetDayText(entry.Date, DateTime.Now, culture);
                entry.DayText = text != day ? text : null;
                day = text ?? day;
            }

            Show(list);
            var store = current?.Name;
            NoteText = loaded && fromSteam && posts.Count > 0 && store != null ? string.Format(resources.GetString("LOCAtrioActivityFromSteam"), store) : null;
            EmptyText = IsLoading || !loaded ? null :
                posts.Count == 0 ? resources.GetString(current?.Source.Copy.PluginId == Guid.Empty ? "LOCAtrioActivityNoStore" : "LOCAtrioActivityNone") :
                OnlyPatches && list.Count == 0 && !hasMore ? resources.GetString("LOCAtrioActivityNoPatches") : null;
            OnPropertyChanged(nameof(CanLoadMore));
            OnPropertyChanged(nameof(HasPatches));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Takes the list on the screen to the one wanted, touching only what changes: making ten lines again at once is
        /// something that shows when going from one game to another.
        /// </summary>
        private void Show(List<ActivityEntry> list)
        {
            wanted = list;
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                if (!wanted.Any(a => a.IsSame(Entries[i])))
                {
                    Entries.RemoveAt(i);
                }
            }

            AddSome(list, FirstEntries);
        }

        private void AddSome(List<ActivityEntry> list, int count)
        {
            if (list != wanted || token.IsCancellationRequested)
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (i < Entries.Count && Entries[i].IsSame(list[i]))
                {
                    if (!ReferenceEquals(Entries[i], list[i]))
                    {
                        Entries[i].LikesText = list[i].LikesText;
                        Entries[i].CommentsText = list[i].CommentsText;
                    }

                    continue;
                }

                var later = -1;
                for (var j = i + 1; j < Entries.Count && later < 0; j++)
                {
                    later = Entries[j].IsSame(list[i]) ? j : -1;
                }

                if (later > 0)
                {
                    Entries.Move(later, i);
                }
                else if (count-- > 0)
                {
                    Entries.Insert(i, list[i]);
                }
                else
                {
                    // The rest when the window has nothing better to do.
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => AddSome(list, 1)));
                    return;
                }
            }

            while (Entries.Count > list.Count)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        }

        private ActivityEntry CreatePost(ActivityPost post, DateTime? date, DateTime? lastPlayed, CultureInfo culture)
        {
            return new ActivityEntry
            {
                Post = post,
                Date = date,
                Label = GetLabel(post.Kind, resources),
                Title = post.Title,
                Text = post.Summary,
                ImageUrl = post.ThumbnailUrl ?? post.ImageUrl,
                FullImageUrl = post.ImageUrl,
                ReadText = resources.GetString(post.Html.IsNullOrEmpty() ? "LOCAtrioActivityOpenWeb" : "LOCAtrioActivityRead"),
                LikesText = GetCount(post.Likes, culture),
                CommentsText = GetCount(post.Comments, culture),
                IsPatch = post.IsPatch,
                IsNew = lastPlayed != null && post.Date > lastPlayed && post.Date > DateTime.Now - newLifetime
            };
        }

        private IEnumerable<ActivityEntry> CreateDays(CultureInfo culture)
        {
            var converter = new PlayTimeToStringConverter();
            foreach (var day in days)
            {
                if (day.Achievements.Count > 0)
                {
                    yield return new ActivityEntry
                    {
                        Date = day.Last,
                        Glyph = "\xE734",
                        Title = day.Achievements.Count == 1 ?
                            string.Format(resources.GetString("LOCAtrioActivityAchievementOne"), day.Achievements[0].Name) :
                            string.Format(resources.GetString("LOCAtrioActivityAchievements"), day.Achievements.Count),
                        Achievements = day.Achievements.Take(AchievementsShown).ToList()
                    };
                }

                if (day.Seconds >= 60)
                {
                    yield return new ActivityEntry
                    {
                        Date = day.Last,
                        Glyph = "\xE7FC",
                        Title = string.Format(resources.GetString("LOCAtrioActivityPlayedDay"), converter.Convert((ulong)day.Seconds, typeof(string), null, culture))
                    };
                }
            }

            var lastActivity = game.Game.LastActivity;
            if (lastActivity != null && days.All(a => a.Day != lastActivity.Value.Date))
            {
                yield return new ActivityEntry { Date = lastActivity, Glyph = "\xE7FC", Title = resources.GetString("LOCAtrioActivityPlayed") };
            }

            if (game.Game.Added != null)
            {
                yield return new ActivityEntry { Date = game.Game.Added, Glyph = "\xE710", Title = resources.GetString("LOCAtrioActivityAdded") };
            }
        }

        public static string GetLabel(ActivityKind kind, IResourceProvider resources)
        {
            return resources.GetString($"LOCAtrioActivityKind{kind}");
        }

        /// <summary>5512 becomes «5,5 mil» where the language says it that way; nothing when there's none.</summary>
        public static string GetCount(int count, CultureInfo culture)
        {
            return count <= 0 ? null :
                count < 1000 ? count.ToString(culture) :
                count < 1000000 ? (count / 1000.0).ToString(count < 10000 ? "0.#" : "0", culture) + " k" :
                (count / 1000000.0).ToString("0.#", culture) + " M";
        }

        /// <summary>The day as Steam writes it: today and yesterday by their name, the year only when it's another one.</summary>
        public string GetDayText(DateTime? date, DateTime now, CultureInfo culture)
        {
            if (date == null || date.Value.Year < 1980)
            {
                return null;
            }

            var day = date.Value.Date;
            if (day == now.Date)
            {
                return resources.GetString("LOCAtrioActivityToday");
            }

            if (day == now.Date.AddDays(-1))
            {
                return resources.GetString("LOCAtrioActivityYesterday");
            }

            return day.ToString(day.Year == now.Year ? culture.DateTimeFormat.MonthDayPattern : GetDatePattern(culture), culture);
        }

        /// <summary>The long date of the language without the day of the week: «7 de octubre de 2025», «October 7, 2025».</summary>
        public static string GetDatePattern(CultureInfo culture)
        {
            var pattern = Regex.Replace(culture.DateTimeFormat.LongDatePattern, @"(?<!d)dddd?(?!d)\.?,?\s*", string.Empty).Trim(' ', ',', '.');
            return pattern.Contains("y") && pattern.Contains("M") ? pattern : "d MMMM yyyy";
        }

        private void Open(ActivityEntry entry)
        {
            if (entry?.Post == null)
            {
                return;
            }

            try
            {
                if (entry.Post.Html.IsNullOrEmpty())
                {
                    if (!entry.Post.Url.IsNullOrEmpty())
                    {
                        GlobalCommands.NavigateUrl(entry.Post.Url);
                    }

                    return;
                }

                var readable = wanted.Where(a => a.Post != null && !a.Post.Html.IsNullOrEmpty()).ToList();
                Windows.ActivityReaderWindow.Open(game.DisplayName, readable, Math.Max(0, readable.FindIndex(a => a.IsSame(entry))));
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to open a post.");
            }
        }
    }
}
