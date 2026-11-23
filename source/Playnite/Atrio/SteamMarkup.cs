using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    /// <summary>
    /// The posts of Steam have their own markup, like [b] or [list]. Here it turns into HTML for the reader, or into plain
    /// words for the card. Only the tags Steam knows are tags: "[PC]" or "[1-8]" are words of the post and stay.
    /// </summary>
    public static class SteamMarkup
    {
        private const string ClanImages = "https://clan.akamai.steamstatic.com/images/";
        private const string AppImages = "https://steamcdn-a.akamaihd.net/steam/apps/";

        private static readonly Regex tokenRegex = new Regex(
            @"\\(?<literal>[\[\]])|\[(?<close>/?)(?:(?<name>[pbiu]|(?-i:[sc]))(?<attributes>=[^\]]*|\s+[a-z]+\s*=[^\]]*)?|(?<name>h[1-6]|strike|spoiler|list|olist|\*|url|img|previewyoutube|video|dynamiclink|table|tr|th|td|hr|quote|code|expand|carousel|noparse|center|pullquote|color|size)(?<attributes>(?:=|\s)[^\]]*)?)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex attributeRegex = new Regex(@"(?<name>[a-zA-Z_-]+)\s*=\s*(?:""(?<value>[^""]*)""|(?<value>[^\s""\]]+))", RegexOptions.Compiled);
        private static readonly Regex spacesRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex htmlTagRegex = new Regex(@"<(?:br|/p|/li|/h[1-6]|/tr|/div)\b[^>]*>|<[^>]+>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> blocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "h1", "h2", "h3", "h4", "h5", "h6", "p", "list", "olist", "table", "tr", "th", "td", "quote", "pullquote", "carousel", "expand", "hr", "*"
        };

        private static readonly Dictionary<string, string> simple = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "b", "b" }, { "i", "i" }, { "u", "u" }, { "s", "s" }, { "strike", "s" }, { "c", "code" },
            { "h1", "h1" }, { "h2", "h2" }, { "h3", "h3" }, { "h4", "h4" }, { "h5", "h5" }, { "h6", "h6" },
            { "p", "p" }, { "tr", "tr" }, { "th", "th" }, { "td", "td" }, { "quote", "blockquote" }, { "pullquote", "blockquote" }, { "center", "center" }
        };

        private static string Encode(string text)
        {
            return WebUtility.HtmlEncode(text);
        }

        private static string Attribute(string attributes, string name)
        {
            foreach (Match match in attributeRegex.Matches(attributes ?? string.Empty))
            {
                if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["value"].Value.Trim();
                }
            }

            return null;
        }

        /// <summary>What goes right after the name: [url=https://...], with or without quotes.</summary>
        private static string MainAttribute(string attributes)
        {
            var text = (attributes ?? string.Empty).Trim();
            if (!text.StartsWith("=", StringComparison.Ordinal))
            {
                return null;
            }

            text = text.Substring(1).Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = text.IndexOf('"', 1);
                return (end > 0 ? text.Substring(1, end - 1) : text.Substring(1)).Trim();
            }

            var space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        /// <summary>An address that a page may lead to: nothing that isn't the web.</summary>
        public static string SafeUrl(string url)
        {
            var text = (url ?? string.Empty).Trim();
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                text = "https:" + text;
            }

            if (text.Length > 0 && text.IndexOf("://", StringComparison.Ordinal) < 0 && text.IndexOf(':') < 0 && text.IndexOf('.') > 0)
            {
                text = "https://" + text;
            }

            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.AbsoluteUri : null;
        }

        /// <summary>The address of a picture, with the places Steam writes as {STEAM_CLAN_IMAGE}.</summary>
        public static string ImageUrl(string source)
        {
            var text = (source ?? string.Empty).Trim().
                Replace("{STEAM_CLAN_IMAGE}/", ClanImages).
                Replace("{STEAM_CLAN_LOC_IMAGE}/", ClanImages).
                Replace("{STEAM_APP_IMAGE}/", AppImages);
            return SafeUrl(text);
        }

        /// <summary>The last part of an address, as words: for the links that come without a text.</summary>
        private static string LinkName(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return url;
            }

            var last = uri.Segments.Select(a => a.Trim('/')).LastOrDefault(a => a.Length > 0 && !a.All(char.IsDigit));
            return uri.Host == "store.steampowered.com" && uri.AbsolutePath.StartsWith("/app/", StringComparison.Ordinal) && last != null && last != "app" ?
                Uri.UnescapeDataString(last).Replace('_', ' ') :
                uri.Host.Replace("www.", string.Empty) + (uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : string.Empty);
        }

        private sealed class Writer
        {
            private readonly StringBuilder html = new StringBuilder();
            private readonly Stack<string> open = new Stack<string>();
            private readonly string videoText;
            private bool atBlock = true;

            public Writer(string videoText)
            {
                this.videoText = videoText;
            }

            private void OpenTag(string name, string tag)
            {
                open.Push(name);
                html.Append(tag);
            }

            private static string Closing(string name)
            {
                switch (name)
                {
                    case "list":
                        return "</ul>";
                    case "olist":
                        return "</ol>";
                    case "*":
                        return "</li>";
                    case "url":
                        return "</a>";
                    case "table":
                        return "</table>";
                    case "spoiler":
                        return "</span>";
                    case "code":
                        return "</pre>";
                    case "expand":
                        return "</div>";
                    default:
                        return simple.TryGetValue(name, out var tag) ? $"</{tag}>" : string.Empty;
                }
            }

            /// <summary>The new lines right before or after a block only separate it, which it already does by itself.</summary>
            private void TrimBreaks()
            {
                const string lineBreak = "<br>";
                while (html.Length >= lineBreak.Length && html.ToString(html.Length - lineBreak.Length, lineBreak.Length) == lineBreak)
                {
                    html.Length -= lineBreak.Length;
                }
            }

            /// <summary>Closes up to the tag given, with what was left open inside of it. Nothing if it wasn't open.</summary>
            public void Close(string name)
            {
                if (!open.Contains(name))
                {
                    return;
                }

                if (blocks.Contains(name))
                {
                    TrimBreaks();
                }

                string closing;
                do
                {
                    closing = open.Pop();
                    html.Append(Closing(closing));
                }
                while (closing != name);

                atBlock = blocks.Contains(name);
            }

            private bool IsInside(string name, string limit)
            {
                foreach (var item in open)
                {
                    if (item == name)
                    {
                        return true;
                    }

                    if (item == limit || (limit == "lists" && (item == "list" || item == "olist")))
                    {
                        return false;
                    }
                }

                return false;
            }

            public void Text(string text)
            {
                if (text.Length == 0)
                {
                    return;
                }

                var lines = text.Replace("\r", string.Empty).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0 && !atBlock)
                    {
                        html.Append("<br>");
                    }

                    var line = lines[i];
                    if (line.Trim().Length > 0)
                    {
                        html.Append(Encode(line));
                        atBlock = false;
                    }
                    else if (line.Length > 0 && !atBlock && i == 0 && lines.Length == 1)
                    {
                        html.Append(' ');
                    }
                }
            }

            public void Open(string name, string attributes, string inside)
            {
                if (blocks.Contains(name))
                {
                    TrimBreaks();
                }

                switch (name)
                {
                    case "list":
                    case "olist":
                        OpenTag(name, name == "list" ? "<ul>" : "<ol>");
                        break;
                    case "*":
                        if (IsInside("*", "lists"))
                        {
                            Close("*");
                        }

                        // An item without its list still is an item.
                        if (!open.Contains("list") && !open.Contains("olist"))
                        {
                            OpenTag("list", "<ul>");
                        }

                        OpenTag("*", "<li>");
                        break;
                    case "url":
                        var url = SafeUrl(MainAttribute(attributes) ?? Attribute(attributes, "href") ?? inside);
                        if (url != null)
                        {
                            OpenTag("url", $"<a href=\"{Encode(url)}\">");
                        }

                        break;
                    case "table":
                        OpenTag("table", "<table class=\"notes\">");
                        break;
                    case "spoiler":
                        OpenTag("spoiler", "<span class=\"spoiler\">");
                        break;
                    case "code":
                        OpenTag("code", "<pre>");
                        break;
                    case "expand":
                        OpenTag("expand", "<div class=\"expand\">");
                        break;
                    case "hr":
                        html.Append("<hr>");
                        break;
                    default:
                        if (simple.TryGetValue(name, out var tag))
                        {
                            OpenTag(name, $"<{tag}>");
                        }

                        break;
                }

                atBlock = blocks.Contains(name) || (atBlock && name != "url");
            }

            public void Image(string source)
            {
                var url = ImageUrl(source);
                if (url != null)
                {
                    html.Append($"<img src=\"{Encode(url)}\">");
                    atBlock = false;
                }
            }

            public void Link(string url, string text)
            {
                var safe = SafeUrl(url);
                if (safe != null)
                {
                    html.Append($"<a href=\"{Encode(safe)}\">{Encode(text ?? LinkName(safe))}</a>");
                    atBlock = false;
                }
            }

            public void Video(string url, string picture)
            {
                var safe = SafeUrl(url);
                if (safe == null)
                {
                    return;
                }

                html.Append($"<div class=\"video\"><a href=\"{Encode(safe)}\">");
                var image = ImageUrl(picture);
                if (image != null)
                {
                    html.Append($"<img src=\"{Encode(image)}\"><br>");
                }

                html.Append($"&#9654; {Encode(videoText)}</a></div>");
                atBlock = true;
            }

            public string Finish()
            {
                while (open.Count > 0)
                {
                    html.Append(Closing(open.Pop()));
                }

                return html.ToString();
            }
        }

        /// <summary>The whole post as HTML, without scripts or anything that isn't the text, its pictures and its links.</summary>
        public static string ToHtml(string body, string videoText = "")
        {
            var text = body ?? string.Empty;
            var writer = new Writer(videoText);
            var position = 0;
            for (var match = tokenRegex.Match(text); match.Success; match = tokenRegex.Match(text, position))
            {
                writer.Text(text.Substring(position, match.Index - position));
                position = match.Index + match.Length;
                if (match.Groups["literal"].Success)
                {
                    writer.Text(match.Groups["literal"].Value);
                    continue;
                }

                var name = match.Groups["name"].Value.ToLowerInvariant();
                var attributes = match.Groups["attributes"].Value;
                if (match.Groups["close"].Length > 0)
                {
                    writer.Close(name);
                    continue;
                }

                // The ones whose content is an address, or that are only words.
                switch (name)
                {
                    case "img":
                        position = ReadInside(text, position, name, out var source);
                        writer.Image(Attribute(attributes, "src") ?? MainAttribute(attributes) ?? source);
                        break;
                    case "previewyoutube":
                        position = ReadInside(text, position, name, out _);
                        var video = (MainAttribute(attributes) ?? string.Empty).Split(';')[0];
                        if (Regex.IsMatch(video, "^[A-Za-z0-9_-]{6,20}$"))
                        {
                            writer.Video($"https://www.youtube.com/watch?v={video}", $"https://i.ytimg.com/vi/{video}/hqdefault.jpg");
                        }

                        break;
                    case "video":
                        position = ReadInside(text, position, name, out _);
                        writer.Video(Attribute(attributes, "mp4") ?? Attribute(attributes, "webm"), Attribute(attributes, "poster"));
                        break;
                    case "dynamiclink":
                        position = ReadInside(text, position, name, out var linkText);
                        writer.Link(Attribute(attributes, "href"), linkText.Trim().Length > 0 ? linkText.Trim() : null);
                        break;
                    case "noparse":
                        position = ReadInside(text, position, name, out var literal);
                        writer.Text(literal);
                        break;
                    case "url":
                        writer.Open(name, attributes, PeekInside(text, position, name));
                        break;
                    default:
                        writer.Open(name, attributes, null);
                        break;
                }
            }

            writer.Text(text.Substring(position));
            return writer.Finish();
        }

        private static string PeekInside(string text, int position, string name)
        {
            ReadInside(text, position, name, out var inside);
            return inside;
        }

        /// <summary>What is between a tag and the one that closes it, and where it ends. A tag that is never closed has nothing inside.</summary>
        private static int ReadInside(string text, int position, string name, out string inside)
        {
            var end = text.IndexOf($"[/{name}]", position, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                inside = string.Empty;
                return position;
            }

            inside = text.Substring(position, end - position);
            return end + name.Length + 3;
        }

        /// <summary>The post in plain words, in one line: without its pictures, its videos and its tags.</summary>
        public static string ToText(string body)
        {
            var text = body ?? string.Empty;
            var result = new StringBuilder();
            var position = 0;
            for (var match = tokenRegex.Match(text); match.Success; match = tokenRegex.Match(text, position))
            {
                result.Append(text, position, match.Index - position);
                position = match.Index + match.Length;
                if (match.Groups["literal"].Success)
                {
                    result.Append(match.Groups["literal"].Value);
                    continue;
                }

                var name = match.Groups["name"].Value.ToLowerInvariant();
                if (match.Groups["close"].Length == 0)
                {
                    switch (name)
                    {
                        case "img":
                        case "previewyoutube":
                        case "video":
                        case "dynamiclink":
                            position = ReadInside(text, position, name, out _);
                            result.Append(' ');
                            break;
                        case "spoiler":
                            position = ReadInside(text, position, name, out _);
                            result.Append("…");
                            break;
                    }
                }

                if (blocks.Contains(name))
                {
                    result.Append(' ');
                }
            }

            result.Append(text, position, text.Length - position);
            return spacesRegex.Replace(result.ToString(), " ").Trim();
        }

        /// <summary>The start of a post in plain words, cut at the end of a word.</summary>
        public static string GetExcerpt(string body, int length = 240)
        {
            return Cut(ToText(body), length);
        }

        public static string Cut(string text, int length)
        {
            if (text == null || text.Length <= length)
            {
                return text;
            }

            var cut = text.LastIndexOf(' ', length);
            return text.Substring(0, cut > length / 2 ? cut : length).TrimEnd(' ', ',', '.', ';', ':') + "…";
        }

        /// <summary>The first picture of a post, null without any.</summary>
        public static string GetFirstImage(string body)
        {
            var text = body ?? string.Empty;
            for (var match = tokenRegex.Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Groups["close"].Length == 0 && match.Groups["name"].Value.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    var attributes = match.Groups["attributes"].Value;
                    var url = ImageUrl(Attribute(attributes, "src") ?? MainAttribute(attributes) ?? PeekInside(text, match.Index + match.Length, "img"));
                    if (url != null)
                    {
                        return url;
                    }
                }
            }

            return null;
        }

        /// <summary>The HTML of another store in plain words, in one line.</summary>
        public static string HtmlToText(string html)
        {
            var text = htmlTagRegex.Replace(html ?? string.Empty, " ");
            return spacesRegex.Replace(WebUtility.HtmlDecode(text), " ").Trim();
        }
    }
}
