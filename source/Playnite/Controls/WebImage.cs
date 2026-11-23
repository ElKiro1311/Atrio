using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Playnite.Controls
{
    /// <summary>
    /// The picture of an address of the web inside an Image: from memory, from the copy on the disk or from the web, and
    /// never making the window wait. Themes use it as WebImage.DecodeWidth="480" WebImage.Url="{Binding ImageUrl}". A list
    /// that is made again shows its pictures at once, instead of downloading them one more time.
    /// </summary>
    public static class WebImage
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = CreateClient();
        private static readonly SemaphoreSlim downloads = new SemaphoreSlim(6, 6);
        private static readonly ConcurrentDictionary<string, Task<BitmapSource>> running = new ConcurrentDictionary<string, Task<BitmapSource>>();
        private static readonly ConcurrentDictionary<string, DateTime> failed = new ConcurrentDictionary<string, DateTime>();
        private static readonly object memoryLock = new object();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> memory =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>>();
        private static readonly LinkedList<KeyValuePair<string, BitmapSource>> order = new LinkedList<KeyValuePair<string, BitmapSource>>();
        private static readonly TimeSpan failureLifetime = TimeSpan.FromMinutes(5);
        private static int trimmed;
        private static long memorySize;

        private const long MemoryLimit = 24L * 1024 * 1024;
        private const int MaxBytes = 16 * 1024 * 1024;
        private const long DiskLimit = 400L * 1024 * 1024;
        private const int DiskDays = 60;

        /// <summary>Tests point it somewhere else.</summary>
        public static string TestFolder { get; set; }

        private static string Folder => TestFolder ?? Path.Combine(PlaynitePaths.DataCachePath, "web-images");

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false };
            var result = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
            result.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Atrio");
            return result;
        }

        public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
            "DecodeWidth", typeof(int), typeof(WebImage), new PropertyMetadata(0));

        /// <summary>How many pixels are enough: a bigger picture is read smaller.</summary>
        public static int GetDecodeWidth(DependencyObject element)
        {
            return (int)element.GetValue(DecodeWidthProperty);
        }

        public static void SetDecodeWidth(DependencyObject element, int value)
        {
            element.SetValue(DecodeWidthProperty, value);
        }

        public static readonly DependencyProperty FallbackProperty = DependencyProperty.RegisterAttached(
            "Fallback", typeof(string), typeof(WebImage), new PropertyMetadata(null));

        /// <summary>The address to use when the first one doesn't answer.</summary>
        public static string GetFallback(DependencyObject element)
        {
            return (string)element.GetValue(FallbackProperty);
        }

        public static void SetFallback(DependencyObject element, string value)
        {
            element.SetValue(FallbackProperty, value);
        }

        private static readonly DependencyProperty IsHiddenProperty = DependencyProperty.RegisterAttached(
            "IsHidden", typeof(bool), typeof(WebImage), new PropertyMetadata(false));

        public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
            "Url", typeof(string), typeof(WebImage), new PropertyMetadata(null, OnUrlChanged));

        public static string GetUrl(DependencyObject element)
        {
            return (string)element.GetValue(UrlProperty);
        }

        public static void SetUrl(DependencyObject element, string value)
        {
            element.SetValue(UrlProperty, value);
        }

        private static void OnUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is Image image))
            {
                return;
            }

            var url = e.NewValue as string;
            if (!IsWebAddress(url))
            {
                image.Source = null;
                return;
            }

            if ((bool)image.GetValue(IsHiddenProperty))
            {
                // The one before couldn't be had; this one may.
                image.SetValue(IsHiddenProperty, false);
                image.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            }

            var known = FromMemory(GetKey(url, GetDecodeWidth(image)));
            if (known != null)
            {
                image.Source = known;
            }
            else
            {
                image.Source = null;
                Load(image, url);
            }
        }

        private static async void Load(Image image, string url)
        {
            try
            {
                // The rest of the properties of the element may still be on their way.
                await image.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                if (GetUrl(image) != url)
                {
                    return;
                }

                var width = GetDecodeWidth(image);
                var picture = await GetAsync(url, width);
                var fallback = GetFallback(image);
                if (picture == null && fallback != url && IsWebAddress(fallback) && GetUrl(image) == url)
                {
                    picture = await GetAsync(fallback, width);
                }

                if (GetUrl(image) != url)
                {
                    return;
                }

                if (picture == null)
                {
                    // An empty frame says nothing: the text takes its place.
                    image.SetValue(IsHiddenProperty, true);
                    image.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                    return;
                }

                image.Source = picture;
                image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                {
                    From = 0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    FillBehavior = FillBehavior.Stop
                });
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // It's a picture: without it, everything else is still there.
                logger.Debug(e, $"Failed to show the picture {url}.");
            }
        }

        private static bool IsWebAddress(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string GetKey(string url, int width)
        {
            return $"{width}|{url}";
        }

        private static BitmapSource FromMemory(string key)
        {
            lock (memoryLock)
            {
                if (!memory.TryGetValue(key, out var node))
                {
                    return null;
                }

                order.Remove(node);
                order.AddFirst(node);
                return node.Value.Value;
            }
        }

        private static void ToMemory(string key, BitmapSource picture)
        {
            lock (memoryLock)
            {
                if (memory.TryGetValue(key, out var old))
                {
                    order.Remove(old);
                    memorySize -= GetSize(old.Value.Value);
                }

                memory[key] = order.AddFirst(new KeyValuePair<string, BitmapSource>(key, picture));
                memorySize += GetSize(picture);
                while (memorySize > MemoryLimit && order.Count > 1)
                {
                    memorySize -= GetSize(order.Last.Value.Value);
                    memory.Remove(order.Last.Value.Key);
                    order.RemoveLast();
                }
            }
        }

        private static long GetSize(BitmapSource picture)
        {
            return picture.PixelWidth * (long)picture.PixelHeight * 4;
        }

        /// <summary>The pictures kept in memory go away; the ones on the disk stay.</summary>
        public static void ForgetMemory()
        {
            lock (memoryLock)
            {
                memory.Clear();
                order.Clear();
                memorySize = 0;
            }

            failed.Clear();
        }

        /// <summary>The picture, or null when it can't be had. Two that ask for the same one at once share the answer.</summary>
        public static async Task<BitmapSource> GetAsync(string url, int width)
        {
            var key = GetKey(url, width);
            var known = FromMemory(key);
            if (known != null || (failed.TryGetValue(url, out var when) && DateTime.UtcNow - when < failureLifetime))
            {
                return known;
            }

            var task = running.GetOrAdd(key, _ => Task.Run(() => ReadAsync(url, width)));
            try
            {
                var picture = await task.ConfigureAwait(false);
                if (picture != null)
                {
                    ToMemory(key, picture);
                }
                else
                {
                    failed[url] = DateTime.UtcNow;
                }

                return picture;
            }
            finally
            {
                running.TryRemove(key, out _);
            }
        }

        private static async Task<BitmapSource> ReadAsync(string url, int width)
        {
            TrimOnce();
            var path = GetPath(url);
            var data = ReadFile(path);
            if (data != null)
            {
                var saved = Decode(data, width);
                if (saved != null)
                {
                    return saved;
                }
            }

            data = await DownloadAsync(url).ConfigureAwait(false);
            var picture = data == null ? null : Decode(data, width);
            if (picture != null)
            {
                WriteFile(path, data);
            }

            return picture;
        }

        private static async Task<byte[]> DownloadAsync(string url)
        {
            await downloads.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var response = await client.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.Debug($"The picture {url} answers {(int)response.StatusCode}.");
                        return null;
                    }

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException || e is IOException || e is WebException)
            {
                logger.Debug($"The picture {url} couldn't be downloaded: {e.Message}");
                return null;
            }
            finally
            {
                downloads.Release();
            }
        }

        /// <summary>The picture at the width it needs, ready for any thread. Null when Windows can't read it.</summary>
        public static BitmapSource Decode(byte[] data, int width)
        {
            try
            {
                int real;
                using (var stream = new MemoryStream(data, false))
                {
                    real = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).PixelWidth;
                }

                using (var stream = new MemoryStream(data, false))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    if (width > 0 && real > width)
                    {
                        image.DecodePixelWidth = width;
                    }

                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception e)
            {
                // Not a picture, or of a kind this Windows doesn't read. One that can't be is said to not fit in memory.
                logger.Debug($"A picture can't be read: {e.Message}");
                return null;
            }
        }

        public static string GetPath(string url)
        {
            using (var sha = SHA1.Create())
            {
                var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", string.Empty).ToLowerInvariant();
                return Path.Combine(Folder, name.Substring(0, 2), name);
            }
        }

        private static byte[] ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                // The date says when it was last used: the ones nobody looks at are the first to go.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(2))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }

                return File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteFile(string path, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = $"{path}.{Guid.NewGuid():N}";
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temporary, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug($"A picture couldn't be kept: {e.Message}");
            }
        }

        private static void TrimOnce()
        {
            if (Interlocked.Exchange(ref trimmed, 1) == 0)
            {
                Task.Run(() => Trim(Folder, DiskLimit, DateTime.UtcNow.AddDays(-DiskDays)));
            }
        }

        /// <summary>The ones not used in a long time go away, and the oldest ones when they take too much.</summary>
        public static void Trim(string folder, long limit, DateTime oldest)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).OrderByDescending(a => a.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (total > limit || file.LastWriteTimeUtc < oldest)
                    {
                        FileSystem.DeleteFileSafe(file.FullName);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, "Failed to look through the pictures kept.");
            }
        }
    }
}
