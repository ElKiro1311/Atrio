using NUnit.Framework;
using Playnite.Converters;
using System;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Tests
{
    [TestFixture]
    public class HeroLayoutTests
    {
        private static double SeenShare(HeroLayout layout)
        {
            return layout.Height / layout.PictureHeight;
        }

        [TestCase(1545, 847)]
        [TestCase(1597, 1028)]
        public void OrdinaryWindowKeepsTheWholeWidthTest(double width, double viewHeight)
        {
            var layout = new HeroLayout(width, viewHeight);
            Assert.AreEqual(430, layout.Height);
            Assert.AreEqual(width, layout.PictureWidth);
            Assert.AreEqual(width / HeroLayout.PictureShape, layout.PictureHeight, 0.001);
            Assert.AreEqual(-(layout.PictureHeight - 430) * 0.58, layout.PictureMargin.Top, 0.001);
            Assert.IsNull(layout.PictureMask);
        }

        [Test]
        public void SmallWindowShowsTheWholePictureTest()
        {
            var layout = new HeroLayout(960, 748);
            Assert.AreEqual(960 / HeroLayout.PictureShape, layout.Height, 0.001);
            Assert.AreEqual(layout.Height, layout.PictureHeight, 0.001);
            Assert.AreEqual(0, layout.PictureMargin.Top, 0.001);
            Assert.IsNull(layout.PictureMask);
        }

        [Test]
        public void TinyWindowStillHasRoomForTheLogoTest()
        {
            var layout = new HeroLayout(500, 400);
            Assert.AreEqual(230, layout.Height);
            Assert.AreEqual(230, layout.PictureHeight);
            Assert.AreEqual(500, layout.PictureWidth);
        }

        [TestCase(3118, 1338)]
        [TestCase(3120, 1388)]
        [TestCase(4800, 1388)]
        [TestCase(2240, 1388)]
        public void WideWindowSeesTheSameBandAsAnOrdinaryOneTest(double width, double viewHeight)
        {
            var layout = new HeroLayout(width, viewHeight);
            var ordinary = new HeroLayout(1545, 847);
            Assert.Less(layout.PictureWidth, width);
            Assert.AreEqual(SeenShare(ordinary), SeenShare(layout), 0.01);
            Assert.AreEqual(ordinary.PictureMargin.Top / ordinary.PictureHeight,
                layout.PictureMargin.Top / layout.PictureHeight, 0.01);
            Assert.IsNotNull(layout.PictureMask);
        }

        [Test]
        public void ShortWideWindowDoesNotPushThePageDownTest()
        {
            var layout = new HeroLayout(2240, 1028);
            Assert.AreEqual(430, layout.Height);
            Assert.AreEqual(1600, layout.PictureWidth);
            Assert.IsNotNull(layout.PictureMask);
        }

        [TestCase(3120, 2108)]
        [TestCase(3520, 2108)]
        public void HugeWindowStopsGrowingTest(double width, double viewHeight)
        {
            var layout = new HeroLayout(width, viewHeight);
            Assert.AreEqual(640, layout.Height);
            Assert.Less(layout.PictureWidth, width);
        }

        [Test]
        public void GrowsWithoutJumpsTest()
        {
            var previous = new HeroLayout(800, 1388);
            for (var width = 801; width < 5000; width++)
            {
                var layout = new HeroLayout(width, 1388);
                Assert.AreEqual(previous.Height, layout.Height, 1, width.ToString());
                Assert.AreEqual(previous.PictureWidth, layout.PictureWidth, 4, width.ToString());
                Assert.GreaterOrEqual(layout.Height, previous.Height - 0.001, width.ToString());
                Assert.LessOrEqual(layout.PictureWidth, width);
                Assert.GreaterOrEqual(layout.PictureHeight, layout.Height);
                previous = layout;
            }
        }

        [Test]
        public void MaskFadesBothSidesAndKeepsTheMiddleTest()
        {
            var mask = (LinearGradientBrush)new HeroLayout(3118, 1338).PictureMask;
            var stops = mask.GradientStops.OrderBy(a => a.Offset).ToList();
            Assert.AreEqual(0, stops.First().Offset, 0.001);
            Assert.AreEqual(1, stops.Last().Offset, 0.001);
            Assert.AreEqual(0, stops.First().Color.A);
            Assert.AreEqual(0, stops.Last().Color.A);
            Assert.IsTrue(stops.Where(a => a.Offset > 0.16 && a.Offset < 0.84).All(a => a.Color.A == 255));
            Assert.IsTrue(stops.Any(a => a.Color.A == 255));
        }

        [Test]
        public void AlmostFittingWindowHardlyFadesTest()
        {
            var mask = (LinearGradientBrush)new HeroLayout(1610, 1028).PictureMask;
            var fade = mask.GradientStops.Where(a => a.Color.A == 255).Min(a => a.Offset);
            Assert.AreEqual(10.0 / 1600, fade, 0.001);
        }

        [Test]
        public void BackdropIsOnlyLoadedWhenItIsSeenTest()
        {
            var converter = new HeroBackdropConverter();
            var wide = new HeroLayout(3118, 1338);
            var ordinary = new HeroLayout(1545, 847);
            Assert.AreEqual("art", converter.Convert(new object[] { false, "art", "background", wide }, null, null, null));
            Assert.AreEqual("background", converter.Convert(new object[] { false, null, "background", wide }, null, null, null));
            Assert.IsNull(converter.Convert(new object[] { false, null, null, wide }, null, null, null));
            Assert.IsNull(converter.Convert(new object[] { false, "art", "background", ordinary }, null, null, null));
            Assert.IsNull(converter.Convert(new object[] { false, "art", "background", null }, null, null, null));
        }

        [Test]
        public void BackdropWaitsForTheArtOnItsWayTest()
        {
            var values = new object[] { true, null, "background", new HeroLayout(3118, 1338) };
            Assert.AreEqual(System.Windows.Data.Binding.DoNothing, new HeroBackdropConverter().Convert(values, null, null, null));
        }

        [Test]
        public void PictureWaitsWhileItsOnItsWayTest()
        {
            var converter = new HeroPictureConverter();
            Assert.AreEqual(System.Windows.Data.Binding.DoNothing, converter.Convert(new object[] { true, null }, null, null, null));
            Assert.IsNull(converter.Convert(new object[] { false, null }, null, null, null));
            Assert.AreEqual("art.jpg", converter.Convert(new object[] { false, "art.jpg" }, null, null, null));
        }

        private static BitmapSource CreatePicture(int width, int height, Func<int, byte> value)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var level = value(i / 4 % width);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = level;
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        }

        private static byte[] GetRow(BitmapSource image, int y)
        {
            var row = new byte[image.PixelWidth * 4];
            image.CopyPixels(new System.Windows.Int32Rect(0, y, image.PixelWidth, 1), row, row.Length, 0);
            return row;
        }

        [Test]
        public void BlurKeepsTheSizeAndAFlatColorTest()
        {
            var blurred = CreatePicture(40, 12, _ => 120).ToBlurred(4);
            Assert.AreEqual(40, blurred.PixelWidth);
            Assert.AreEqual(12, blurred.PixelHeight);
            Assert.IsTrue(blurred.IsFrozen);
            foreach (var y in new[] { 0, 6, 11 })
            {
                var row = GetRow(blurred, y);
                Assert.IsTrue(Enumerable.Range(0, 40).All(x => row[x * 4] == 120));
            }
        }

        [Test]
        public void BlurSoftensAnEdgeAndLeavesTheFarSidesTest()
        {
            var blurred = CreatePicture(80, 10, x => (byte)(x < 40 ? 0 : 240)).ToBlurred(3);
            var row = GetRow(blurred, 5);
            Assert.AreEqual(0, row[0]);
            Assert.AreEqual(240, row[79 * 4]);
            Assert.Greater(row[39 * 4], 90);
            Assert.Less(row[39 * 4], 150);
            for (var x = 1; x < 80; x++)
            {
                Assert.GreaterOrEqual(row[x * 4], row[(x - 1) * 4]);
            }
        }

        [Test]
        public void BlurHandlesAPictureSmallerThanTheRadiusTest()
        {
            var blurred = CreatePicture(3, 2, x => (byte)(x * 60)).ToBlurred(10);
            Assert.AreEqual(3, blurred.PixelWidth);
            Assert.AreEqual(2, blurred.PixelHeight);
        }
    }
}
