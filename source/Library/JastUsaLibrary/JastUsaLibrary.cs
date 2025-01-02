﻿using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PluginsCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using Playnite.SDK.Events;
using JastUsaLibrary.DownloadManager.Views;
using System.Collections.ObjectModel;
using JastUsaLibrary.ProgramsHelper;
using PlayniteUtilitiesCommon;
using System.Threading;
using JastUsaLibrary.JastUsaIntegration.Infrastructure.Persistence;
using JastUsaLibrary.JastUsaIntegration.Application.Services;
using JastUsaLibrary.JastUsaIntegration.Infrastructure.External;
using JastUsaLibrary.DownloadManager.Domain.Events;
using EventsCommon;
using JastUsaLibrary.JastUsaIntegration.Domain.Enums;
using JastUsaLibrary.DownloadManager.Domain.Entities;
using JastUsaLibrary.JastUsaIntegration.Domain.Aggregates.Product;
using JastUsaLibrary.DownloadManager.Domain.Enums;
using JastUsaLibrary.DownloadManager.Application;
using JastUsaLibrary.JastLibraryCacheService.Application;
using JastUsaLibrary.DownloadManager.Presentation;
using JastUsaLibrary.JastLibraryCacheService.Interfaces;
using JastUsaLibrary.JastUsaIntegration.Domain.Entities;
using JastUsaLibrary.JastLibraryCacheService.Persistence;
using JastUsaLibrary.Features.MetadataProvider;
using JastUsaLibrary.Features.DownloadManager.Application;
using JastUsaLibrary.Features.DownloadManager.Infrastructure;

namespace JastUsaLibrary
{
    public class JastUsaLibrary : LibraryPlugin
    {
        private static readonly ILogger _logger = LogManager.GetLogger();
        private JastUsaLibrarySettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("d407a620-5953-4ca4-a25c-8194c8559381");
        public override string LibraryIcon { get; }

        private readonly string _sidebarBaseItemTitle;
        private readonly SidebarItem _sidebarLibraryManagerView;

        public override string Name => "JAST USA";
        public override LibraryClient Client { get; } = new JastUsaLibraryClient();
        private readonly JastUsaAccountClient _accountClient;
        private readonly string _userGamesCachePath;
        public string UserGamesCachePath => _userGamesCachePath;
        private readonly ILibraryCacheService _libraryCacheService;
        private readonly DownloadsManager _downloadsManager;
        private DownloadsManagerViewModel _downloadsManagerViewModel;

        public JastUsaLibrary(IPlayniteAPI api) : base(api)
        {
            var apiClient = new JastUsaApiClient(_logger);
            var authenticationPersistence = new AuthenticationPersistence(Path.Combine(GetPluginUserDataPath(), "authentication.json"));
            _accountClient = new JastUsaAccountClient(api, apiClient, authenticationPersistence);
            settings = new JastUsaLibrarySettingsViewModel(this, PlayniteApi, _accountClient);
            _userGamesCachePath = Path.Combine(GetPluginUserDataPath(), "userGamesCache.json");

            //var downloadsPersistence = new DownloadDataPersistencePluginSettings(settings);
            var downloadsPersistence = new DownloadDataPersistenceJson(_logger, GetPluginUserDataPath());
            _libraryCacheService = new LibraryCacheService(PlayniteApi, new LibraryCachePersistencePluginSettings(settings), Id);
            _downloadsManager = new DownloadsManager(this, _accountClient, settings, PlayniteApi, downloadsPersistence, _libraryCacheService);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };

            LibraryIcon = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");
            _sidebarBaseItemTitle = ResourceProvider.GetString("LOC_JUL_JastLibraryManager");
            _sidebarLibraryManagerView = new SidebarItem
            {
                Title = _sidebarBaseItemTitle,
                Type = SiderbarItemType.View,
                Icon = LibraryIcon,
                ProgressValue = 0,
                ProgressMaximum = 100,
                Opened = () => {
                    _downloadsManagerViewModel = new DownloadsManagerViewModel(this, settings, PlayniteApi, _libraryCacheService, _downloadsManager);
                    return new DownloadsManagerView() { DataContext = _downloadsManagerViewModel };
                },
                Closed = () => {
                    _downloadsManagerViewModel?.Dispose();
                }
            };

            SubscribeToEvents();
        }

        private void SubscribeToEvents()
        {
            _downloadsManager.GlobalDownloadProgressChanged += OnGlobalProgressChanged;
        }

        private void UnsubscribeFromEvents()
        {
            _downloadsManager.GlobalDownloadProgressChanged -= OnGlobalProgressChanged;
        }

        private void OnGlobalProgressChanged(object sender, GlobalDownloadProgressChangedEventArgs e)
        {
            UpdateSidebarProgress(e);
        }

        private void UpdateSidebarProgress(GlobalDownloadProgressChangedEventArgs e)
        {
            if (e.TotalItems == 0 || !e.TotalDownloadProgress.HasValue)
            {
                _sidebarLibraryManagerView.ProgressValue = 0;
            }
            else
            {
                _sidebarLibraryManagerView.ProgressValue = e.TotalDownloadProgress.Value;
                _sidebarLibraryManagerView.Title = _sidebarBaseItemTitle
                    + Environment.NewLine + Environment.NewLine
                    + $"{e.TotalBytesDownloaded.Value.ToReadableSize()}/{e.TotalBytesToDownload.Value.ToReadableSize()}"
                    + $" ({e.TotalDownloadProgress.Value:F2}%)";
            }

            if (_sidebarLibraryManagerView.ProgressValue == 0 || _sidebarLibraryManagerView.ProgressValue == 100)
            {
                _sidebarLibraryManagerView.Title = _sidebarBaseItemTitle;
            }
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();
            var isLoggedIn = _accountClient.GetIsUserLoggedIn();
            if (!isLoggedIn)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage("JastNotLoggedIn", "JAST USA Library: " + ResourceProvider.GetString("LOC_JUL_DialogMessageNotAuthenticated"), NotificationType.Error, () => OpenSettingsView()));
                return games;
            }

            var jastProducts = Task.Run(() => _accountClient.GetGamesAsync())
                .GetAwaiter().GetResult();
            if (jastProducts.Count == 0)
            {
                return games;
            }

            FileSystem.WriteStringToFile(_userGamesCachePath, Serialization.ToJson(jastProducts), true);
            var libraryCache = _libraryCacheService.GetAllCache();
            foreach (var kv in libraryCache)
            {
                var gameVariant = jastProducts.FirstOrDefault(x => x.ProductVariant.GameId.ToString() == kv.GameId);
                if (gameVariant is null)
                {
                    _libraryCacheService.RemoveCacheById(kv.GameId);
                    _logger.Debug($"Removed cache with id {kv.GameId} because it was not found in the JAST games list response");
                }
                else if (kv.Product is null || Serialization.ToJson(kv.Product) != Serialization.ToJson(gameVariant)) // TODO Properly compare if it has changed
                {
                    kv.Product = gameVariant;
                    _libraryCacheService.SaveCache(kv);
                }
            }

            foreach (var jastProduct in jastProducts)
            {
                if (!jastProduct.ProductVariant.Platforms.Any(x => x.Any(y => y.Value == JastPlatform.Windows)))
                {
                    continue;
                }

                var game = new GameMetadata
                {
                    Name = GameNameSanitizer.Satinize(jastProduct.ProductVariant.ProductName),
                    GameId = jastProduct.ProductVariant.GameId.ToString(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                    Source = new MetadataNameProperty("JAST USA")
                };

                var existingCache = libraryCache.FirstOrDefault(x => x.GameId == game.GameId);
                if (existingCache != null)
                {
                    if (existingCache.Program?.Path.IsNullOrEmpty() == false && FileSystem.FileExists(existingCache.Program?.Path))
                    {
                        game.InstallDirectory = Path.GetDirectoryName(existingCache.Program.Path);
                        game.IsInstalled = true;
                    }
                }
                else
                {
                    var newLibraryCache = new GameCache
                    {
                        GameId = game.GameId,
                        Product = jastProduct
                    };

                    _libraryCacheService.SaveCache(newLibraryCache);
                }

                games.Add(game);
            }

            foreach (var cache in libraryCache)
            {
                if (cache.Assets != null)
                {
                    continue;
                }

                var assets = new ObservableCollection<JastAssetWrapper>();
                var gameTranslations = cache.Product?.ProductVariant.Game.Translations.Where(x => x.Key == Locale.En_Us);
                if (gameTranslations.HasItems())
                {
                    var response = Task.Run(() => _accountClient.GetGameTranslationsAsync(gameTranslations.First().Value))
                        .GetAwaiter().GetResult();
                    if (response is null)
                    {
                        continue;
                    }

                    assets = (new[]
                    {
                        response.GamePathLinks?
                            .Select(x => new JastAssetWrapper(x, JastAssetType.Game)) ?? Enumerable.Empty<JastAssetWrapper>(),
                        response.GameExtraLinks?
                            .Select(x => new JastAssetWrapper(x, JastAssetType.Extra)) ?? Enumerable.Empty<JastAssetWrapper>(),
                        response.GamePatchLinks?
                            .Select(x => new JastAssetWrapper(x, JastAssetType.Patch)) ?? Enumerable.Empty<JastAssetWrapper>()
                    })
                    .SelectMany(x => x)
                    .ToObservable();
                }

                cache.Assets = assets;
                _libraryCacheService.SaveCache(cache);
            }

            return games;
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new JastUsaLibraryMetadataProvider(_userGamesCachePath);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new JastUsaLibrarySettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            settings.UpgradeSettings();
            if (settings.Settings.StartDownloadsOnStartup)
            {
                _ = Task.Run(() => _downloadsManager.StartDownloadsAsync(false, false));
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var game = args.Games.Last();
            if (args.Games.Count == 1 && game.PluginId == Id)
            {
                var gameCache = _libraryCacheService.GetCacheById(game.GameId);
                if (gameCache is null)
                {
                    return base.GetGameMenuItems(args);
                }

                const string menuSection = "JAST USA Library";
                return new List<GameMenuItem>
                {
                    new GameMenuItem
                    {
                        Description = ResourceProvider.GetString("LOC_JUL_DialogMessageBrowseForGameOption"),
                        MenuSection = menuSection,
                        Icon = PlayniteUtilities.GetIcoFontGlyphResource('\uEC5B'),
                        Action = a =>
                        {
                            var selectedProgram = ProgramsService.SelectExecutable();
                            if (selectedProgram != null)
                            {
                                gameCache.Program = selectedProgram;
                                SavePluginSettings();
                                game.IsInstalled = true;
                                game.InstallDirectory = Path.GetDirectoryName(gameCache.Program.Path);
                                PlayniteApi.Database.Games.Update(game);
                            }
                        }
                    }
                };

            }

            return base.GetGameMenuItems(args);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            var game = args.Game;
            if (game.PluginId == Id)
            {
                var gameCache = _libraryCacheService.GetCacheById(game.GameId);
                if (gameCache != null && gameCache.Program != null)
                {
                    return new List<PlayController>
                    {
                        new AutomaticPlayController(game)
                        {
                            Name = gameCache.Program.Name,
                            Path = gameCache.Program.Path,
                            Arguments = gameCache.Program.Arguments,
                            WorkingDir = gameCache.Program.WorkDir,
                            TrackingMode = TrackingMode.Default
                        }
                    };
                }
            }

            return base.GetPlayActions(args);
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            var game = args.Game;
            if (args.Game.PluginId == Id)
            {
                if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOC_JUL_MessageInstallUnavailableFsMode"), "JAST USA Library");
                    return base.GetInstallActions(args);
                }

                var gameCache = _libraryCacheService.GetCacheById(game.GameId);
                if (gameCache?.Product != null)
                {
                    return new List<InstallController> { new JastInstallController(game, gameCache, PlayniteApi, _logger, _downloadsManager, this) };
                }
            }

            return base.GetInstallActions(args);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            var game = args.Game;
            if (game.PluginId == Id)
            {
                if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOC_JUL_MessageInstallUnavailableFsMode"), "JAST USA Library");
                    return base.GetUninstallActions(args);
                }

                var gameCache = _libraryCacheService.GetCacheById(game.GameId);
                if (gameCache != null && gameCache.Program != null && FileSystem.FileExists(gameCache.Program.Path))
                {
                    return new List<UninstallController> { new JastUninstallController(game, gameCache, this) };
                }
            }

            return base.GetUninstallActions(args);
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            UnsubscribeFromEvents();
            _downloadsManager.StopDownloadsAndPersistDownloadData();
            _downloadsManager?.Dispose();
            _downloadsManagerViewModel?.Dispose();
        }

        public GameTranslationsResponse GetGameTranslations(Game game, CancellationToken cancellationToken)
        {
            if (!FileSystem.FileExists(_userGamesCachePath))
            {
                return null;
            }
            
            var cache = Serialization.FromJsonFile<List<JastProduct>>(_userGamesCachePath);
            var gameVariant = cache.FirstOrDefault(x => x.ProductVariant.GameId.ToString() == game.GameId);
            if (gameVariant is null)
            {
                return null;
            }

            var gameTranslations = gameVariant.ProductVariant.Game.Translations.Where(x => x.Key == Locale.En_Us);
            if (!gameTranslations.HasItems())
            {
                return null;
            }

            return Task.Run(() => _accountClient.GetGameTranslationsAsync(gameTranslations.First().Value))
                .GetAwaiter().GetResult();
        }

        public void SavePluginSettings()
        {
            settings.SaveSettings();
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return _sidebarLibraryManagerView;
        }

    }
}