namespace StoreEdge;

using System;
using System.IO;
using System.Xml;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.System.UserProfile;
using Windows.Management.Deployment;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;

public interface IProduct
{
    string Title { get; }

    string AppCategoryId { get; }
}

public interface IUpdateIdentity
{
    string UpdateId { get; }
}

file struct Product(string title, string appCategoryId) : IProduct
{
    public readonly string Title => title;

    public readonly string AppCategoryId => appCategoryId;
}

file struct UpdateIdentity(string updateId, bool mainPackage) : IUpdateIdentity
{
    public readonly string UpdateId => updateId;

    internal readonly bool MainPackage => mainPackage;
}

file class Update
{
    internal string Id;

    internal DateTime Modified;

    internal bool MainPackage;
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

file struct UncapturedContext : INotifyCompletion
{
    internal readonly bool IsCompleted => SynchronizationContext.Current == null;

    internal readonly void GetResult() { }

    internal readonly UncapturedContext GetAwaiter() { return this; }

    public readonly void OnCompleted(Action continuation)
    {
        var syncContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            continuation();
        }
        finally { SynchronizationContext.SetSynchronizationContext(syncContext); }
    }
}


public class Store
{
    readonly string syncUpdates;

    static readonly JavaScriptSerializer javaScriptSerializer = new();

    static readonly string requestUri = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products{{0}}?market={GlobalizationPreferences.HomeGeographicRegion}&locale=iv&deviceFamily=Windows.Desktop";

    static readonly HttpClient httpClient = new() { BaseAddress = new("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/") };

    static readonly PackageManager packageManager = new();

    static readonly string architecture = RuntimeInformation.OSArchitecture.ToString().ToLower();

    Store(string encryptedData) { syncUpdates = Resources.SyncUpdates.Replace("{1}", encryptedData); }

    public static async Task<Store> CreateAsync()
    {
        await default(UncapturedContext);

        return new((await PostAsSoapAsync(Resources.GetCookie)).GetElementsByTagName("EncryptedData")[0].InnerText);
    }

    public async Task<IEnumerable<IProduct>> GetProductsAsync(params string[] productIds)
    {
        await default(UncapturedContext);

        List<IProduct> products = [];
        foreach (var productId in productIds)
        {
            var payload = javaScriptSerializer.Deserialize<dynamic>(await httpClient.GetStringAsync(string.Format(requestUri, $"/{productId}")))["Payload"];
            ((Dictionary<string, object>)payload).TryGetValue("ShortTitle", out object value);

            products.Add(new Product((string)(value ?? payload["Title"]), javaScriptSerializer.Deserialize<dynamic>(payload["Skus"][0]["FulfillmentData"])["WuCategoryId"]));
        }

        return products;
    }

    public async Task<IEnumerable<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
    {
        await default(UncapturedContext);

        using StringContent content = new(javaScriptSerializer.Serialize(new Dictionary<dynamic, dynamic>()
        {
            ["IdType"] = "PackageFamilyName",
            ["ProductIds"] = packageFamilyNames
        }));

        content.Headers.ContentType = new("application/json");
        using var response = await httpClient.PostAsync(string.Format(requestUri, string.Empty), content);

        return ((IEnumerable<dynamic>)javaScriptSerializer.Deserialize<dynamic>(
            await response.Content.ReadAsStringAsync())["Payload"]["Products"]).Select(product => (string)product["ProductId"]);
    }

    public async Task<string> GetUrlAsync(IUpdateIdentity updateIdentity)
    {
        await default(UncapturedContext);

        return (await PostAsSoapAsync(Resources.GetExtendedUpdateInfo2.Replace("{1}", updateIdentity.UpdateId), true)).GetElementsByTagName("Url").Cast<XmlNode>().First(
            xmlNode => xmlNode.InnerText.StartsWith("http://tlu.dl.delivery.mp.microsoft.com")).InnerText;
    }

    public async Task<IEnumerable<IUpdateIdentity>> SyncUpdates(IProduct product)
    {
        await default(UncapturedContext);

        var syncUpdatesResult = (XmlElement)(await PostAsSoapAsync(syncUpdates.Replace("{2}", product.AppCategoryId))).GetElementsByTagName("SyncUpdatesResult")[0];
        Dictionary<string, Update> updates = [];

        foreach (XmlNode xmlNode in syncUpdatesResult.GetElementsByTagName("AppxPackageInstallData"))
        {
            var xmlElement = (XmlElement)xmlNode.ParentNode.ParentNode.ParentNode;
            var file = xmlElement.GetElementsByTagName("File")[0];

            var packageIdentity = file.Attributes["InstallerSpecificIdentifier"].InnerText.Split('_');
            if (!string.Equals(packageIdentity[2], architecture) && !string.Equals(packageIdentity[2], "neutral")) continue;
            if (!updates.ContainsKey(packageIdentity[0])) updates.Add(packageIdentity[0], new());

            var modified = Convert.ToDateTime(file.Attributes["Modified"].InnerText);
            if (updates[packageIdentity[0]].Modified < modified)
            {
                updates[packageIdentity[0]].Id = xmlElement["ID"].InnerText;
                updates[packageIdentity[0]].Modified = modified;
                updates[packageIdentity[0]].MainPackage = xmlNode.Attributes["MainPackage"].InnerText == "true";
            }
        }

        List<UpdateIdentity> updateIdentities = [];
        foreach (XmlNode xmlNode in syncUpdatesResult.GetElementsByTagName("SecuredFragment"))
        {
            var xmlElement = (XmlElement)xmlNode.ParentNode.ParentNode.ParentNode;
            var update = updates.Values.FirstOrDefault(update => string.Equals(update.Id, xmlElement["ID"].InnerText));
            if (update == null) continue;

            if (CheckUpdateAvailability(xmlElement.GetElementsByTagName("AppxMetadata")[0].Attributes["PackageMoniker"].InnerText))
                updateIdentities.Add(new(xmlElement.GetElementsByTagName("UpdateIdentity")[0].Attributes["UpdateID"].InnerText, update.MainPackage));
            else if (update.MainPackage) return [];
        }

        return (IEnumerable<IUpdateIdentity>)updateIdentities.OrderBy(updateIdentity => updateIdentity.MainPackage);
    }

    static async Task<XmlDocument> PostAsSoapAsync(string xml, bool secured = false)
    {
        using StringContent content = new(xml);
        content.Headers.ContentType = new("application/soap+xml");

        using var response = await httpClient.PostAsync(secured ? "secured" : null, content);
        response.EnsureSuccessStatusCode();

        XmlDocument xmlDocument = new();
        xmlDocument.LoadXml((await response.Content.ReadAsStringAsync()).Replace("&lt;", "<").Replace("&gt;", ">"));
        return xmlDocument;
    }

    static bool CheckUpdateAvailability(string packageFullName)
    {
        var packageIdentity = packageFullName.Split('_');
        var package = packageManager.FindPackagesForUser(string.Empty, $"{packageIdentity.First()}_{packageIdentity.Last()}").FirstOrDefault();

        return
            !(package?.IsDevelopmentMode ?? false) &&
            new Version(packageIdentity[1]) > new Version(package?.Id.Version.Major ?? 0, package?.Id.Version.Minor ?? 0, package?.Id.Version.Build ?? 0, package?.Id.Version.Revision ?? 0);
    }
}
