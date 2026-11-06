using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Atrio
{
    public enum GameCollectionKind
    {
        Favorites,

        /// <summary>The user decides what goes in.</summary>
        Manual,

        /// <summary>Fills itself with the games that pass a saved filter.</summary>
        Dynamic,

        /// <summary>Every game outside of the manual collections.</summary>
        Uncategorized,

        Hidden
    }

    /// <summary>
    /// Games shown together. Manual collections are categories and dynamic ones are filter presets, so both
    /// keep working in add-ons, in the filters and in the fullscreen mode.
    /// </summary>
    public class GameCollection
    {
        public static readonly Guid FavoritesId = new Guid("a7c0de00-0000-4000-8000-000000000001");
        public static readonly Guid UncategorizedId = new Guid("a7c0de00-0000-4000-8000-000000000002");
        public static readonly Guid HiddenId = new Guid("a7c0de00-0000-4000-8000-000000000003");

        private readonly Func<Game, bool> matches;

        public Guid Id { get; }
        public GameCollectionKind Kind { get; }
        public string Name { get; }

        public bool IsUserMade => Kind == GameCollectionKind.Manual || Kind == GameCollectionKind.Dynamic;
        public bool AcceptsGames => Kind == GameCollectionKind.Manual || Kind == GameCollectionKind.Favorites;
        public bool ShowsHiddenGames => Kind == GameCollectionKind.Hidden;

        public GameCollection(Guid id, GameCollectionKind kind, string name, Func<Game, bool> matches)
        {
            Id = id;
            Kind = kind;
            Name = name;
            this.matches = matches;
        }

        /// <summary>Whether a game is in the collection, from all the copies it has in different stores.</summary>
        public bool Contains(IEnumerable<Game> copies)
        {
            return Kind == GameCollectionKind.Uncategorized ? copies.All(matches) : copies.Any(matches);
        }

        public static GameCollection FromCategory(Category category)
        {
            var id = category.Id;
            return new GameCollection(id, GameCollectionKind.Manual, category.Name, a => a.CategoryIds?.Contains(id) == true);
        }

        public static GameCollection FromPreset(FilterPreset preset, IGameDatabaseMain database)
        {
            var settings = preset.Settings ?? new FilterPresetSettings();
            var filter = FilterSettings.FromSdkFilterSettings(settings);
            filter.UseAndFilteringStyle = settings.UseAndFilteringStyle;
            return new GameCollection(preset.Id, GameCollectionKind.Dynamic, preset.Name, a => database.GetGameMatchesFilter(a, filter, false));
        }

        public static GameCollection CreateFavorites(string name)
        {
            return new GameCollection(FavoritesId, GameCollectionKind.Favorites, name, a => a.Favorite);
        }

        public static GameCollection CreateUncategorized(string name)
        {
            return new GameCollection(UncategorizedId, GameCollectionKind.Uncategorized, name, a => !a.CategoryIds.HasItems());
        }

        public static GameCollection CreateHidden(string name)
        {
            return new GameCollection(HiddenId, GameCollectionKind.Hidden, name, a => a.Hidden);
        }
    }

    public static class GameCollections
    {
        /// <summary>The ones the user made in between the ones that are always there, like in a library with shelves.</summary>
        public static List<GameCollection> GetAll(IGameDatabaseMain database, AtrioSettings settings)
        {
            var own = database.Categories.Select(GameCollection.FromCategory).ToList();
            foreach (var id in settings.DynamicCollections)
            {
                var preset = database.FilterPresets[id];
                if (preset != null)
                {
                    own.Add(GameCollection.FromPreset(preset, database));
                }
            }

            var all = new List<GameCollection> { GameCollection.CreateFavorites(ResourceProvider.GetString("LOCAtrioCollectionFavorites")) };
            all.AddRange(own.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
            all.Add(GameCollection.CreateUncategorized(ResourceProvider.GetString("LOCAtrioCollectionUncategorized")));
            all.Add(GameCollection.CreateHidden(ResourceProvider.GetString("LOCAtrioCollectionHidden")));
            return all;
        }

        public static GameCollection Find(IGameDatabaseMain database, AtrioSettings settings, Guid id)
        {
            return GetAll(database, settings).FirstOrDefault(a => a.Id == id);
        }

        public static IReadOnlyList<Game> GetCopies(Game game, GameUnifier unifier)
        {
            return (IReadOnlyList<Game>)unifier?.GetGroup(game.Id)?.Copies ?? new[] { game };
        }

        public static bool IsMember(Guid categoryId, Game game, GameUnifier unifier)
        {
            return GetCopies(game, unifier).Any(a => a.CategoryIds?.Contains(categoryId) == true);
        }

        /// <summary>
        /// Puts every copy of the games in or out of a collection, so that a game doesn't leave it the day another
        /// of its stores becomes the main one. Returns the copies that changed.
        /// </summary>
        public static List<Game> SetMembership(Guid categoryId, IEnumerable<Game> games, GameUnifier unifier, bool member)
        {
            var changed = new List<Game>();
            foreach (var copy in games.SelectMany(a => GetCopies(a, unifier)).Distinct())
            {
                var ids = copy.CategoryIds ?? new List<Guid>();
                if (ids.Contains(categoryId) != member)
                {
                    copy.CategoryIds = member ? ids.Concat(new[] { categoryId }).ToList() : ids.Where(a => a != categoryId).ToList();
                    changed.Add(copy);
                }
            }

            return changed;
        }

        public static void AddGames(IGameDatabaseMain database, Guid categoryId, IEnumerable<Game> games, GameUnifier unifier)
        {
            Save(database, SetMembership(categoryId, games, unifier, true));
        }

        public static void RemoveGames(IGameDatabaseMain database, Guid categoryId, IEnumerable<Game> games, GameUnifier unifier)
        {
            Save(database, SetMembership(categoryId, games, unifier, false));
        }

        private static void Save(IGameDatabaseMain database, List<Game> changed)
        {
            if (changed.Count > 0)
            {
                using (database.BufferedUpdate())
                {
                    database.Games.Update(changed);
                }
            }
        }
    }
}
