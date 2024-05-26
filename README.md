# StoreEdge
A lightweight .NET Framework library for interacting with the Microsoft Store.

## Usage

```csharp
Task<Store> CreateAsync()
```
- Create a new `Store` instance.

```csharp
 Task<IEnumerable<IProduct>> GetProductsAsync(params string[] productIds)
```
- Obtains products for the specified product IDs.

```csharp
Task<IEnumerable<string>> GetPackageFamilyNamesAsync(params string[] packageFamilyNames)
```
- Obtains product IDs for the specified package family names.

```csharp
Task<string> GetUrlAsync(IUpdateIdentity updateIdentity)
```
- Obtains the download URL for the specified update identity.

```csharp
Task<IEnumerable<IUpdateIdentity>> SyncUpdates(IProduct product)
```
- Synchronizes updates for the specified product.

## Building
1. Download the following:
    - [.NET SDK](https://dotnet.microsoft.com/en-us/download)
    - [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net481-developer-pack-offline-installer)

2. Run `dotnet publish` to compile.
    - You can run `dotnet pack` to make a [NuGet](https://www.nuget.org/) package.