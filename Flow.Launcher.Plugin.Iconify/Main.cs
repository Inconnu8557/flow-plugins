using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Flow.Launcher.Plugin;
using SkiaSharp;
using Svg.Skia;

namespace Flow.Launcher.Plugin.Iconify
{
    public class Iconify : IAsyncPlugin, IContextMenu
    {
        private PluginInitContext _context = null!;
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private const string ApiBase = "https://api.iconify.design";
        private const int SearchLimit = 48;

        // Local cache: Flow cannot render remote SVGs (LoadRemoteImageAsync -> BitmapImage),
        // and even locally SharpVectors loader has a centering bug (does not translate Bounds). See ImageLoader.cs:LoadSvgImage
        // So we convert SVGs to centered 256x256 PNGs via Svg.Skia/SkiaSharp and provide PNGs to Flow (native support).
        private static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLauncher", "IconifyCache");
        private static readonly SemaphoreSlim CacheSemaphore = new(8, 8);
        private const int PngSize = 256;

        static Iconify()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Flow.Launcher.Plugin.Iconify/1.0");
        }

        public void Init(PluginInitContext context)
        {
            _context = context;
            try { Directory.CreateDirectory(CacheDir); } catch { }
        }

        public Task InitAsync(PluginInitContext context)
        {
            _context = context;
            try { Directory.CreateDirectory(CacheDir); } catch { }
            return Task.CompletedTask;
        }

        public List<Result> Query(Query query)
        {
            return QueryAsync(query, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            var search = query.Search?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(search))
            {
                return await GetHelpResultsAsync(query);
            }

            if (search.Length < 2 && !search.Contains(":"))
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Type at least 2 characters to search",
                        SubTitle = "Ex: iconify home | iconify mdi:home | iconify arrow",
                        IcoPath = "Images\\icon.png",
                        Score = 100
                    }
                };
            }

            if (IsCollectionBrowseQuery(search, out var collectionPrefix))
            {
                return await GetCollectionResultsAsync(collectionPrefix!, query, token);
            }

            if (IsExactIconQuery(search))
            {
                var exact = await GetExactIconResultAsync(search, query, token);
                if (exact != null)
                {
                    var related = await SearchIconsAsync(search.Replace(":", " "), query, token, topScore: 50);
                    var list = new List<Result> { exact };
                    list.AddRange(related.Take(15));
                    return list;
                }
            }

            string? prefixFilter = null;
            string effectiveQuery = search;

            if (search.StartsWith("--prefix=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = search.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    prefixFilter = parts[0].Substring("--prefix=".Length).Trim();
                    effectiveQuery = parts.Length > 1 ? parts[1] : string.Empty;
                    if (string.IsNullOrWhiteSpace(effectiveQuery))
                    {
                        return new List<Result>
                        {
                            new Result
                            {
                                Title = $"Collection filter: {prefixFilter}",
                                SubTitle = "Add a search term after filter. Ex: --prefix=mdi home",
                                IcoPath = "Images\\icon.png"
                            }
                        };
                    }
                }
            }
            else if (search.Contains(' '))
            {
                var split = search.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length == 2 && split[0].Length is >= 2 and <= 24 && !split[0].Contains(':'))
                {
                    var popular = new HashSet<string> { "mdi", "mdi-light", "ph", "tabler", "carbon", "heroicons", "fa", "fa-solid", "fa-regular", "bi", "lucide", "solar", "uil", "ri", "icon-park", "material-symbols", "octicon", "codicon", "fluent" };
                    if (popular.Contains(split[0].ToLowerInvariant()))
                    {
                        prefixFilter = split[0];
                        effectiveQuery = split[1];
                    }
                }
            }

            return await SearchIconsAsync(effectiveQuery, query, token, prefixFilter);
        }

        #region Cache SVG local (fix preview)

        private static string GetCacheFilePath(string prefix, string name)
        {
            var safePrefix = string.Concat(prefix.Split(Path.GetInvalidFileNameChars()));
            var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(CacheDir, $"{safePrefix}_{safeName}.png");
        }

        private static bool TryConvertSvgToPng(string svgContent, string pngPath, int size = 256)
        {
            try
            {
                var svg = new SKSvg();
                // SKSvg 5.2.3 : Load from string via FromSvg, fallback via MemoryStream
                bool loaded = false;
                try
                {
                    // Tente FromSvg si disponible (reflection pour compat)
                    var m = typeof(SKSvg).GetMethod("FromSvg", new[] { typeof(string) });
                    if (m != null)
                    {
                        var res = m.Invoke(svg, new object[] { svgContent });
                        loaded = res is SKSvg s && s.Picture != null || svg.Picture != null;
                        if (!loaded) loaded = svg.Picture != null;
                    }
                    else
                    {
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svgContent));
                        svg.Load(ms);
                        loaded = svg.Picture != null;
                    }
                }
                catch
                {
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svgContent));
                    svg.Load(ms);
                    loaded = svg.Picture != null;
                }

                if (!loaded || svg.Picture == null)
                    return false;

                var bounds = svg.Picture.CullRect;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    bounds = new SKRect(0, 0, 24, 24);

                using var bitmap = new SKBitmap(size, size);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                float scale = Math.Min(size / bounds.Width, size / bounds.Height);
                // marge de 10% pour ne pas coller aux bords
                scale *= 0.85f;
                float dx = (size - bounds.Width * scale) / 2 - bounds.Left * scale;
                float dy = (size - bounds.Height * scale) / 2 - bounds.Top * scale;
                var matrix = SKMatrix.CreateScaleTranslation(scale, scale, dx, dy);
                canvas.DrawPicture(svg.Picture, in matrix);
                canvas.Flush();

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                using var fs = File.OpenWrite(pngPath);
                data.SaveTo(fs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> GetCachedIconPathAsync(string prefix, string name, CancellationToken token = default)
        {
            var file = GetCacheFilePath(prefix, name);
            if (File.Exists(file))
                return file;

            await CacheSemaphore.WaitAsync(token);
            try
            {
                if (File.Exists(file))
                    return file;

                Directory.CreateDirectory(CacheDir);
                var url = $"{ApiBase}/{prefix}/{name}.svg";
                using var resp = await Http.GetAsync(url, token);
                if (!resp.IsSuccessStatusCode)
                    return "Images\\icon.png";

                var svg = await resp.Content.ReadAsStringAsync(token);
                if (!svg.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                    return "Images\\icon.png";

                // Convertit en PNG centre (fix Flow LoadSvgImage qui coupe les icons desaxe)
                if (TryConvertSvgToPng(svg, file, PngSize))
                    return file;

                // fallback: save SVG as is (will be rendered via SharpVectors even if clipped, better than nothing)
                var svgFallback = Path.ChangeExtension(file, ".svg");
                await File.WriteAllTextAsync(svgFallback, svg, token);
                return svgFallback;
            }
            catch
            {
                return "Images\\icon.png";
            }
            finally
            {
                CacheSemaphore.Release();
            }
        }

        // Fire-and-forget version to preload without blocking
        private static void PreloadCacheAsync(string prefix, string name)
        {
            _ = Task.Run(async () =>
            {
                try { await GetCachedIconPathAsync(prefix, name); } catch { }
            });
        }

        #endregion

        #region Query helpers

        private async Task<List<Result>> GetHelpResultsAsync(Query query)
        {
            var mdiIcon = await GetCachedIconPathAsync("mdi", "home");
            return new List<Result>
            {
                new Result
                {
                    Title = "Search Iconify icons",
                    SubTitle = "Type a keyword: iconify home | iconify arrow | iconify mdi:home",
                    IcoPath = "Images\\icon.png",
                    Score = 100,
                    Action = _ => true
                },
                new Result
                {
                    Title = "Exemple: iconify home",
                    SubTitle = "Search all icons containing 'home' (~200 collections)",
                    IcoPath = "Images\\icon.png",
                    Score = 90,
                    Action = c =>
                    {
                        _context.API.ChangeQuery($"{query.ActionKeyword} home", true);
                        return false;
                    }
                },
                new Result
                {
                    Title = "Exemple: iconify mdi:home",
                    SubTitle = "Direct copy of exact icon SVG (Enter to copy)",
                    IcoPath = mdiIcon,
                    Score = 80,
                    Action = ctx => { _ = CopySvgAsync("mdi:home"); return false; },
                    ContextData = "mdi:home"
                },
                new Result
                {
                    Title = "Filter by collection: --prefix=mdi home",
                    SubTitle = "Limit search to a collection. Ex: --prefix=ph arrow",
                    IcoPath = "Images\\icon.png",
                    Score = 70,
                    Action = c =>
                    {
                        _context.API.ChangeQuery($"{query.ActionKeyword} --prefix=mdi home", true);
                        return false;
                    }
                },
                new Result
                {
                    Title = "Browse collection: iconify :mdi",
                    SubTitle = "Browse a collection. Type :mdi, :ph, :tabler...",
                    IcoPath = "Images\\icon.png",
                    Score = 60,
                    Action = c =>
                    {
                        _context.API.ChangeQuery($"{query.ActionKeyword} :mdi", true);
                        return false;
                    }
                },
                new Result
                {
                    Title = "Tip: Enter = copy SVG | Ctrl+C = copy name | Context menu = more options",
                    SubTitle = "SVG is copied to clipboard without leaving Flow Launcher",
                    IcoPath = "Images\\icon.png",
                    Score = 10
                }
            };
        }

        private static bool IsExactIconQuery(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return false;
            if (search.Contains(' ')) return false;
            if (!search.Contains(':')) return false;
            var parts = search.Split(':');
            if (parts.Length != 2) return false;
            if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return false;
            if (parts[0].Length < 2 || parts[0].Length > 32) return false;
            if (parts[1].Length < 1 || parts[1].Length > 64) return false;
            return parts.All(p => p.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ));
        }

        private static bool IsCollectionBrowseQuery(string search, out string? prefix)
        {
            prefix = null;
            var t = search.Trim();
            if (t.StartsWith(":") && t.Length > 1)
            {
                prefix = t.Substring(1).Trim().ToLowerInvariant();
                return !string.IsNullOrWhiteSpace(prefix);
            }
            if (t.StartsWith("collection:", StringComparison.OrdinalIgnoreCase))
            {
                prefix = t.Substring("collection:".Length).Trim().ToLowerInvariant();
                return !string.IsNullOrWhiteSpace(prefix);
            }
            if (t.StartsWith("list ", StringComparison.OrdinalIgnoreCase))
            {
                prefix = t.Substring(4).Trim().ToLowerInvariant();
                return !string.IsNullOrWhiteSpace(prefix);
            }
            if (t.EndsWith("/") && !t.Contains(' ') && !t.Contains(':'))
            {
                prefix = t.TrimEnd('/').ToLowerInvariant();
                return !string.IsNullOrWhiteSpace(prefix);
            }
            return false;
        }

        private async Task<Result?> GetExactIconResultAsync(string iconName, Query query, CancellationToken token)
        {
            var parts = iconName.Split(':');
            var prefix = parts[0];
            var name = parts[1];
            try
            {
                var url = $"{ApiBase}/{prefix}/{name}.svg";
                using var resp = await Http.GetAsync(url, token);
                if (!resp.IsSuccessStatusCode)
                    return null;

                var cached = await GetCachedIconPathAsync(prefix, name, token);
                return new Result
                {
                    Title = name,
                    SubTitle = $"{iconName} | {prefix} | Enter: copy SVG",
                    IcoPath = cached,
                    Score = 1000,
                    CopyText = iconName,
                    AutoCompleteText = $"{query.ActionKeyword} {iconName}",
                    ContextData = iconName,
                    Action = ctx => { _ = CopySvgAsync(iconName); return false; },
                    TitleHighlightData = _context.API.FuzzySearch(query.Search, name).MatchData
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<List<Result>> GetCollectionResultsAsync(string prefix, Query query, CancellationToken token)
        {
            try
            {
                var url = $"{ApiBase}/collection?prefix={HttpUtility.UrlEncode(prefix)}&pretty=1";
                using var resp = await Http.GetAsync(url, token);
                if (!resp.IsSuccessStatusCode)
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = $"Collection '{prefix}' not found",
                            SubTitle = $"Error {(int)resp.StatusCode} {resp.ReasonPhrase} - Check prefix (ex: mdi, ph, tabler, carbon)",
                            IcoPath = "Images\\icon.png",
                            Score = 100
                        }
                    };
                }
                var json = await resp.Content.ReadAsStringAsync(token);
                var data = JsonSerializer.Deserialize<CollectionResponse>(json);
                if (data == null || data.Icons == null || data.Icons.Count == 0)
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = $"Collection '{prefix}' empty or invalid",
                            SubTitle = data?.Total + " icons",
                            IcoPath = "Images\\icon.png"
                        }
                    };
                }

                var icons = data.Icons.Take(64).ToList();
                // Preload cache in parallel for preview
                var cacheTasks = icons.Select(n => GetCachedIconPathAsync(prefix, n, token)).ToArray();
                string[] cachedPaths;
                try
                {
                    cachedPaths = await Task.WhenAll(cacheTasks);
                }
                catch
                {
                    cachedPaths = icons.Select(n => GetCacheFilePath(prefix, n)).ToArray();
                }

                var results = new List<Result>();
                int score = 1000;
                for (int i = 0; i < icons.Count; i++)
                {
                    var iconShortName = icons[i];
                    var fullName = $"{prefix}:{iconShortName}";
                    var cached = cachedPaths[i];
                    // fallback if not yet cached -> local path even if file not yet written, ImageLoader will handle Missing; keep local path anyway
                    if (!File.Exists(cached))
                        cached = GetCacheFilePath(prefix, iconShortName);
                    // if still no file, preload in background and use placeholder for now
                    if (!File.Exists(cached))
                    {
                        PreloadCacheAsync(prefix, iconShortName);
                        cached = "Images\\icon.png";
                    }

                    results.Add(new Result
                    {
                        Title = iconShortName,
                        SubTitle = $"{fullName} | Collection {data.Title ?? prefix} | {data.Total} icons | Enter: copy SVG",
                        IcoPath = cached,
                        Score = score--,
                        CopyText = fullName,
                        AutoCompleteText = $"{query.ActionKeyword} {fullName}",
                        ContextData = fullName,
                        Action = ctx => { _ = CopySvgAsync(fullName); return false; },
                        TitleHighlightData = _context.API.FuzzySearch(query.Search, iconShortName).MatchData
                    });
                }

                results.Insert(0, new Result
                {
                    Title = $"Collection: {data.Title ?? prefix} ({prefix})",
                    SubTitle = $"{data.Total} icons | {data.Author?.Name ?? ""} | Type to filter: {query.ActionKeyword} {prefix}:<name>",
                    IcoPath = "Images\\icon.png",
                    Score = 2000,
                    Action = _ => false
                });

                return results;
            }
            catch (Exception ex)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = $"Collection error '{prefix}'",
                        SubTitle = ex.Message,
                        IcoPath = "Images\\icon.png"
                    }
                };
            }
        }

        private async Task<List<Result>> SearchIconsAsync(string effectiveQuery, Query query, CancellationToken token, string? prefixFilter = null, int topScore = 1000)
        {
            try
            {
                var qb = HttpUtility.UrlEncode(effectiveQuery);
                var url = $"{ApiBase}/search?query={qb}&limit={SearchLimit}&pretty=1";
                if (!string.IsNullOrWhiteSpace(prefixFilter))
                    url += $"&prefix={HttpUtility.UrlEncode(prefixFilter)}";

                using var resp = await Http.GetAsync(url, token);
                if (!resp.IsSuccessStatusCode)
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = "Iconify search error",
                            SubTitle = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                            IcoPath = "Images\\icon.png"
                        }
                    };
                }

                var json = await resp.Content.ReadAsStringAsync(token);
                var data = JsonSerializer.Deserialize<SearchResponse>(json);
                if (data == null || data.Icons == null || data.Icons.Count == 0)
                {
                    return new List<Result>
                    {
                        new Result
                        {
                            Title = $"No results for '{effectiveQuery}'" + (prefixFilter != null ? $" (prefix={prefixFilter})" : ""),
                            SubTitle = "Try another keyword or remove collection filter",
                            IcoPath = "Images\\icon.png",
                            Score = 100
                        },
                        new Result
                        {
                            Title = "Tip: try synonyms",
                            SubTitle = "Ex: home -> house, arrow -> chevron, close -> x",
                            IcoPath = "Images\\icon.png",
                            Score = 90
                        }
                    };
                }

                // Preload cache for preview (in parallel, limit 8 via semaphore)
                var cacheTasks = data.Icons.Select(full =>
                {
                    var p = full.Split(':');
                    return p.Length == 2 ? GetCachedIconPathAsync(p[0], p[1], token) : Task.FromResult("Images\\icon.png");
                }).ToArray();

                string[] cachedPaths;
                try
                {
                    cachedPaths = await Task.WhenAll(cacheTasks);
                }
                catch
                {
                    // in case of partial cancellation, fallback
                    cachedPaths = data.Icons.Select(_ => "Images\\icon.png").ToArray();
                }

                var results = new List<Result>();
                int score = topScore;
                for (int idx = 0; idx < data.Icons.Count; idx++)
                {
                    var fullName = data.Icons[idx];
                    var parts = fullName.Split(':');
                    if (parts.Length != 2) continue;
                    var prefix = parts[0];
                    var name = parts[1];
                    var cached = cachedPaths[idx];
                    if (!File.Exists(cached))
                        cached = "Images\\icon.png";

                    string collectionLabel = prefix;
                    if (data.Collections != null && data.Collections.TryGetValue(prefix, out var info))
                    {
                        collectionLabel = info.Name ?? prefix;
                    }

                    var match = _context.API.FuzzySearch(effectiveQuery, name);

                    results.Add(new Result
                    {
                        Title = name,
                        SubTitle = $"{fullName} | {collectionLabel} | Enter: copy SVG | Ctrl+C: copy name",
                        IcoPath = cached,
                        Score = score--,
                        CopyText = fullName,
                        AutoCompleteText = $"{query.ActionKeyword} {fullName}",
                        TitleHighlightData = match.MatchData,
                        ContextData = fullName,
                        Action = ctx => { _ = CopySvgAsync(fullName); return false; },
                        Preview = new Result.PreviewInfo
                        {
                            PreviewImagePath = cached,
                            Description = $"{fullName}\nCollection: {collectionLabel}\nSVG: {ApiBase}/{prefix}/{name}.svg"
                        }
                    });
                }

                if (data.Total > data.Icons.Count)
                {
                    results.Add(new Result
                    {
                        Title = $"{data.Total - data.Icons.Count} more results not shown",
                        SubTitle = $"Refine your search or increase limit (current: {SearchLimit})",
                        IcoPath = "Images\\icon.png",
                        Score = 0,
                        Action = _ => false
                    });
                }

                return results;
            }
            catch (TaskCanceledException)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Search cancelled (timeout)",
                        SubTitle = "Check your internet connection",
                        IcoPath = "Images\\icon.png"
                    }
                };
            }
            catch (Exception ex)
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Search error",
                        SubTitle = ex.Message,
                        IcoPath = "Images\\icon.png"
                    }
                };
            }
        }

        #endregion

        #region Clipboard & API

        private async Task CopySvgAsync(string fullName, string? color = null, string? width = null, string? height = null)
        {
            try
            {
                var parts = fullName.Split(':');
                if (parts.Length != 2) return;
                var prefix = parts[0];
                var name = parts[1];

                var url = $"{ApiBase}/{prefix}/{name}.svg";
                var qs = new List<string>();
                if (!string.IsNullOrWhiteSpace(color)) qs.Add($"color={HttpUtility.UrlEncode(color)}");
                if (!string.IsNullOrWhiteSpace(width)) qs.Add($"width={HttpUtility.UrlEncode(width)}");
                if (!string.IsNullOrWhiteSpace(height)) qs.Add($"height={HttpUtility.UrlEncode(height)}");
                if (qs.Count > 0) url += "?" + string.Join("&", qs);

                using var resp = await Http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var svg = await resp.Content.ReadAsStringAsync();

                if (!svg.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                {
                    _context.API.ShowMsg("Invalid SVG", $"Unexpected response for {fullName}", "");
                    return;
                }

                _context.API.CopyToClipboard(svg, false, true);
            }
            catch (Exception ex)
            {
                _context.API.ShowMsg($"Error copying SVG {fullName}", ex.Message, "");
            }
        }

        private void CopyTextToClipboard(string text, string successMsg)
        {
            try
            {
                _context.API.CopyToClipboard(text, false, true);
            }
            catch (Exception ex)
            {
                _context.API.ShowMsg("Copy error", ex.Message, "");
            }
        }

        #endregion

        #region IContextMenu

        public List<Result> LoadContextMenus(Result selectedResult)
        {
            var fullName = selectedResult.ContextData as string ?? selectedResult.Title;
            if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains(':'))
                return new List<Result>();

            var parts = fullName.Split(':');
            var prefix = parts[0];
            var name = parts[1];
            var svgUrl = $"{ApiBase}/{prefix}/{name}.svg";
            var cached = GetCacheFilePath(prefix, name);
            if (!File.Exists(cached)) cached = "Images\\icon.png";
            var iconifyPage = $"https://icon-sets.iconify.design/{prefix}/{name}.html";
            var searchPage = $"https://icon-sets.iconify.design/{prefix}/";

            return new List<Result>
            {
                new Result
                {
                    Title = "Copy SVG code",
                    SubTitle = $"Copy SVG for {fullName} to clipboard",
                    IcoPath = File.Exists(cached) ? cached : "Images\\icon.png",
                    Score = 1000,
                    Action = ctx => { _ = CopySvgAsync(fullName); return false; }
                },
                new Result
                {
                    Title = "Copy SVG with color #000000",
                    SubTitle = "SVG with forced black color (replaces currentColor)",
                    IcoPath = File.Exists(cached) ? cached : "Images\\icon.png",
                    Score = 900,
                    Action = ctx => { _ = CopySvgAsync(fullName, color: "#000000"); return false; }
                },
                new Result
                {
                    Title = "Copy SVG 24x24",
                    SubTitle = "SVG with dimensions 24x24",
                    IcoPath = File.Exists(cached) ? cached : "Images\\icon.png",
                    Score = 800,
                    Action = ctx => { _ = CopySvgAsync(fullName, width: "24", height: "24"); return false; }
                },
                new Result
                {
                    Title = "Copy SVG URL",
                    SubTitle = svgUrl,
                    IcoPath = "Images\\icon.png",
                    CopyText = svgUrl,
                    Score = 700,
                    Action = _ =>
                    {
                        CopyTextToClipboard(svgUrl, "URL copied");
                        return false;
                    }
                },
                new Result
                {
                    Title = "Copy icon name",
                    SubTitle = fullName,
                    IcoPath = "Images\\icon.png",
                    CopyText = fullName,
                    Score = 600,
                    Action = _ =>
                    {
                        CopyTextToClipboard(fullName, "Name copied");
                        return false;
                    }
                },
                new Result
                {
                    Title = "Copy as Data URL",
                    SubTitle = "SVG encoded as base64 for CSS/HTML",
                    IcoPath = "Images\\icon.png",
                    Score = 500,
                    Action = ctx => { _ = CopyAsDataUrlAsync(fullName); return false; }
                },
                new Result
                {
                    Title = "Open on Iconify",
                    SubTitle = iconifyPage,
                    IcoPath = "Images\\icon.png",
                    Score = 400,
                    Action = _ =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(iconifyPage) { UseShellExecute = true }); } catch {}
                        return true;
                    }
                },
                new Result
                {
                    Title = $"View collection {prefix}",
                    SubTitle = searchPage,
                    IcoPath = "Images\\icon.png",
                    Score = 300,
                    Action = _ =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(searchPage) { UseShellExecute = true }); } catch {}
                        return true;
                    }
                }
            };
        }

        private async Task CopyAsDataUrlAsync(string fullName)
        {
            try
            {
                var parts = fullName.Split(':');
                var prefix = parts[0];
                var name = parts[1];
                var url = $"{ApiBase}/{prefix}/{name}.svg";
                using var resp = await Http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var svg = await resp.Content.ReadAsStringAsync();
                var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
                var dataUrl = $"data:image/svg+xml;base64,{base64}";
                _context.API.CopyToClipboard(dataUrl, false, true);
            }
            catch (Exception ex)
            {
                _context.API.ShowMsg($"Data URL error {fullName}", ex.Message, "");
            }
        }

        #endregion
    }

    #region Models

    internal class SearchResponse
    {
        [JsonPropertyName("icons")]
        public List<string>? Icons { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("collections")]
        public Dictionary<string, CollectionInfo>? Collections { get; set; }
    }

    internal class CollectionInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("author")]
        public AuthorInfo? Author { get; set; }

        [JsonPropertyName("license")]
        public LicenseInfo? License { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("palette")]
        public bool? Palette { get; set; }
    }

    internal class AuthorInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    internal class LicenseInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("spdx")]
        public string? Spdx { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    internal class CollectionResponse
    {
        [JsonPropertyName("prefix")]
        public string? Prefix { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("icons")]
        public List<string>? Icons { get; set; }

        [JsonPropertyName("author")]
        public AuthorInfo? Author { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }

    #endregion
}

