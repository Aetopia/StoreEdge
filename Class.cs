namespace StoreEdge;

using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.System.UserProfile;
using System.Collections.ObjectModel;
using Windows.Management.Deployment;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using System.Runtime.Serialization.Json;

public interface IProduct
{
    string Title { get; }

    string AppCategoryId { get; }
}

public interface IUpdate { string UpdateId { get; } }

file struct Product(string title, string appCategoryId) : IProduct
{
    public readonly string Title => title;

    public readonly string AppCategoryId => appCategoryId;
}

file class Update : IUpdate
{
    string updateId;

    internal string Id;

    internal DateTime Modified;

    internal bool MainPackage;

    public string UpdateId { get { return updateId; } set { updateId = value; } }
}


file static class Resources
{
    static readonly Assembly assembly = Assembly.GetExecutingAssembly();

    internal readonly static string GetCookie = ToString("GetCookie.xml");

    internal readonly static string GetExtendedUpdateInfo2 = ToString("GetExtendedUpdateInfo2.xml");

    internal readonly static string SyncUpdates = ToString("SyncUpdates.xml");

    static string ToString(string name)
    {
        using StreamReader stream = new(assembly.GetManifestResourceStream(name));
        return stream.ReadToEnd();
    }
}

file struct SynchronizationContextRemover : INotifyCompletion
{
    internal readonly bool IsCompleted => SynchronizationContext.Current == null;

    internal readonly void GetResult() { }

    internal readonly SynchronizationContextRemover GetAwaiter() { return this; }

    public readonly void OnCompleted(Action continuation)
    {
        var syncContext = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(null); continuation(); }
        finally { SynchronizationContext.SetSynchronizationContext(syncContext); }
    }
}


public class Store
{
    readonly string syncUpdates;

    static readonly JavaScriptSerializer javaScriptSerializer = new();

    static readonly string requestUri = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products{{0}}?market={GlobalizationPreferences.HomeGeographicRegion}&locale=iv&deviceFamily=Windows.Desktop";

    static readonly HttpClient client = new() { BaseAddress = new("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/") };

    static readonly PackageManager packageManager = new();

    static readonly string architecture = RuntimeInformation.OSArchitecture.ToString().ToLower();

    Store(string encryptedData) { syncUpdates = Resources.SyncUpdates.Replace("{1}", encryptedData); }

    public static async Task<Store> CreateAsync()
    {
        await default(SynchronizationContextRemover);

        return new((await PostAsSoapAsync(Resources.GetCookie)).GetElementsByTagName("EncryptedData")[0].InnerText);
    }

    public async Task<ReadOnlyCollection<IProduct>> GetProductsAsync(params string[] productIds)
    {
        await default(SynchronizationContextRemover);

        List<IProduct> products = [];
        foreach (var productId in productIds)
        {
            using var message = await client.GetAsync(string.Format(requestUri, $"/{productId}"));
            message.EnsureSuccessStatusCode();

            var payload = Deserialize(await message.Content.ReadAsStringAsync())["Payload"];
            var title = payload?["ShortTitle"]?.InnerText;

            products.Add(new Product(
                string.IsNullOrEmpty(title) ? payload["Title"].InnerText : title,
                Deserialize(payload.GetElementsByTagName("FulfillmentData")[0].InnerText)["WuCategoryId"].InnerText));
        }

        return products.AsReadOnly();
    }

    public async Task<ReadOnlyCollection<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
    {
        await default(SynchronizationContextRemover);

        using StringContent content = new(javaScriptSerializer.Serialize(new Dictionary<object, object>()
        {
            ["IdType"] = "PackageFamilyName",
            ["ProductIds"] = packageFamilyNames
        }), Encoding.UTF8, "application/json");

        using var message = await client.PostAsync(string.Format(requestUri, string.Empty), content);
        message.EnsureSuccessStatusCode();

        return Deserialize(await message.Content.ReadAsStringAsync())
        .GetElementsByTagName("ProductId")
        .Cast<XmlElement>()
        .Select(element => element.InnerText)
        .ToList()
        .AsReadOnly();
    }

    public async Task<string> GetUrlAsync(IUpdate update)
    {
        await default(SynchronizationContextRemover);

        return (await PostAsSoapAsync(Resources.GetExtendedUpdateInfo2.Replace("{1}", update.UpdateId), true)).GetElementsByTagName("Url").Cast<XmlNode>().First(
            node => node.InnerText.StartsWith("http://tlu.dl.delivery.mp.microsoft.com")).InnerText;
    }

    public async Task<ReadOnlyCollection<IUpdate>> SyncUpdatesAsync(IProduct product)
    {
        await default(SynchronizationContextRemover);

        var syncUpdatesResult = (XmlElement)(await PostAsSoapAsync(syncUpdates.Replace("{2}", product.AppCategoryId))).GetElementsByTagName("SyncUpdatesResult")[0];

        Dictionary<string, Update> updates = [];
        foreach (XmlNode xmlNode in syncUpdatesResult.GetElementsByTagName("AppxPackageInstallData"))
        {
            var element = (XmlElement)xmlNode.ParentNode.ParentNode.ParentNode;
            var file = element.GetElementsByTagName("File")[0];

            var packageIdentity = file.Attributes["InstallerSpecificIdentifier"].InnerText.Split('_');
            if (!packageIdentity[2].Equals(architecture, StringComparison.OrdinalIgnoreCase) && !packageIdentity[2].Equals("neutral")) continue;
            if (!updates.ContainsKey(packageIdentity[0])) updates.Add(packageIdentity[0], new());

            var modified = Convert.ToDateTime(file.Attributes["Modified"].InnerText);
            if (updates[packageIdentity[0]].Modified < modified)
            {
                updates[packageIdentity[0]].Id = element["ID"].InnerText;
                updates[packageIdentity[0]].Modified = modified;
                updates[packageIdentity[0]].MainPackage = xmlNode.Attributes["MainPackage"].InnerText == "true";
            }
        }

        foreach (XmlNode xmlNode in syncUpdatesResult.GetElementsByTagName("SecuredFragment"))
        {
            var element = (XmlElement)xmlNode.ParentNode.ParentNode.ParentNode;
            var update = updates.FirstOrDefault(update => update.Value.Id.Equals(element["ID"].InnerText));
            if (update.Value == null) continue;

            var packageIdentity = element.GetElementsByTagName("AppxMetadata")[0].Attributes["PackageMoniker"].InnerText.Split('_');
            var package = packageManager.FindPackagesForUser(string.Empty, $"{packageIdentity.First()}_{packageIdentity.Last()}").FirstOrDefault();

            if (package == null || (!package.IsDevelopmentMode && new Version(packageIdentity[1]) >
                new Version(package.Id.Version.Major, package.Id.Version.Minor, package.Id.Version.Build, package.Id.Version.Revision)))
                updates[update.Key].UpdateId = element.GetElementsByTagName("UpdateIdentity")[0].Attributes["UpdateID"].InnerText;
            else if (update.Value.MainPackage) return new([]);
            else updates.Remove(update.Key);
        }

        return updates
        .Select(update => update.Value)
        .OrderBy(update => update.MainPackage)
        .Cast<IUpdate>()
        .ToList()
        .AsReadOnly();
    }

    static XmlElement Deserialize(string input)
    {
        using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(input), XmlDictionaryReaderQuotas.Max);
        XmlDocument xml = new();
        xml.Load(reader);
        return xml["root"];
    }

    static async Task<XmlDocument> PostAsSoapAsync(string value, bool secured = false)
    {
        using StringContent content = new(value, Encoding.UTF8, "application/soap+xml");
        using var response = await client.PostAsync(secured ? "secured" : null, content);
        response.EnsureSuccessStatusCode();

        XmlDocument xml = new();
        xml.LoadXml((await response.Content.ReadAsStringAsync()).Replace("&lt;", "<").Replace("&gt;", ">"));
        return xml;
    }
}
