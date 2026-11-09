using Playnite.Common;
using Playnite.FullscreenApp.ViewModels;
using Playnite.SDK;
using Playnite.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Playnite.FullscreenApp.Controls
{
    /// <summary>A tab of a filter is checked when its filter is the active one and the start isn't showing.</summary>
    public class PresetTabConverter : IMultiValueConverter
    {
        /// <summary>
        /// Values: the active filter, whether the rows show, and (optional) the collection whose games show and
        /// whether the tab of the games with a controller is the one showing.
        /// </summary>
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return values?.Length > 1 && values[0] != null && Equals(values[0], parameter) && values[1] as bool? != true &&
                (values.Length < 3 || values[2] == null || values[2] == System.Windows.DependencyProperty.UnsetValue) &&
                (values.Length < 4 || values[3] as bool? != true);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    [TemplatePart(Name = "PART_ItemsFilterPresets", Type = typeof(ItemsControl))]
    public class FilterPresetSelector : Control
    {
        private readonly FullscreenAppViewModel mainModel;
        private ItemsControl ItemsFilterPresets;
        private CheckBoxEx homeItem;
        private CheckBoxEx collectionsItem;
        private CheckBoxEx controllerItem;
        private Playnite.SDK.Models.FilterPreset collectionsTabPreset;

        static FilterPresetSelector()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(FilterPresetSelector), new FrameworkPropertyMetadata(typeof(FilterPresetSelector)));
        }

        public FilterPresetSelector() : this(FullscreenApplication.Current?.MainModel)
        {
        }

        public FilterPresetSelector(FullscreenAppViewModel mainModel)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
            {
                this.mainModel = DesignMainViewModel.DesignIntance;
            }
            else if (mainModel != null)
            {
                this.mainModel = mainModel;
                mainModel.PropertyChanged += MainModel_PropertyChanged;
            }
        }

        private void MainModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ItemsFilterPresets == null)
            {
                return;
            }

            if (e.PropertyName == nameof(MainViewModelBase.SortedFilterFullscreenPresets))
            {
                ItemsFilterPresets.Items.Clear();

                // The start goes first, before the filters. Its name goes through the same converter as theirs.
                var homeName = ResourceProvider.GetString("LOCAtrioCouchHome");
                var home = new CheckBoxEx
                {
                    Style = ResourceProvider.GetResource<Style>("ItemFilterQuickPreset"),
                    Command = mainModel.ShowHomeCommand,
                    DataContext = new Playnite.SDK.Models.FilterPreset { Name = homeName }
                };

                AutomationProperties.SetName(home, homeName);
                BindingTools.SetBinding(home,
                    CheckBox.IsCheckedProperty,
                    mainModel,
                    nameof(mainModel.StartTabActive),
                    mode: BindingMode.OneWay);
                BindingTools.SetBinding(home,
                    UIElement.VisibilityProperty,
                    mainModel,
                    nameof(mainModel.ThemeHasHome),
                    converter: new Converters.BooleanToVisibilityConverter());
                homeItem = home;
                ItemsFilterPresets.Items.Add(home);

                // Right after the start: the collections, one row each. Its name says which one is open.
                collectionsTabPreset = new Playnite.SDK.Models.FilterPreset { Name = mainModel.CollectionsTabName };
                var collections = new CheckBoxEx
                {
                    Style = ResourceProvider.GetResource<Style>("ItemFilterQuickPreset"),
                    Command = mainModel.ShowCollectionsCommand,
                    DataContext = collectionsTabPreset
                };

                AutomationProperties.SetName(collections, collectionsTabPreset.Name);
                BindingTools.SetBinding(collections,
                    CheckBox.IsCheckedProperty,
                    mainModel,
                    nameof(mainModel.CollectionsTabActive),
                    mode: BindingMode.OneWay);
                BindingTools.SetBinding(collections,
                    UIElement.VisibilityProperty,
                    mainModel,
                    nameof(mainModel.ThemeHasHome),
                    converter: new Converters.BooleanToVisibilityConverter());
                collectionsItem = collections;
                ItemsFilterPresets.Items.Add(collections);

                // Then the games played with a controller, over the whole library.
                var controllerName = ResourceProvider.GetString("LOCAtrioCouchController");
                var controller = new CheckBoxEx
                {
                    Style = ResourceProvider.GetResource<Style>("ItemFilterQuickPreset"),
                    Command = mainModel.ShowControllerCommand,
                    DataContext = new Playnite.SDK.Models.FilterPreset { Name = controllerName }
                };

                AutomationProperties.SetName(controller, controllerName);
                BindingTools.SetBinding(controller,
                    CheckBox.IsCheckedProperty,
                    mainModel,
                    nameof(mainModel.ControllerTabActive),
                    mode: BindingMode.OneWay);
                BindingTools.SetBinding(controller,
                    UIElement.VisibilityProperty,
                    mainModel,
                    nameof(mainModel.ThemeHasHome),
                    converter: new Converters.BooleanToVisibilityConverter());
                controllerItem = controller;
                ItemsFilterPresets.Items.Add(controller);

                foreach (var preset in mainModel.SortedFilterFullscreenPresets)
                {
                    var item = new CheckBoxEx
                    {
                        Style = ResourceProvider.GetResource<Style>("ItemFilterQuickPreset"),
                        Command = mainModel.SelectFilterTabCommand,
                        CommandParameter = preset,
                        DataContext = preset
                    };

                    BindingTools.SetBinding(item,
                         AutomationProperties.NameProperty,
                         preset,
                         nameof(preset.Name));

                    // Checked when it is the active filter and the start isn't showing in front of it.
                    var isChecked = new MultiBinding { Converter = new PresetTabConverter(), ConverterParameter = preset, Mode = BindingMode.OneWay };
                    isChecked.Bindings.Add(new Binding(nameof(mainModel.ActiveFilterPreset)) { Source = mainModel });
                    isChecked.Bindings.Add(new Binding(nameof(mainModel.HomeActive)) { Source = mainModel });
                    isChecked.Bindings.Add(new Binding(nameof(mainModel.OpenCollection)) { Source = mainModel });
                    isChecked.Bindings.Add(new Binding(nameof(mainModel.ControllerOnly)) { Source = mainModel });
                    item.SetBinding(CheckBox.IsCheckedProperty, isChecked);
                    ItemsFilterPresets.Items.Add(item);
                }
            }

            if (e.PropertyName == nameof(FullscreenAppViewModel.HomeActive) && mainModel.HomeActive && homeItem != null)
            {
                ItemsFilterPresets.ScrollIntoView(mainModel.CollectionsTabActive && collectionsItem != null ? collectionsItem : homeItem);
            }

            if (e.PropertyName == nameof(FullscreenAppViewModel.ControllerTabActive) && mainModel.ControllerTabActive && controllerItem != null)
            {
                ItemsFilterPresets.ScrollIntoView(controllerItem);
            }

            // The tab shows its name through a converter of the whole object: a new object makes it read it again.
            if (e.PropertyName == nameof(FullscreenAppViewModel.CollectionsTabName) && collectionsItem != null)
            {
                collectionsTabPreset = new Playnite.SDK.Models.FilterPreset { Name = mainModel.CollectionsTabName };
                collectionsItem.DataContext = collectionsTabPreset;
                AutomationProperties.SetName(collectionsItem, collectionsTabPreset.Name);
            }

            if (e.PropertyName == nameof(MainViewModelBase.ActiveFilterPreset) && mainModel.ActiveFilterPreset != null)
            {
                foreach (CheckBoxEx item in ItemsFilterPresets.Items)
                {
                    if (item.DataContext == mainModel.ActiveFilterPreset)
                    {
                        ItemsFilterPresets.ScrollIntoView(item);
                        break;
                    }
                }
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (Template == null)
            {
                return;
            }

            ItemsFilterPresets = Template.FindName("PART_ItemsFilterPresets", this) as ItemsControl;
        }
    }
}
