using Newtonsoft.Json;
using Playnite.Common;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>What the Microsoft Store says about one of its installs.</summary>
    public class StoreInstallStatus
    {
        public string State { get; set; }
        public double Percent { get; set; }
        public string Error { get; set; }

        public bool IsFinished => State == "Completed";

        /// <summary>Ended without the game: the Store won't do anything else with it.</summary>
        public bool HasFailed => State == "Error" || State == "Canceled";
    }

    /// <summary>
    /// The install queue of the Microsoft Store, the same one the Store and the Xbox app use. It's a
    /// part of Windows the program reaches by name so that it also runs where it isn't there.
    /// </summary>
    internal class MicrosoftStoreQueue
    {
        private const string ManagerName = "Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager, Windows.ApplicationModel.Store.Preview, ContentType=WindowsRuntime";
        private const string OptionsName = "Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallOptions, Windows.ApplicationModel.Store.Preview, ContentType=WindowsRuntime";
        private const string ItemName = "Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem, Windows.ApplicationModel.Store.Preview, ContentType=WindowsRuntime";
        private const string ExtensionsName = "System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        private readonly object manager;

        public MicrosoftStoreQueue()
        {
            manager = Activator.CreateInstance(Type.GetType(ManagerName, true));
        }

        private static object Read(object target, string property)
        {
            return target.GetType().GetProperty(property).GetValue(target);
        }

        private static object Call(object target, string method, params object[] values)
        {
            var found = target.GetType().GetMethods().First(a => a.Name == method && a.GetParameters().Length == values.Length);
            return found.Invoke(target, values);
        }

        private IEnumerable<object> GetItems()
        {
            return ((IEnumerable)Read(manager, "AppInstallItems")).Cast<object>();
        }

        /// <summary>The Store remembers an install that failed and answers with it until somebody clears it.</summary>
        private void ClearFailed(string productId)
        {
            foreach (var item in GetItems().Where(a => (string)Read(a, "ProductId") == productId))
            {
                if (GetStatus(item).HasFailed)
                {
                    Call(item, "Cancel");
                }
            }
        }

        public async Task<object> StartAsync(string productId)
        {
            ClearFailed(productId);
            var itemType = Type.GetType(ItemName, true);
            var listType = typeof(IReadOnlyList<>).MakeGenericType(itemType);
            var options = Activator.CreateInstance(Type.GetType(OptionsName, true));
            var operation = manager.GetType().GetMethods().
                First(a => a.Name == "StartProductInstallAsync" && a.GetParameters().Length == 5).
                Invoke(manager, new object[] { productId, string.Empty, "Atrio", string.Empty, options });

            var asTask = Type.GetType(ExtensionsName, true).GetMethods(BindingFlags.Public | BindingFlags.Static).First(a =>
                a.Name == "AsTask" &&
                a.GetParameters().Length == 1 &&
                a.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");

            var task = (Task)asTask.MakeGenericMethod(listType).Invoke(null, new[] { operation });
            await task.ConfigureAwait(false);
            // A bundle answers with one item for each of its parts.
            var items = ((IEnumerable)task.GetType().GetProperty("Result").GetValue(task)).Cast<object>().ToList();
            return items.FirstOrDefault(a => (string)Read(a, "ProductId") == productId) ?? items.First();
        }

        public StoreInstallStatus GetStatus(object item)
        {
            var status = Call(item, "GetCurrentStatus");
            return new StoreInstallStatus
            {
                State = Read(status, "InstallState").ToString(),
                Percent = Convert.ToDouble(Read(status, "PercentComplete")),
                Error = (Read(status, "ErrorCode") as Exception)?.Message
            };
        }

        public void Cancel(object item)
        {
            Call(item, "Cancel");
        }
    }

    /// <summary>Installs a game of the Microsoft Store from its queue, with no windows involved.</summary>
    public class XboxDirectInstallController : DirectInstallController
    {
        private class Product
        {
            public string ProductId { get; set; }
        }

        private class ProductList
        {
            public List<Product> Products { get; set; }
        }

        private const string GamingServicesProduct = "9MWPM2CQNLHN";
        private const string GamingServicesService = "GamingServices";
        private const string CatalogUrl = "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&Value={0}&market=US&languages=en-US&MS-CV=DGU1mcuYo0WMMp+F.1";
        private const string ConsoleGamePrefix = "CONSOLE_";

        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan servicesPatience = TimeSpan.FromSeconds(30);
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public XboxDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        /// <summary>Only what runs on this computer has something to install.</summary>
        public static bool CanInstall(Game game)
        {
            return !game.GameId.IsNullOrEmpty() && !game.GameId.StartsWith(ConsoleGamePrefix, StringComparison.Ordinal);
        }

        /// <summary>Null if the catalog doesn't know the package.</summary>
        public static string ParseProductId(string catalogAnswer)
        {
            return JsonConvert.DeserializeObject<ProductList>(catalogAnswer)?.Products?.FirstOrDefault()?.ProductId;
        }

        private async Task<string> FindProductAsync()
        {
            var url = string.Format(CatalogUrl, Uri.EscapeDataString(Game.GameId));
            using (var response = await client.GetAsync(url, Cancellation).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"The Microsoft catalog didn't know {Game.GameId}: {(int)response.StatusCode}.");
                    return null;
                }

                return ParseProductId(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private static bool HasGamingServices()
        {
            return ServiceController.GetServices().Any(a => a.ServiceName.Equals(GamingServicesService, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> WaitAsync(MicrosoftStoreQueue queue, object item, bool report, Func<bool> alreadyDone = null)
        {
            while (true)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                if (alreadyDone?.Invoke() == true)
                {
                    return true;
                }

                var status = queue.GetStatus(item);
                if (status.IsFinished)
                {
                    return true;
                }

                if (status.HasFailed)
                {
                    Logger.Warn($"The Microsoft Store stopped installing {Game.GameId}: {status.State}, {status.Error}");
                    if (status.State == "Error")
                    {
                        queue.Cancel(item);
                    }

                    return false;
                }

                if (report)
                {
                    DirectInstallProgress.Set(Game.Id, (int)status.Percent);
                }
            }
        }

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            var product = await FindProductAsync().ConfigureAwait(false);
            if (product.IsNullOrEmpty())
            {
                return false;
            }

            var queue = new MicrosoftStoreQueue();
            if (!HasGamingServices())
            {
                // Without it the games fail to install, and it's what the Xbox app would install first.
                Logger.Info("Installing Gaming Services, Xbox games need it.");
                var services = await queue.StartAsync(GamingServicesProduct).ConfigureAwait(false);
                await WaitAsync(queue, services, false, HasGamingServices).ConfigureAwait(false);

                // The Store can call it an error and still leave it installed a moment later.
                var started = DateTime.Now;
                while (!HasGamingServices() && DateTime.Now - started < servicesPatience)
                {
                    await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                }
            }

            var item = await queue.StartAsync(product).ConfigureAwait(false);
            Logger.Info($"Microsoft Store told to install {product} ({Game.GameId}).");
            if (!await WaitAsync(queue, item, true).ConfigureAwait(false))
            {
                return false;
            }

            var app = Programs.GetUWPApps().FirstOrDefault(a => a.AppId?.Equals(Game.GameId, StringComparison.OrdinalIgnoreCase) == true);
            Finish(app?.WorkDir);
            return true;
        }
    }
}
