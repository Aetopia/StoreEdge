namespace StoreEdge;

using System;
using System.IO;
using System.Xml;
using System.Net;
using System.Text;
using System.Linq;
using Windows.System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.UserProfile;
using System.Collections.Generic;
using Windows.Management.Deployment;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;

readonly struct Resources
{
    static readonly System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();

    internal static readonly string GetExtendedUpdateInfo2 = GetString("GetExtendedUpdateInfo2.xml");

    internal static string GetString(string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}

readonly struct _ : INotifyCompletion
{
    internal readonly bool IsCompleted => SynchronizationContext.Current == null;

    internal readonly void GetResult() { }

    internal readonly _ GetAwaiter() { return this; }

    public readonly void OnCompleted(Action _)
    {
        var syncContext = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(null); _(); }
        finally { SynchronizationContext.SetSynchronizationContext(syncContext); }
    }
}

public sealed class Product
{
    internal Product(string title, string architecture, string appCategoryId)
    {
        Title = title;
        Architecture = architecture;
        AppCategoryId = appCategoryId;
    }

    public readonly string Title;

    public readonly string Architecture;

    internal readonly string AppCategoryId;
}

public sealed class UpdateIdentity
{
    internal UpdateIdentity(string updateId, string revisionNumber, bool mainPackage)
    {
        UpdateID = updateId;
        RevisionNumber = revisionNumber;
        MainPackage = mainPackage;
    }

    internal readonly string UpdateID;

    internal readonly string RevisionNumber;

    internal readonly bool MainPackage;
}

sealed class Update
{
    internal string ID;

    internal DateTime Modified;

    internal ProcessorArchitecture Architecture;

    internal string PackageFamilyName;

    internal string Version;

    internal bool MainPackage;
}

public static class Store
{
    static string content;

    static readonly JavaScriptSerializer serializer = new() { MaxJsonLength = int.MaxValue, RecursionLimit = int.MaxValue };

    static readonly PackageManager manager = new();

    static readonly HttpClient client = new() { BaseAddress = new("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/") };

    static readonly string requestUri = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products{{0}}?market={GlobalizationPreferences.HomeGeographicRegion}&locale=iv&deviceFamily=Windows.Desktop";

    static readonly (
        (string String, ProcessorArchitecture Architecture) Native,
        (string String, ProcessorArchitecture Architecture) Compatible
    ) architectures = (
        (
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => ProcessorArchitecture.X86,
                Architecture.X64 => ProcessorArchitecture.X64,
                Architecture.Arm => ProcessorArchitecture.Arm,
                Architecture.Arm64 => ProcessorArchitecture.Arm64,
                _ => ProcessorArchitecture.Unknown
            }
        ),
        (
            RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x86", Architecture.Arm64 => "arm", _ => null },
            RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => ProcessorArchitecture.X86,
                Architecture.Arm64 => ProcessorArchitecture.Arm,
                _ => ProcessorArchitecture.Unknown
            }
        )
    );

    public static async Task<string> GetUrlAsync(UpdateIdentity update)
    {
        await default(_);

        return (await PostAsSoapAsync(string.Format(Resources.GetExtendedUpdateInfo2, update.UpdateID, update.RevisionNumber), true))
       .GetElementsByTagName("Url")
       .Cast<XmlNode>()
       .First(node => node.InnerText.StartsWith("http://tlu.dl.delivery.mp.microsoft.com", StringComparison.Ordinal)).InnerText;
    }

    public static async Task<ReadOnlyCollection<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
    {
        await default(_);

        using StringContent content = new(serializer.Serialize(new Dictionary<string, object>()
        {
            ["IdType"] = "PackageFamilyName",
            ["ProductIds"] = packageFamilyNames
        }), Encoding.UTF8, "application/json");

        using var message = await client.PostAsync(string.Format(requestUri, string.Empty), content);
        message.EnsureSuccessStatusCode();

        return new(Deserialize(await message.Content.ReadAsByteArrayAsync()).GetElementsByTagName("ProductId").Cast<XmlNode>().Select(element => element.InnerText).ToArray());
    }

    public static async Task<ReadOnlyCollection<Product>> GetProductsAsync(params string[] productIds)
    {
        await default(_);

        Product[] products = new Product[productIds.Length];

        for (int i = 0; i < products.Length; i++)
        {
            using var message = await client.GetAsync(string.Format(requestUri, $"/{productIds[i]}"));
            message.EnsureSuccessStatusCode();

            var payload = Deserialize(await message.Content.ReadAsByteArrayAsync())["Payload"];
            var title = payload?["ShortTitle"]?.InnerText;
            var platforms = payload["Platforms"].Cast<XmlNode>().Select(node => node.InnerText);

            products[i] = new(
                string.IsNullOrEmpty(title) ? payload["Title"].InnerText : title,
                (platforms.FirstOrDefault(item => item.Equals(architectures.Native.String, StringComparison.OrdinalIgnoreCase)) ??
                platforms.FirstOrDefault(item => item.Equals(architectures.Compatible.String, StringComparison.OrdinalIgnoreCase)))?.ToLowerInvariant(),
                Deserialize(Encoding.Unicode.GetBytes(payload.GetElementsByTagName("FulfillmentData")[0].InnerText))["WuCategoryId"].InnerText
            );
        }

        return new(products);
    }

    public static async Task<ReadOnlyCollection<UpdateIdentity>> GetUpdatesAsync(Product product)
    {
        await default(_);

        if (product.Architecture is null) return new([]);

        var result = (XmlElement)(await PostAsSoapAsync(
            string.Format(
                content ??= string.Format(Resources.GetString("SyncUpdates.xml"),
                (await PostAsSoapAsync(Resources.GetString("GetCookie.xml"))).GetElementsByTagName("EncryptedData")[0].InnerText, "{0}"),
                product.AppCategoryId), false))
            .GetElementsByTagName("SyncUpdatesResult")[0];

        var nodes = result.GetElementsByTagName("AppxPackageInstallData");
        if (nodes.Count == 0) return new([]);

        ProcessorArchitecture architecture;
        Dictionary<string, Update> dictionary = [];
        foreach (XmlNode node in nodes)
        {
            var element = (XmlElement)node.ParentNode.ParentNode.ParentNode;
            var file = element.GetElementsByTagName("File")[0];
            if (Path.GetExtension(file.Attributes["FileName"].InnerText).StartsWith(".e", StringComparison.Ordinal)) continue;

            var identity = file.Attributes["InstallerSpecificIdentifier"].InnerText.Split('_');
            var neutral = identity[2] == "neutral";
            if (!neutral && identity[2] != architectures.Native.String && identity[2] != architectures.Compatible.String) continue;
            architecture = (neutral ? product.Architecture : identity[2]) switch
            {
                "x86" => ProcessorArchitecture.X86,
                "x64" => ProcessorArchitecture.X64,
                "arm" => ProcessorArchitecture.Arm,
                "arm64" => ProcessorArchitecture.Arm64,
                _ => ProcessorArchitecture.Unknown
            };

            var key = identity[0] + identity[2];
            if (!dictionary.ContainsKey(key)) dictionary.Add(key, new()
            {
                Architecture = architecture,
                PackageFamilyName = $"{identity[0]}_{identity[4]}",
                Version = identity[1],
                MainPackage = node.Attributes["MainPackage"].InnerText == "true"
            });

            var modified = Convert.ToDateTime(file.Attributes["Modified"].InnerText);
            if (dictionary[key].Modified < modified)
            {
                dictionary[key].ID = element["ID"].InnerText;
                dictionary[key].Modified = modified;
            }
        }

        var values = dictionary.Where(item => item.Value.MainPackage).Select(item => item.Value);
        architecture = (
            values.FirstOrDefault(value => value.Architecture == architectures.Native.Architecture) ??
            values.FirstOrDefault(value => value.Architecture == architectures.Compatible.Architecture)
        ).Architecture;
        var items = dictionary.Select(item => item.Value).Where(item => item.Architecture == architecture);

        List<UpdateIdentity> list = [];

        foreach (XmlNode node in result.GetElementsByTagName("SecuredFragment"))
        {
            var element = (XmlElement)node.ParentNode.ParentNode.ParentNode;
            var item = items.FirstOrDefault(item => item.ID == element["ID"].InnerText);
            if (item is null) continue;

            var packages = manager.FindPackagesForUser(string.Empty, item.PackageFamilyName);
            var package = item.MainPackage ? packages.SingleOrDefault() : packages.FirstOrDefault(package => package.Id.Architecture == item.Architecture);
            if (package is null || (!package.IsDevelopmentMode &&
                new Version(item.Version) > new Version(package.Id.Version.Major, package.Id.Version.Minor, package.Id.Version.Build, package.Id.Version.Revision)))
            {
                var attributes = element.GetElementsByTagName("UpdateIdentity")[0].Attributes;
                list.Add(new(attributes["UpdateID"].InnerText, attributes["RevisionNumber"].InnerText, item.MainPackage));
            }
            else if (item.MainPackage) return new([]);
        }

        list.Sort((x, y) => x.MainPackage ? 1 : -1);
        return new(list);
    }

    static XmlElement Deserialize(byte[] buffer)
    {
        using var reader = JsonReaderWriterFactory.CreateJsonReader(buffer, XmlDictionaryReaderQuotas.Max);
        XmlDocument document = new();
        document.Load(reader);
        return document["root"];
    }

    static async Task<XmlDocument> PostAsSoapAsync(string value, bool? _ = null)
    {
        using StringContent content = new(value, Encoding.UTF8, "application/soap+xml");
        using var message = await client.PostAsync(_.HasValue && _.Value ? "secured" : null, content);
        message.EnsureSuccessStatusCode();
        var xml = await message.Content.ReadAsStringAsync();

        XmlDocument document = new();
        document.LoadXml(_.HasValue && !_.Value ? WebUtility.HtmlDecode(xml) : xml);
        return document;
    }
}
