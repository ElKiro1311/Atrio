using Playnite.Database;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>One row of the start of the mode for the couch: a title and its games, with a note for each when it has one.</summary>
    public class HomeRow
    {
        public string Title { get; set; }
        public List<HomeTile> Tiles { get; set; } = new List<HomeTile>();
    }

    public class HomeTile
    {
        public GamesCollectionViewEntry Game { get; set; }

        /// <summary>A short note under the cover, like how many achievements are left.</summary>
        public string Note { get; set; }

        /// <summary>The last tile of a row of a collection: it opens the whole collection instead of a game.</summary>
        public GameCollection Collection { get; set; }

        /// <summary>What the tile says when it has no game.</summary>
        public string Label { get; set; }

        public string Title => Game?.DisplayName ?? Label;
    }

    /// <summary>
    /// The rows of the start of the mode for the couch, like the shelves of the start of the desktop:
    /// the games played lately, the collections pinned to the start, the ones still to try and the newest.
    /// The almost finished ones come later, once the add-on of the achievements has its numbers.
    /// </summary>
    public static class HomeRowBuilder
    {
        public const int RowSize = 20;

        public static List<HomeRow> Build(IEnumerable<GamesCollectionViewEntry> library, IGameDatabaseMain database, AtrioSettings settings)
        {
            var unifier = GameUnifier.Current;
            var seen = new HashSet<Guid>();
            var entries = (library ?? Enumerable.Empty<GamesCollectionViewEntry>()).
                Where(a => !a.Hidden && unifier?.IsSecondaryCopy(a.Id) != true && seen.Add(a.Id)).
                ToList();

            var rows = new List<HomeRow>();
            void add(string title, IEnumerable<GamesCollectionViewEntry> games)
            {
                var tiles = games.Take(RowSize).Select(a => new HomeTile { Game = a }).ToList();
                if (tiles.Count > 0)
                {
                    rows.Add(new HomeRow { Title = title, Tiles = tiles });
                }
            }

            add(ResourceProvider.GetString("LOCAtrioHomeRecent"),
                entries.Where(a => a.LastActivity != null).OrderByDescending(a => a.LastActivity));

            if (database != null && settings != null)
            {
                foreach (var id in settings.HomeCollections ?? new List<Guid>())
                {
                    var collection = GameCollections.Find(database, settings, id);
                    if (collection != null)
                    {
                        add(collection.Name, entries.Where(a => collection.Contains(a.Copies?.Select(b => b.Game) ?? new[] { a.Game })));
                    }
                }
            }

            add(ResourceProvider.GetString("LOCAtrioHomePlayNext"),
                entries.Where(a => a.Playtime == 0 && a.LastActivity == null).
                    OrderByDescending(a => a.IsInstalled).
                    ThenByDescending(a => a.CommunityScore ?? 0).
                    ThenByDescending(a => a.Added ?? DateTime.MinValue));

            add(ResourceProvider.GetString("LOCAtrioHomeAdded"),
                entries.Where(a => a.Added != null).OrderByDescending(a => a.Added));
            return rows;
        }

        /// <summary>
        /// The collections of the person, one row each with its first games and, at the end, a tile that opens the
        /// whole collection. Favorites, the ones the person made and the dynamic ones; not the ones of everything
        /// without a collection or of the hidden games, which would be most of the library.
        /// </summary>
        public static List<HomeRow> BuildCollections(IEnumerable<GamesCollectionViewEntry> library, IGameDatabaseMain database, AtrioSettings settings)
        {
            var rows = new List<HomeRow>();
            if (database == null || settings == null)
            {
                return rows;
            }

            var unifier = GameUnifier.Current;
            var seen = new HashSet<Guid>();
            var entries = (library ?? Enumerable.Empty<GamesCollectionViewEntry>()).
                Where(a => !a.Hidden && unifier?.IsSecondaryCopy(a.Id) != true && seen.Add(a.Id)).
                ToList();
            foreach (var collection in GameCollections.GetAll(database, settings))
            {
                if (collection.Kind == GameCollectionKind.Uncategorized || collection.Kind == GameCollectionKind.Hidden)
                {
                    continue;
                }

                var games = entries.
                    Where(a => collection.Contains(a.Copies?.Select(b => b.Game) ?? new[] { a.Game })).
                    OrderByDescending(a => a.LastActivity ?? DateTime.MinValue).
                    ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                    ToList();
                if (games.Count == 0)
                {
                    continue;
                }

                var tiles = games.Take(RowSize).Select(a => new HomeTile { Game = a }).ToList();
                tiles.Add(new HomeTile
                {
                    Collection = collection,
                    Label = string.Format(ResourceProvider.GetString("LOCAtrioCouchSeeCollection"), games.Count)
                });

                rows.Add(new HomeRow { Title = $"{collection.Name} ({games.Count})", Tiles = tiles });
            }

            return rows;
        }

        /// <summary>The row of the almost finished games, right after the recent ones, or nothing if there are none.</summary>
        public static List<HomeRow> AddAlmostDone(List<HomeRow> rows, IEnumerable<GamesCollectionViewEntry> library, IEnumerable<AchievementProgress.Entry> progress)
        {
            var games = AlmostDoneShelf.Choose(library, progress, RowSize);
            if (games.Count == 0)
            {
                return rows;
            }

            var recent = ResourceProvider.GetString("LOCAtrioHomeRecent");
            var updated = new List<HomeRow>(rows);
            updated.Insert(updated.Count > 0 && updated[0].Title == recent ? 1 : 0, new HomeRow
            {
                Title = ResourceProvider.GetString("LOCAtrioHomeAlmostDone"),
                Tiles = games.Select(a => new HomeTile { Game = a.Entry, Note = a.Text }).ToList()
            });
            return updated;
        }
    }
}
