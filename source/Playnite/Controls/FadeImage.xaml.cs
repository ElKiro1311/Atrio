using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Playnite.Controls
{
    /// <summary>
    /// Interaction logic for FadeImage.xaml
    /// </summary>
    public partial class FadeImage : UserControl
    {
        private enum CurrentImage
        {
            Image1,
            Image2,
            None
        }

        private CurrentImage currentImage = CurrentImage.None;
        private object currentSource = null;
        private System.Timers.Timer sourceChangeTimer = new System.Timers.Timer() { AutoReset = false };
        private object newSource;
        private readonly SynchronizationContext context;

        internal Storyboard Image1FadeIn;
        internal Storyboard Image2FadeIn;
        internal Storyboard Image1FadeOut;
        internal Storyboard Image2FadeOut;
        internal Storyboard stateAnim;
        internal Storyboard BorderDarkenFadeOut;

        public double SourceUpdateDelay { get; set; } = 0;

        /// <summary>
        /// <see cref="SourceUpdateDelay"/> applies only to a run of changes: the first one after a pause
        /// is loaded at once. Going through a list doesn't load everything, a single step feels immediate.
        /// </summary>
        public bool DelayOnlyRuns { get; set; } = false;

        private DateTime lastSourceChange = DateTime.MinValue;

        private double fadeSeconds = 0.5;

        /// <summary>How long a change of picture takes.</summary>
        public double FadeSeconds
        {
            get => fadeSeconds;
            set
            {
                fadeSeconds = value;
                var speed = 0.5 / value;
                Image1FadeIn.SpeedRatio = speed;
                Image2FadeIn.SpeedRatio = speed;
                Image1FadeOut.SpeedRatio = speed;
                Image2FadeOut.SpeedRatio = speed;
                BorderDarkenFadeOut.SpeedRatio = speed;
            }
        }

        private const int BlurredCopyWidth = 320;
        private const int BlurredCopiesLimit = 100;

        // Keeps the blur of the effect on a window of 1920 pixels: the copy is six times smaller, and
        // three box blurs in a row reach further than their radius.
        private const int BlurAmountPerCopyRadius = 20;

        // They weigh very little, and going back to a game finds its copy ready.
        private static readonly Dictionary<string, BitmapSource> blurredCopies = new Dictionary<string, BitmapSource>();

        #region AnimationEnabled

        public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
            nameof(AnimationEnabled),
            typeof(bool),
            typeof(FadeImage),
            new PropertyMetadata(true));

        public bool AnimationEnabled
        {
            get { return (bool)GetValue(AnimationEnabledProperty); }
            set { SetValue(AnimationEnabledProperty, value); }
        }

        #endregion AnimationEnabled

        #region Source

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source),
            typeof(object),
            typeof(FadeImage),
            new PropertyMetadata(null, SourceChanged));

        public object Source
        {
            get { return GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        #endregion Source

        #region ImageOpacityMask

        public static readonly DependencyProperty ImageOpacityMaskProperty = DependencyProperty.Register(
            nameof(ImageOpacityMask),
            typeof(Brush),
            typeof(FadeImage),
            new PropertyMetadata());

        public Brush ImageOpacityMask
        {
            get { return (Brush)GetValue(ImageOpacityMaskProperty); }
            set { SetValue(ImageOpacityMaskProperty, value); }
        }

        #endregion ImageOpacityMask

        #region ImageDarkeningBrush

        public static readonly DependencyProperty ImageDarkeningBrushProperty = DependencyProperty.Register(
            nameof(ImageDarkeningBrush),
            typeof(Brush),
            typeof(FadeImage),
            new PropertyMetadata());

        public Brush ImageDarkeningBrush
        {
            get { return (Brush)GetValue(ImageDarkeningBrushProperty); }
            set { SetValue(ImageDarkeningBrushProperty, value); }
        }

        #endregion ImageDarkeningBrush

        #region Stretch

        public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(FadeImage),
            new PropertyMetadata(Stretch.UniformToFill));

        public Stretch Stretch
        {
            get { return (Stretch)GetValue(StretchProperty); }
            set { SetValue(StretchProperty, value); }
        }

        #endregion Strech

        #region IsBlurEnabled

        public static readonly DependencyProperty IsBlurEnabledProperty = DependencyProperty.Register(
            nameof(IsBlurEnabled),
            typeof(bool),
            typeof(FadeImage),
            new PropertyMetadata(false, BlurSettingChanged));

        public bool IsBlurEnabled
        {
            get { return (bool)GetValue(IsBlurEnabledProperty); }
            set { SetValue(IsBlurEnabledProperty, value); }
        }

        #endregion IsBlurEnabled

        #region BlurAmount

        public static readonly DependencyProperty BlurAmountProperty = DependencyProperty.Register(
            nameof(BlurAmount),
            typeof(int),
            typeof(FadeImage),
            new PropertyMetadata(10, BlurSettingChanged));

        public int BlurAmount
        {
            get { return (int)GetValue(BlurAmountProperty); }
            set { SetValue(BlurAmountProperty, value); }
        }

        #endregion IsBlurEnabled

        #region HighQualityBlur

        public static readonly DependencyProperty HighQualityBlurProperty = DependencyProperty.Register(
            nameof(HighQualityBlurProperty),
            typeof(bool),
            typeof(FadeImage),
            new PropertyMetadata(false, BlurSettingChanged));

        public bool HighQualityBlur
        {
            get { return (bool)GetValue(HighQualityBlurProperty); }
            set { SetValue(HighQualityBlurProperty, value); }
        }

        #endregion HighQualityBlur

        #region UseBlurredCopy

        public static readonly DependencyProperty UseBlurredCopyProperty = DependencyProperty.Register(
            nameof(UseBlurredCopy),
            typeof(bool),
            typeof(FadeImage),
            new PropertyMetadata(false, BlurSettingChanged));

        /// <summary>
        /// Does the blur on a small copy of the picture, once, when it loads. The effect is limited to a
        /// radius of 100 and it's done again for every frame, which is a lot over a whole window in the
        /// middle of a change of picture. The copy has no limit and it costs nothing to draw.
        /// </summary>
        public bool UseBlurredCopy
        {
            get { return (bool)GetValue(UseBlurredCopyProperty); }
            set { SetValue(UseBlurredCopyProperty, value); }
        }

        #endregion UseBlurredCopy

        public FadeImage()
        {
            InitializeComponent();
            context = SynchronizationContext.Current;
            Image1FadeIn = (Storyboard)TryFindResource("Image1FadeIn");
            Image2FadeIn = (Storyboard)TryFindResource("Image2FadeIn");
            Image1FadeOut = (Storyboard)TryFindResource("Image1FadeOut");
            Image2FadeOut = (Storyboard)TryFindResource("Image2FadeOut");
            BorderDarkenFadeOut = (Storyboard)TryFindResource("BorderDarkenFadeOut");
            Image2FadeIn.Completed += Image2FadeIn_Completed;
            Image1FadeOut.Completed += Image1FadeOut_Completed;
            Image2FadeOut.Completed += Image2FadeOut_Completed;
            BorderDarkenFadeOut.Completed += BorderDarkenOut_Completed;
            sourceChangeTimer.Elapsed += (_, __) => context.Send((___) => LoadNewSource(), null);
        }

        ~FadeImage()
        {
            sourceChangeTimer.Dispose();
        }

        private void Image2FadeIn_Completed(object sender, EventArgs e)
        {
            if (currentImage == CurrentImage.Image2)
            {
                Image1.Source = null;
            }
        }

        private void Image1FadeOut_Completed(object sender, EventArgs e)
        {
            Image1.Source = null;
        }

        private void Image2FadeOut_Completed(object sender, EventArgs e)
        {
            Image2.Source = null;
        }

        private void BorderDarkenOut_Completed(object sender, EventArgs e)
        {
            BorderDarken.Opacity = 0;
        }

        private static BitmapSource LoadImage(object source, int blurredCopyRadius)
        {
            if (blurredCopyRadius > 0)
            {
                return GetBlurredCopy((source as BitmapLoadProperties)?.Source ?? source as string, blurredCopyRadius);
            }
            else if (source is string str)
            {
                return ImageSourceManager.GetImage(str, false);
            }
            else if (source is BitmapLoadProperties props)
            {
                return ImageSourceManager.GetImage(props.Source, false, props);
            }
            else
            {
                return null;
            }
        }

        private static BitmapSource GetBlurredCopy(string path, int radius)
        {
            if (path == null)
            {
                return null;
            }

            var key = $"{radius}|{path}";
            lock (blurredCopies)
            {
                if (blurredCopies.TryGetValue(key, out var known))
                {
                    return known;
                }
            }

            return MakeBlurredCopy(path, radius, key);
        }

        /// <summary>Lets go of the blurred copies: they are made again the next time a picture needs one.</summary>
        public static int ClearBlurredCopies()
        {
            lock (blurredCopies)
            {
                var count = blurredCopies.Count;
                blurredCopies.Clear();
                return count;
            }
        }

        private static BitmapSource MakeBlurredCopy(string path, int radius, string key)
        {
            var copy =ImageSourceManager.GetImage(path, false, new BitmapLoadProperties(BlurredCopyWidth, 0))?.ToBlurred(radius);
            if (copy != null)
            {
                lock (blurredCopies)
                {
                    if (blurredCopies.Count >= BlurredCopiesLimit)
                    {
                        blurredCopies.Clear();
                    }

                    blurredCopies[key] = copy;
                }
            }

            return copy;
        }

        private static void BlurSettingChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            var control = (FadeImage)obj;
            if (control.Source == null)
            {
                return;
            }

            if (control.UseBlurredCopy)
            {
                control.ImageHolder.Effect = null;
                control.currentSource = null;
                control.LoadNewSource();
                return;
            }

            var blurAmount = control.BlurAmount;
            var blurEnabled = control.IsBlurEnabled;
            var highQuality = control.HighQualityBlur;
            if (blurEnabled)
            {
                control.ImageHolder.Effect = new BlurEffect()
                {
                    KernelType = KernelType.Gaussian,
                    Radius = blurAmount,
                    RenderingBias = highQuality ? RenderingBias.Quality : RenderingBias.Performance
                };
            }
            else
            {
                control.ImageHolder.Effect = null;
            }
        }

        private static void SourceChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            var control = (FadeImage)obj;
            control.newSource = args.NewValue;
            var now = DateTime.UtcNow;
            var afterPause = control.DelayOnlyRuns && (now - control.lastSourceChange).TotalMilliseconds > control.SourceUpdateDelay * 2;
            control.lastSourceChange = now;

            if (control.SourceUpdateDelay > 0 && !afterPause)
            {
                control.sourceChangeTimer.Enabled = false;
                control.sourceChangeTimer.Interval = control.SourceUpdateDelay;
                control.sourceChangeTimer.Enabled = true;
            }
            else
            {
                control.LoadNewSource();
            }
        }

        private async void LoadNewSource()
        {
            sourceChangeTimer.Enabled = false;
            if (newSource?.Equals(currentSource) == true)
            {
                return;
            }

            var blurAmount = BlurAmount;
            var blurEnabled = IsBlurEnabled;
            var highQuality = HighQualityBlur;
            var blurredCopyRadius = blurEnabled && UseBlurredCopy ? Math.Max(1, blurAmount / BlurAmountPerCopyRadius) : 0;
            var requested = newSource;
            BitmapSource image = null;
            currentSource = requested;
            if (requested != null)
            {
                image = await Task.Factory.StartNew(() => LoadImage(requested, blurredCopyRadius));

                // The picture of a game left behind can't take the place of the one of the next game.
                if (!ReferenceEquals(requested, currentSource))
                {
                    return;
                }
            }

            if (blurEnabled && blurredCopyRadius == 0)
            {
                if (ImageHolder.Effect == null)
                {
                    ImageHolder.Effect = new BlurEffect()
                    {
                        KernelType = KernelType.Gaussian,
                        Radius = blurAmount,
                        RenderingBias = highQuality ? RenderingBias.Quality : RenderingBias.Performance
                    };
                }
            }
            else
            {
                if (ImageHolder.Effect != null)
                {
                    ImageHolder.Effect = null;
                }
            }

            if (AnimationEnabled)
            {
                if (image == null)
                {
                    if (currentImage == CurrentImage.None)
                    {
                        return;
                    }

                    // Both: in the middle of a change the old picture is still there under the new one.
                    Image1FadeOut.Begin();
                    Image2FadeOut.Begin();
                    BorderDarkenFadeOut.Begin();
                    currentImage = CurrentImage.None;
                }
                else
                {
                    if (currentImage == CurrentImage.None)
                    {
                        Image1FadeOut.Stop();
                        Image1.Source = image;
                        Image1FadeIn.Begin();
                        BorderDarken.Opacity = 1;
                        BorderDarkenFadeOut.Stop();
                        currentImage = CurrentImage.Image1;
                    }
                    // Only the image on top moves, the other one stays whole under it. Fading both at
                    // the same time lets what is behind them show through in the middle of the change.
                    else if (currentImage == CurrentImage.Image1)
                    {
                        Image2FadeOut.Stop();
                        Image2.Source = image;
                        Image2FadeIn.Begin();
                        BorderDarken.Opacity = 1;
                        BorderDarkenFadeOut.Stop();
                        currentImage = CurrentImage.Image2;
                    }
                    else if (currentImage == CurrentImage.Image2)
                    {
                        Image1FadeOut.Stop();
                        Image1FadeIn.Stop();
                        Image1.Source = image;
                        Image2FadeOut.Begin();
                        BorderDarken.Opacity = 1;
                        BorderDarkenFadeOut.Stop();
                        currentImage = CurrentImage.Image1;
                    }
                }
            }
            else
            {
                if (currentImage == CurrentImage.Image1)
                {
                    Image1.Source = image;
                }
                else if (currentImage == CurrentImage.Image2)
                {
                    Image2.Source = image;
                }
                else
                {
                    Image1.Source = image;
                    currentImage = CurrentImage.Image1;
                }
            }
        }
    }
}
