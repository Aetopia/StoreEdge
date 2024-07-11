# StoreEdge
A lightweight .NET Framework library for interacting with the Microsoft Store.

## Usage

```csharp
Task<ReadOnlyCollection<Product>> GetProductsAsync(params string[] productIds)
```
- Obtains products for the specified product IDs.

```csharp
Task<ReadOnlyCollection<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
```
- Obtains product IDs for the specified package family names.

```csharp
Task<string> GetUrlAsync(UpdateIdentity update)
```
- Obtains the download URL for the specified update identity.

```csharp
Task<ReadOnlyCollection<UpdateIdentity>> GetUpdatesAsync(Product product)
```
- Gets updates for the specified product.

## Example
```csharp
using System;
using System.Threading.Tasks;
using StoreEdge;

static class Program
{
    static async Task Main()
    {
        // Get product IDs from package family names.
        var productIds = await Store.GetPackageFamilyNamesAsync(
            "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
            "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe");

        // Get product information from product IDs.
        var products = await Store.GetProductsAsync([.. productIds]);

        foreach (var product in products)
        {
            // Verify if a product supports an installed operating system's architecture.
            var supported = product.Architecture is not null;

            Console.WriteLine($"Title: {product.Title}");
            Console.WriteLine($"Supported: {supported}\n");

            // Get packages/updates for a product.
            foreach (var update in await Store.GetUpdatesAsync(product))
            {
                // Get a url for a package/update.
                var url = await Store.GetUrlAsync(update);

                Console.WriteLine($"\tUpdate Hash Code: {update.GetHashCode()}");
                Console.WriteLine($"\tUrl: {url}\n");
            }
        }
    }
}
```

## Building
1. Download the following:
    - [.NET SDK](https://dotnet.microsoft.com/en-us/download)
    - [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net481-developer-pack-offline-installer)

2. Run `dotnet publish` to compile & make a [NuGet](https://www.nuget.org/) package.