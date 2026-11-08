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
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return values?.Length > 1 && values[0] != null && Equals(values[0], parameter) && values[1] as bool? != true;
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
                    nameof(mainModel.HomeActive),
                    mode: BindingMode.OneWay);
                BindingTools.SetBinding(home,
                    UIElement.VisibilityProperty,
                    mainModel,
                    nameof(mainModel.ThemeHasHome),
                    converter: new Converters.BooleanToVisibilityConverter());
                homeItem = home;
                ItemsFilterPresets.Items.Add(home);

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
                    item.SetBinding(CheckBox.IsCheckedProperty, isChecked);
                    ItemsFilterPresets.Items.Add(item);
                }
            }

            if (e.PropertyName == nameof(FullscreenAppViewModel.HomeActive) && mainModel.HomeActive && homeItem != null)
            {
                ItemsFilterPresets.ScrollIntoView(homeItem);
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
