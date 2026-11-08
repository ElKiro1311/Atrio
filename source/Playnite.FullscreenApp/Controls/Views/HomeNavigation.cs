using Playnite.Atrio;
using Playnite.Common;
using Playnite.FullscreenApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Playnite.FullscreenApp.Controls.Views
{
    /// <summary>
    /// The pad and the arrows over the rows of the start: left and right along a row, up and down to the
    /// same place of the row above or below. The game with the focus is the selected one, so its art and
    /// the buttons of the bottom are its own. Each tile is a button named "Tile".
    /// </summary>
    internal class HomeNavigation
    {
        public const string TileName = "Tile";

        private readonly FullscreenAppViewModel model;
        private readonly ItemsControl rows;
        private int lastRow;
        private int lastTile;

        public HomeNavigation(FullscreenAppViewModel model, ItemsControl rows)
        {
            this.model = model;
            this.rows = rows;
            rows.PreviewKeyDown += Rows_PreviewKeyDown;
            rows.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Rows_GotKeyboardFocus), true);
        }

        /// <summary>Where the arrows take the focus from a tile; null for a key that doesn't move it.</summary>
        public static Tuple<int, int> GetTarget(IReadOnlyList<int> rowSizes, int row, int index, Key key)
        {
            switch (key)
            {
                case Key.Left:
                    return Tuple.Create(row, Math.Max(0, index - 1));
                case Key.Right:
                    return Tuple.Create(row, Math.Min(rowSizes[row] - 1, index + 1));
                case Key.Up:
                case Key.Down:
                    // The next row with games, at the same place or at its last game.
                    var step = key == Key.Up ? -1 : 1;
                    for (var next = row + step; next >= 0 && next < rowSizes.Count; next += step)
                    {
                        if (rowSizes[next] > 0)
                        {
                            return Tuple.Create(next, Math.Min(index, rowSizes[next] - 1));
                        }
                    }

                    return Tuple.Create(row, index);
                default:
                    return null;
            }
        }

        private List<List<ButtonBase>> GetTiles()
        {
            var tiles = new List<List<ButtonBase>>();
            for (var i = 0; i < rows.Items.Count; i++)
            {
                var container = rows.ItemContainerGenerator.ContainerFromIndex(i);
                tiles.Add(container == null ?
                    new List<ButtonBase>() :
                    ElementTreeHelper.FindVisualChildren<ButtonBase>(container).Where(a => a.Name == TileName).ToList());
            }

            return tiles;
        }

        private static bool Locate(object element, List<List<ButtonBase>> tiles, out int row, out int index)
        {
            for (row = 0; row < tiles.Count; row++)
            {
                index = tiles[row].IndexOf(element as ButtonBase);
                if (index >= 0)
                {
                    return true;
                }
            }

            row = index = -1;
            return false;
        }

        private void Rows_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var tiles = GetTiles();
            if (!Locate(Keyboard.FocusedElement, tiles, out var row, out var index))
            {
                return;
            }

            var target = GetTarget(tiles.Select(a => a.Count).ToList(), row, index, e.Key);
            if (target == null)
            {
                return;
            }

            // At an end the key stays here, instead of the focus leaving the start.
            e.Handled = true;
            Focus(tiles, target.Item1, target.Item2);
            ShowRow(target.Item1);
        }

        /// <summary>The whole row in sight, with its title, not only the game with the focus.</summary>
        private void ShowRow(int row)
        {
            if (rows.ItemContainerGenerator.ContainerFromIndex(row) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }

        private void Rows_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var tiles = GetTiles();
            if (!Locate(e.NewFocus, tiles, out var row, out var index))
            {
                return;
            }

            lastRow = row;
            lastTile = index;
            if ((tiles[row][index].DataContext as HomeTile)?.Game is GamesCollectionViewEntry game && model.SelectedGame != game)
            {
                model.SelectedGame = game;
            }
        }

        private static void Focus(List<List<ButtonBase>> tiles, int row, int index)
        {
            if (row < 0 || row >= tiles.Count || tiles[row].Count == 0)
            {
                return;
            }

            var tile = tiles[row][Math.Max(0, Math.Min(index, tiles[row].Count - 1))];
            tile.Focus();
            tile.BringIntoView();
        }

        /// <summary>Back to the game that had the focus, or to the first one, once the rows are laid out.</summary>
        public void FocusSoon()
        {
            rows.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!model.HomeActive || model.GameDetailsVisible)
                {
                    return;
                }

                var tiles = GetTiles();
                if (lastRow >= tiles.Count || tiles[lastRow].Count == 0)
                {
                    lastRow = lastTile = 0;
                }

                Focus(tiles, lastRow, lastTile);
                ShowRow(lastRow);
            }), DispatcherPriority.Loaded);
        }
    }
}
