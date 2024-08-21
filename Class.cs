namespace StoreEdge;

using System;
using System.IO;
using System.Xml;
using System.Net;
using System.Text;
using System.Linq;
using Windows.System;
using System.Net.Http;
using System.Xml.Linq;
using System.Threading;
using System.IO.Compression;
using System.Threading.Tasks;
using Windows.System.UserProfile;
using System.Collections.Generic;
using Windows.Management.Deployment;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;

readonly struct Resources
{
    static readonly System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();

    internal static readonly string GetExtendedUpdateInfo2 = GetString("GetExtendedUpdateInfo2.xml.gz");

    internal static string GetString(string name)
    {
        using var _ = assembly.GetManifestResourceStream(name);
        using GZipStream stream = new(_, CompressionMode.Decompress);
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
    internal Product(string title, string architecture, string appCategoryId, string productId)
    {
        Title = title;
        Architecture = architecture;
        AppCategoryId = appCategoryId;
        ProductId = productId;
    }

    public readonly string Title;

    public readonly string Architecture;

    internal readonly string AppCategoryId;

    internal string ProductId;
}

public sealed class UpdateIdentity
{
    internal UpdateIdentity(string updateId, string revisionNumber, bool mainPackage)
    {
        UpdateId = updateId;
        RevisionNumber = revisionNumber;
        MainPackage = mainPackage;
    }

    internal readonly string UpdateId;

    internal readonly string RevisionNumber;

    internal readonly bool MainPackage;
}

class Update
{
    internal string Id;

    internal DateTime Modified;

    internal ProcessorArchitecture Architecture;

    internal string PackageFullName;

    internal string[] PackageIdentity;

    internal string Version;

    internal bool MainPackage;
}

public static class Store
{
    static string _;

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

        return (await PostAsync(string.Format(Resources.GetExtendedUpdateInfo2, update.UpdateId, update.RevisionNumber), true))
        .Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}Url")
        .First(node => node.Value.StartsWith("http://tlu.dl.delivery.mp.microsoft.com", StringComparison.Ordinal)).Value;
    }

    public static async Task<ReadOnlyCollection<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
    {
        await default(_);

        using StringContent content = new($"{{\"IdType\":\"PackageFamilyName\",\"ProductIds\":[{string.Join(",", packageFamilyNames.Select(_ => $"\"{_}\""))}]}}", Encoding.UTF8, "application/json");
        using var message = await client.PostAsync(string.Format(requestUri, string.Empty), content);
        message.EnsureSuccessStatusCode();
        
        return new(Deserialize(await message.Content.ReadAsByteArrayAsync()).Descendants("ProductId").Select(_ => _.Value).ToArray());
    }

    public static async Task<ReadOnlyCollection<Product>> GetProductsAsync(params string[] productIds)
    {
        await default(_);

        Product[] products = new Product[productIds.Length];
        for (int index = 0; index < products.Length; index++)
        {
            var payload = Deserialize(await client.GetByteArrayAsync(string.Format(requestUri, $"/{productIds[index]}"))).Element("Payload");
            var title = payload.Element("ShortTitle")?.Value;
            var platforms = payload.Element("Platforms").Descendants().Select(node => node.Value);

            products[index] = new(
                string.IsNullOrEmpty(title) ? payload.Element("Title").Value : title,
                (
                    platforms.FirstOrDefault(_ => _.Equals(architectures.Native.String, StringComparison.OrdinalIgnoreCase)) ??
                    platforms.FirstOrDefault(_ => _.Equals(architectures.Compatible.String, StringComparison.OrdinalIgnoreCase)))?
                .ToLowerInvariant().Trim(),
                Deserialize(Encoding.Unicode.GetBytes(payload.Descendants("FulfillmentData").First().Value)).Element("WuCategoryId").Value,
                productIds[index]
            );
        }

        return new(products);
    }

    public static async Task<ReadOnlyCollection<UpdateIdentity>> GetUpdatesAsync(Product product)
    {
        await default(_);

        if (product.Architecture is null) return new([]);

        var result = (await PostAsync(string.Format(
            _ ??= string.Format(Resources.GetString("SyncUpdates.xml.gz"),
            (await PostAsync(Resources.GetString("GetCookie.xml.gz"))).Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}EncryptedData").First().Value, "{0}"),
            product.AppCategoryId), false))
            .Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}SyncUpdatesResult").First();

        var elements = result.Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}AppxPackageInstallData");
        if (!elements.Any()) return new([]);

        ProcessorArchitecture architecture;
        Dictionary<string, Update> dictionary = [];
        foreach (var element in elements)
        {
            var parent = element.Parent.Parent.Parent;
            var file = parent.Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}File").First();
            if (Path.GetExtension(file.Attribute("FileName").Value).StartsWith(".e", StringComparison.OrdinalIgnoreCase)) continue;

            var name = file.Attribute("InstallerSpecificIdentifier").Value;
            var identity = name.Split('_');
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
                PackageFullName = name,
                PackageIdentity = identity,
                Version = identity[1],
                MainPackage = element.Attribute("MainPackage").Value == "true"
            });


            var modified = Convert.ToDateTime(file.Attribute("Modified").Value);
            if (dictionary[key].Modified < modified)
            {
                dictionary[key].Id = parent.Element("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}ID").Value;
                dictionary[key].Modified = modified;
            }
        }

        var values = dictionary.Where(_ => _.Value.MainPackage).Select(_ => _.Value);
        var value = values.FirstOrDefault(_ => _.Architecture == architectures.Native.Architecture) ?? values.FirstOrDefault(_ => _.Architecture == architectures.Compatible.Architecture);
        architecture = value.Architecture;

        using var message = await client.GetAsync($"https://displaycatalog.mp.microsoft.com/v7.0/products/{product.ProductId}?languages=iv&market={GlobalizationPreferences.HomeGeographicRegion}");
        message.EnsureSuccessStatusCode();
        var enumerable = Deserialize(
            await message.Content.ReadAsByteArrayAsync())
            .Descendants("FrameworkDependencies")
            .First(_ => _.Parent.Element("PackageFullName").Value == value.PackageFullName)
            .Descendants("PackageIdentity")
            .Select(_ => _.Value);

        var items = dictionary
        .Select(_ => _.Value)
        .Where(_ => _.Architecture == architecture && (_.MainPackage || enumerable.Contains(_.PackageIdentity[0])));

        List<UpdateIdentity> list = [];
        foreach (var element in result.Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}SecuredFragment"))
        {
            var parent = element.Parent.Parent.Parent;
            var item = items.FirstOrDefault(item => item.Id == parent.Element("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}ID").Value);
            if (item is null) continue;

            var package = manager.FindPackagesForUser(string.Empty, $"{item.PackageIdentity[0]}_{item.PackageIdentity[4]}").FirstOrDefault(_ => _.Id.Architecture == item.Architecture || item.MainPackage);
            if (package is null || (!package.IsDevelopmentMode &&
                new Version(item.Version) > new Version(package.Id.Version.Major, package.Id.Version.Minor, package.Id.Version.Build, package.Id.Version.Revision)))
            {
                var identity = parent.Descendants("{http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService}UpdateIdentity").First();
                list.Add(new(identity.Attribute("UpdateID").Value, identity.Attribute("RevisionNumber").Value, item.MainPackage));
            }
            else if (item.MainPackage) return new([]);
        }

        list.Sort((x, y) => x.MainPackage ? 1 : -1);
        return new(list);
    }

    static XElement Deserialize(byte[] buffer)
    {
        using var _ = JsonReaderWriterFactory.CreateJsonReader(buffer, XmlDictionaryReaderQuotas.Max);
        return XDocument.Load(_).Element("root");
    }

    static async Task<XDocument> PostAsync(string value, bool? _ = null)
    {
        using StringContent content = new(value, Encoding.UTF8, "application/soap+xml");
        using var message = await client.PostAsync(_.HasValue && _.Value ? "secured" : null, content);
        message.EnsureSuccessStatusCode();
        var text = await message.Content.ReadAsStringAsync();
        return XDocument.Parse(_.HasValue && !_.Value ? WebUtility.HtmlDecode(text) : text);
    }
}
