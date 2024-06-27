# StoreEdge
A lightweight .NET Framework library for interacting with the Microsoft Store.

## Usage

```csharp
Task<Store> CreateAsync()
```
- Create a new `Store` instance.

```csharp
 Task<ReadOnlyCollection<IProduct>> GetProductsAsync(params string[] productIds)
```
- Obtains products for the specified product IDs.

```csharp
Task<IReadOnlyCollection<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
```
- Obtains product IDs for the specified package family names.

```csharp
Task<string> GetUrlAsync(IUpdate update)
```
- Obtains the download URL for the specified update identity.

```csharp
Task<ReadOnlyCollection<IUpdate>> SyncUpdatesAsync(IProduct product)
```
- Synchronizes updates for the specified product.

## Example
```csharp
using System;
using System.Threading.Tasks;
using StoreEdge;

static class Program
{
    static async Task Main()
    {
        // Connect to the Microsoft Store.
        var store = await Store.CreateAsync();

        // Get product IDs from package family names.
        var productIds = await store.GetPackageFamilyNamesAsync("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe");

        // Get product information from product IDs.
        var products = await store.GetProductsAsync([.. productIds]);

        foreach (var product in products)
        {
            Console.WriteLine($"Title: {product.Title}");
            Console.WriteLine($"App Category ID: {product.AppCategoryId}\n");

            // Get packages/updates for a product.
            foreach (var update in await store.SyncUpdatesAsync(product))
            {
                // Get a url for a package/update.
                var url = await store.GetUrlAsync(update);

                Console.WriteLine($"\tUpdate ID: {update.UpdateId}");
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