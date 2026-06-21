using Cryville.EEW.ComponentModel;
using Cryville.EEW.Extensions;
using Cryville.Packages;
using Microsoft.Extensions.DependencyModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace Cryville.EEW.ExtensionRepo {
	class Program {
		static readonly JsonSerializerOptions _jsonSerializerOptions = new() {
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		};
		static readonly Resolver _resolver = new();

		static readonly HashSet<string> _summaryMetadataKeys = ["displayName", "description", "authors"];

		static async Task Main(string[] args) {
			Trace.Listeners.Add(new ConsoleTraceListener());
			var workingDir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
			string outDir = Path.Combine(workingDir, "out");
			IOHelpers.CopyDirectoryOverriddingOlder(Path.Combine(workingDir, "template"), outDir, true);
			string outAssetsDir = Path.Combine(outDir, "public");
			await ProcessPackagesAsync(new(Path.Combine(workingDir, "extensions")), Directory.CreateDirectory(outAssetsDir));
		}

		static async Task ProcessPackagesAsync(DirectoryInfo inExtensionsDirInfo, DirectoryInfo outAssetsDirInfo) {
			var packagesFileInfo = new FileInfo(Path.Combine(outAssetsDirInfo.FullName, "packages.json"));
			PackageInfoCollection packages;
			if (packagesFileInfo.Exists) {
				using var oldPackagesFileStream = new FileStream(packagesFileInfo.FullName, FileMode.Open, FileAccess.Read);
				packages = await JsonSerializer.DeserializeAsync<PackageInfoCollection>(oldPackagesFileStream, _jsonSerializerOptions) ?? [];
			}
			else {
				packages = [];
			}
			foreach (var extDirInfo in inExtensionsDirInfo.EnumerateDirectories()) {
				string packageName = extDirInfo.Name;
				var changedPlatInfos = await ProcessPackageAsync(extDirInfo, new(Path.Combine(outAssetsDirInfo.FullName, packageName)), packageName);
				if (changedPlatInfos.Count == 0)
					continue;
				if (!packages.TryGetValue(packageName, out var packageInfo))
					packageInfo = new(packageName, []);
				foreach (var platInfo in changedPlatInfos) {
					packageInfo.Platforms.Remove(platInfo.Name);
					packageInfo.Platforms.Add(platInfo with { Versions = null });
				}
				packages.Remove(packageName);
				packages.Add(packageInfo);
			}
			using var packagesFileStream = new FileStream(packagesFileInfo.FullName, FileMode.Create, FileAccess.Write);
			await JsonSerializer.SerializeAsync(packagesFileStream, packages, _jsonSerializerOptions);
		}

		static async Task<IReadOnlyCollection<PlatformPackageInfo>> ProcessPackageAsync(DirectoryInfo extDirInfo, DirectoryInfo outExtDirInfo, string packageName) {
			Collection<PlatformPackageInfo> changedPlatInfos = [];
			foreach (var platDirInfo in extDirInfo.EnumerateDirectories()) {
				if (await ProcessPlatformPackageAsync(platDirInfo, new(Path.Combine(outExtDirInfo.FullName, platDirInfo.Name)), packageName, platDirInfo.Name) is not { } platInfo)
					continue;
				changedPlatInfos.Add(platInfo);
			}
			return changedPlatInfos;
		}

		static async Task<PlatformPackageInfo?> ProcessPlatformPackageAsync(DirectoryInfo platDirInfo, DirectoryInfo outPlatDirInfo, string packageName, string platformName) {
			List<VersionInfo>? addedVersionInfos = null;
			VersionInfo? addedLatestVersionInfo = null;
			foreach (var versionDirInfo in platDirInfo.EnumerateDirectories()) {
				if (await ProcessVersionAsync(versionDirInfo, new(Path.Combine(outPlatDirInfo.FullName, versionDirInfo.Name)), packageName, platformName, versionDirInfo.Name) is not { } versionInfo)
					continue;
				addedVersionInfos ??= [];
				addedVersionInfos.Add(versionInfo);
				if (addedLatestVersionInfo == null || new Version(versionInfo.Name) > new Version(addedLatestVersionInfo.Name))
					addedLatestVersionInfo = versionInfo;
			}
			if (addedVersionInfos == null)
				return null;
			var platInfoFileInfo = new FileInfo(Path.Combine(outPlatDirInfo.FullName, "platform.json"));
			PlatformPackageInfo? oldPlatInfo = null;
			if (platInfoFileInfo.Exists) {
				Trace.TraceInformation("Processing changes to {0} ({1})", packageName, platformName);
				using var oldPlatInfoFileStream = new FileStream(platInfoFileInfo.FullName, FileMode.Open, FileAccess.Read);
				oldPlatInfo = await JsonSerializer.DeserializeAsync<PlatformPackageInfo>(oldPlatInfoFileStream, _jsonSerializerOptions);
			}
			else {
				Trace.TraceInformation("New platform package {0} ({1})", packageName, platformName);
			}

			var oldVersions = oldPlatInfo?.Versions ?? [];
			var versions = oldVersions.Concat(addedVersionInfos.Select(v => v.Name)).Select(v => new Version(v)).OrderDescending().Select(v => v.ToString()).ToList();
			var latestVersion = oldPlatInfo?.LatestVersion;
			if (addedLatestVersionInfo != null && (latestVersion == null || new Version(addedLatestVersionInfo.Name) > new Version(latestVersion.Name)))
				latestVersion = addedLatestVersionInfo;
			if (latestVersion == null || versions.Count == 0)
				return null;
			var latestVersionSummary = latestVersion with {
				Metadata = latestVersion.Metadata.Where(m => _summaryMetadataKeys.Contains(m.Key)).ToDictionary(),
				Resources = null,
				Dependencies = null,
				PackedDependencies = null,
			};
			var platInfo = new PlatformPackageInfo(platformName, latestVersionSummary, versions);
			using var platInfoFileStream = new FileStream(platInfoFileInfo.FullName, FileMode.Create, FileAccess.Write);
			await JsonSerializer.SerializeAsync(platInfoFileStream, platInfo, _jsonSerializerOptions);
			return platInfo;
		}

		static async Task<VersionInfo?> ProcessVersionAsync(DirectoryInfo versionDirInfo, DirectoryInfo outVersionDirInfo, string packageName, string platformName, string versionName) {
			if (outVersionDirInfo.Exists)
				return null;
			Trace.TraceInformation("Processing {0} ({1}, {2})", packageName, platformName, versionName);
			outVersionDirInfo.Create();
			var depFiles = versionDirInfo.GetFiles("*.deps.json");
			if (depFiles.Length == 0)
				throw new FileNotFoundException("No extension found in the directory.");
			if (depFiles.Length > 1)
				throw new InvalidOperationException("The directory contains multiple extensions. A directory must contain only one extension.");
			var depFile = depFiles[0];
			string expectedAssemblyName = depFile.Name[..^10];
			if (expectedAssemblyName != packageName)
				throw new InvalidOperationException("The file name of the extension mismatches with its assembly name.");
			string dllFile = Path.Combine(versionDirInfo.FullName, expectedAssemblyName + ".dll");
			if (!File.Exists(dllFile))
				throw new FileNotFoundException("No extension found in the directory.");
			var customInfo = new CustomExtensionInfo(versionDirInfo);
			var extensionInfo = await _resolver.ResolveDllAsync(dllFile, versionDirInfo, expectedAssemblyName, customInfo, CancellationToken.None).ConfigureAwait(true);
			if (extensionInfo.Version?.ToString() != versionName)
				throw new InvalidOperationException("The assembly version mismatches with the version declared.");

			using var depsFileReader = new DependencyContextJsonReader();
			using var depsFileStream = new FileStream(depFile.FullName, FileMode.Open, FileAccess.Read);
			var depsContext = depsFileReader.Read(depsFileStream);
			var referencedAssemblies = extensionInfo.ReferencedAssemblies;
			var dependencies = new DependencyInfoCollection();
			foreach (var d in depsContext.RuntimeLibraries.Single(l => l.Type == "project").Dependencies) {
				if (referencedAssemblies.SingleOrDefault(a => a.Name == d.Name) is { } referencedAssembly) {
					dependencies.Add(new(
						referencedAssembly.Name ?? throw new InvalidOperationException("Unexpected reference to an unnamed assembly."),
						referencedAssembly.Version?.ToString() ?? throw new InvalidOperationException("Unexpected reference to an assembly without version.")
					));
				}
				else {
					Trace.TraceWarning("{0} ({1}) has a reference to {2} ({3}) but does not have a corresponding assembly reference. This reference is probably not used. Inferring assembly version.", extensionInfo.AssemblyName, extensionInfo.Version, d.Name, d.Version);
					dependencies.Add(new DependencyInfo(d.Name, InferNormalizedVersion(d.Version)));
				}
			}

			var packedDependecies = new HashSet<string>();
			foreach (var dependency in dependencies) {
				string id = dependency.Id;
				if (File.Exists(Path.Combine(versionDirInfo.FullName, id + ".dll"))) {
					packedDependecies.Add(id);
				}
			}

			using var fullPackageStream = new FileStream(Path.Combine(outVersionDirInfo.FullName, ".zip"), FileMode.Create, FileAccess.Write);
			ZipFile.CreateFromDirectory(versionDirInfo.FullName, fullPackageStream);

			var versionInfo = new VersionInfo(
				extensionInfo.Version.ToString(),
				customInfo.Metadata,
				[new FullResourceInfo(new(".zip", UriKind.Relative), fullPackageStream.Position)],
				[.. dependencies],
				packedDependecies.Count != 0 ? packedDependecies : null
			);
			using var versionInfoFile = new FileStream(Path.Combine(outVersionDirInfo.FullName, "version.json"), FileMode.Create, FileAccess.Write);
			await JsonSerializer.SerializeAsync(versionInfoFile, versionInfo, _jsonSerializerOptions);
			return versionInfo;
		}
		static string InferNormalizedVersion(string version) {
			var parsedVersion = new Version(version);
			int build = parsedVersion.Build;
			int revision = parsedVersion.Revision;
			return new Version(
				parsedVersion.Major,
				parsedVersion.Minor,
				build != -1 ? build : 0,
				revision != -1 ? revision : 0
			).ToString();
		}

		sealed class Resolver : ExtensionResolver {
			protected override void ResolveCustomInfo(MetadataLoadContext context, Assembly assembly, object? customInfo) {
				if (customInfo is not CustomExtensionInfo customExtensionInfo)
					return;
				var attrData = assembly.GetCustomAttributesData();

				var displayNameAttribute = GetAttrbute<DisplayNameAttribute>(attrData, context);
				var assemblyTitleAttribute = GetAttrbute<AssemblyTitleAttribute>(attrData, context);
				CollectMetadata(
					customExtensionInfo, "displayName",
					(Attribute?)displayNameAttribute ?? assemblyTitleAttribute,
					displayNameAttribute?.DisplayName ?? assemblyTitleAttribute?.Title ?? assembly.FullName
				);

				var descriptionAttribute = GetAttrbute<DescriptionAttribute>(attrData, context);
				var assemblyDescriptionAttribute = GetAttrbute<AssemblyDescriptionAttribute>(attrData, context);
				CollectMetadata(
					customExtensionInfo, "description",
					(Attribute?)descriptionAttribute ?? assemblyDescriptionAttribute,
					descriptionAttribute?.Description ?? assemblyDescriptionAttribute?.Description
				);

				var assemblyCompanyAttribute = GetAttrbute<AssemblyCompanyAttribute>(attrData, context);
				CollectMetadata(
					customExtensionInfo, "authors",
					assemblyCompanyAttribute,
					assemblyCompanyAttribute?.Company
				);
			}
			static void CollectMetadata(CustomExtensionInfo customExtensionInfo, string metadataKey, Attribute? attribute, string? fallbackValue) {
				if (CollectMetadataInternal(customExtensionInfo, attribute) is { } collection) {
					customExtensionInfo.Metadata.Add(metadataKey, collection);
				}
				else if (fallbackValue != null) {
					customExtensionInfo.Metadata.Add(metadataKey, [new("und", fallbackValue)]);
				}
			}
			static LocalizedMetadataValueCollection? CollectMetadataInternal(CustomExtensionInfo customExtensionInfo, Attribute? attribute) {
				if (attribute is not ILocalizableMetadataAttribute localizableMetadataAttribute) {
					return null;
				}
				var messagesDirInfo = new DirectoryInfo(Path.Combine(customExtensionInfo.VersionDirInfo.FullName, "Messages", localizableMetadataAttribute.Type));
				if (!messagesDirInfo.Exists)
					return null;
				var availableCultures = messagesDirInfo.GetFiles("*.json").Select(f => SharedCultures.Get(f.Name[..^5])).ToArray();
				if (availableCultures.Length == 0)
					return null;
				var collection = new LocalizedMetadataValueCollection();
				foreach (var culture in availableCultures) {
					var metadataCulture = culture;
					var metadataValue = localizableMetadataAttribute.GetLocalizedValue(ref metadataCulture);
					collection.Add(new(metadataCulture.Name, metadataValue));
				}
				return collection;
			}
		}
		sealed class CustomExtensionInfo {
			public DirectoryInfo VersionDirInfo { get; }

			public CustomExtensionInfo(DirectoryInfo versionDirInfo) {
				VersionDirInfo = versionDirInfo;
			}

			public Dictionary<string, LocalizedMetadataValueCollection> Metadata { get; } = [];
		}
	}
}
