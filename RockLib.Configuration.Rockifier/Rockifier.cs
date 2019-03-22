using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace RockLib.Configuration.Editor
{
    public sealed class Rockifier
    {
        private readonly HttpClient _client = new HttpClient();

        private readonly ConcurrentDictionary<string, PackageAssemblies> _assemblyCache = new ConcurrentDictionary<string, PackageAssemblies>();

        private readonly string _debugProfile;

        public Rockifier(string debugProfile = null)
        {
            _debugProfile = debugProfile;
        }

        public ConfigInfo Load(string projectPath = null)
        {
            projectPath = string.IsNullOrEmpty(projectPath) ? Environment.CurrentDirectory : projectPath;

            if (Directory.Exists(projectPath))
            {
                var csprojFiles = Directory.GetFiles(projectPath, "*.csproj");
                if (csprojFiles.Length != 1)
                    return ConfigInfo.Empty;

                projectPath = csprojFiles[0];
            }

            if (!File.Exists(projectPath))
                return ConfigInfo.Empty;

            // TODO: If not a .csproj file, we have a problem

            var allPackageAssemblies = LoadPackageAssemblies(projectPath).GetAwaiter().GetResult();

            var sections = GetSections(allPackageAssemblies);
            var usedDocumentation = new HashSet<XDocument>();
            var implementations = GetImplementations(sections.Select(GetTargetType), allPackageAssemblies, usedDocumentation);
            var documentation = GetDocumentation(implementations, usedDocumentation);
            var settings = GetSettings(projectPath, sections.Keys);

            return new ConfigInfo(sections, implementations, documentation, settings);
        }

        private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetSettings(string projectPath, IEnumerable<string> sections)
        {
            var settings = new Dictionary<string, IReadOnlyDictionary<string, string>>();

            if (IsOldSchoolCsproj(projectPath))
            {
                AddAppConfigWebConfig(projectPath, settings, sections);
                AddAppsettingsJson(projectPath, settings, sections);
            }
            else
            {
                AddLaunchSettingsJsonEnvironmentVariables(projectPath, settings, sections);
                AddLaunchJsonEnvironmentVariables(projectPath, settings, sections);
                AddAppsettingsJson(projectPath, settings, sections);
            }

            return settings;
        }

        private void AddAppsettingsJson(string projectPath, Dictionary<string, IReadOnlyDictionary<string, string>> settings, IEnumerable<string> sections)
        {
            var appsettingsPath = Path.Combine(Path.GetDirectoryName(projectPath), "appsettings.json");
            if (!File.Exists(appsettingsPath))
                return;

            Dictionary<string, string> appsettingsJson;

            if (settings.TryGetValue("appsettings.json", out var s))
                appsettingsJson = (Dictionary<string, string>)s;
            else
            {
                appsettingsJson = new Dictionary<string, string>();
                settings.Add("appsettings.json", appsettingsJson);
            }

            var json = JObject.Parse(File.ReadAllText(appsettingsPath));

            foreach (var property in json.Properties())
                AddAppsettingsJson("", property, appsettingsJson, sections);

            if (appsettingsJson.Count == 0)
                settings.Remove("appsettings.json");
        }

        private void AddAppsettingsJson(string settingPath, JToken token, Dictionary<string, string> appsettingsJson, IEnumerable<string> sections)
        {
            switch (token)
            {
                case JProperty property:
                    AddAppsettingsJson(Append(settingPath, property.Name), property.Value, appsettingsJson, sections);
                    break;
                case JObject json:
                    foreach (var property in json.Properties())
                        AddAppsettingsJson(settingPath, property, appsettingsJson, sections);
                    break;
                case JValue value:
                    AddSetting(appsettingsJson, settingPath, GetValue(value), sections);
                    break;
                case JArray array:
                    foreach (var item in array)
                        AddAppsettingsJson(settingPath, item, appsettingsJson, sections);
                    break;
            }
        }

        private static string Append(string lhs, string rhs)
        {
            if (string.IsNullOrEmpty(lhs))
                return rhs;
            return $"{lhs}:{rhs}";
        }

        private static string GetValue(JValue value)
        {
            switch (value.Value)
            {
                case string str:
                    return str;
                case bool b:
                    return b ? "true" : "false";
                default:
                    return value.Value.ToString();
            }
        }

        private void AddLaunchJsonEnvironmentVariables(string projectPath, Dictionary<string, IReadOnlyDictionary<string, string>> settings, IEnumerable<string> sections)
        {
            // TODO: Implement for VS Code support.
        }

        private void AddLaunchSettingsJsonEnvironmentVariables(string projectPath, Dictionary<string, IReadOnlyDictionary<string, string>> settings, IEnumerable<string> sections)
        {
            var launchSettingsPath = Path.Combine(Path.GetDirectoryName(projectPath), "Properties", "launchSettings.json");
            if (!File.Exists(launchSettingsPath))
                return;

            var json = JObject.Parse(File.ReadAllText(launchSettingsPath));
            var profiles = json["profiles"] as JObject;
            if (profiles == null || !profiles.HasValues)
                return;

            JObject profileObject;
            string profilePropertyName;

            if (_debugProfile == null)
            {
                var profileProperty = profiles.First as JProperty;
                if (profileProperty == null)
                    return;
                profileObject = profileProperty.Value as JObject;
                profilePropertyName = profileProperty.Name;
            }
            else
            {
                profileObject = profiles[_debugProfile] as JObject;
                profilePropertyName = _debugProfile;
            }

            if (profileObject == null)
                return;
            var environmentVariablesObject = profileObject["environmentVariables"] as JObject;
            if (environmentVariablesObject == null)
                return;

            Dictionary<string, string> environmentVariables;

            if (settings.TryGetValue(profilePropertyName, out var s))
                environmentVariables = (Dictionary<string, string>)s;
            else
            {
                environmentVariables = new Dictionary<string, string>();
                settings.Add("environmentVariables", environmentVariables);
            }

            foreach (var property in environmentVariablesObject.Properties())
                AddSetting(environmentVariables, property.Name, (string)property.Value, sections);
        }

        private void AddAppConfigWebConfig(string projectPath, Dictionary<string, IReadOnlyDictionary<string, string>> settings, IEnumerable<string> sections)
        {
            // TODO: Implement for old-school project support in VS.
        }

        private static void AddSetting(Dictionary<string, string> settingGroup, string name, string value, IEnumerable<string> sections)
        {
            if (sections.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                settingGroup.Add(name, value);
        }

        private IReadOnlyDictionary<MemberInfo, MemberDocumentation> GetDocumentation(IReadOnlyCollection<Type> implementations, IReadOnlyCollection<XDocument> usedDocumentation)
        {
            var documentation = new Dictionary<MemberInfo, MemberDocumentation>();

            var stuff = implementations.Distinct();

            var xmlNamesList = implementations.Select(i => (GetXmlName(i), (MemberInfo)i))
                .Concat(implementations.SelectMany(i => i.GetConstructors(), (x, y) => (GetXmlName(y), (MemberInfo)y)))
                .Concat(implementations.SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite), (x, y) => (GetXmlName(y), (MemberInfo)y)))
                .ToList();

            var xmlNames = new Dictionary<string, MemberInfo>();
            foreach (var xmlName in xmlNamesList)
                if (!xmlNames.ContainsKey(xmlName.Item1))
                    xmlNames.Add(xmlName.Item1, xmlName.Item2);

            foreach (var doc in usedDocumentation)
            {
                foreach (var memberElement in doc.Root.Element("members").Elements("member"))
                {
                    var xmlName = memberElement.Attribute("name").Value;
                    if (xmlNames.TryGetValue(xmlName, out var member))
                        documentation.Add(member, new MemberDocumentation(memberElement));
                }
            }

            return documentation;
        }

        private static string GetXmlName(MemberInfo member)
        {
            switch (member)
            {
                case Type type:
                    return $"T:{GetXmlTypeName(type)}";
                case ConstructorInfo constructor:
                    return $"M:{GetXmlTypeName(constructor.DeclaringType)}.#ctor({string.Join(",", constructor.GetParameters().Select(p => GetXmlTypeName(p.ParameterType)))})";
                case PropertyInfo property:
                    return $"P:{GetXmlTypeName(property.DeclaringType)}.{property.Name}";
                case FieldInfo field:
                    return $"F:{GetXmlTypeName(field.DeclaringType)}.{field.Name}";
                case MethodInfo method:
                    return $"M:{GetXmlTypeName(method.DeclaringType)}.{method.Name}({string.Join(",", method.GetParameters().Select(p => GetXmlTypeName(p.ParameterType)))})";
                case EventInfo @event:
                    return $"E:{GetXmlTypeName(@event.DeclaringType)}.{@event.Name}";
                default:
                    throw new NotSupportedException($"Unknown MemberInfo type: {member.GetType()}");
            }
        }

        private static string GetXmlTypeName(Type type)
        {
            if (!type.IsGenericType)
                return type.FullName;

            var typeArguments = type.GetGenericArguments();
            var typeName = type.Name.Substring(0, type.Name.IndexOf('`'));
            return $"{type.Namespace}.{typeName}{{{string.Join(",", typeArguments.Select(t => GetXmlTypeName(t)))}}}";
        }

        private static Type GetTargetType(KeyValuePair<string, Type> item) =>
            IsListType(item.Value, out var itemType) ? itemType : item.Value;

        private static bool IsListType(Type type, out Type itemType)
        {
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                {
                    itemType = null;
                    return false;
                }

                itemType = type.GetElementType();
                return true;
            }

            if (type.IsGenericType)
            {
                var typeDefinition = type.GetGenericTypeDefinition();
                if (typeDefinition == typeof(List<>)
                    || typeDefinition == typeof(IList<>)
                    || typeDefinition == typeof(ICollection<>)
                    || typeDefinition == typeof(IEnumerable<>)
                    || typeDefinition == typeof(IReadOnlyList<>)
                    || typeDefinition == typeof(IReadOnlyCollection<>))
                {
                    itemType = type.GetGenericArguments()[0];
                    return true;
                }
            }

            itemType = null;
            return false;
        }

        private IReadOnlyList<Type> GetImplementations(IEnumerable<Type> targetTypes,
            IReadOnlyCollection<PackageAssemblies> allPackageAssemblies, HashSet<XDocument> usedDocumentation)
        {
            var implementations = new List<Type>();
            var processedTargetTypes = new HashSet<Type>();

            while (true)
            {
                var additionalTargetTypes = new HashSet<Type>();

                AddImplementations(targetTypes, allPackageAssemblies, implementations, additionalTargetTypes, processedTargetTypes, usedDocumentation);

                if (additionalTargetTypes.Count == 0)
                    break;

                targetTypes = additionalTargetTypes;
            }

            return implementations;
        }

        private void AddImplementations(IEnumerable<Type> targetTypes, IReadOnlyCollection<PackageAssemblies> allPackageAssemblies,
            List<Type> implementations, HashSet<Type> additionalTargetTypes, HashSet<Type> processedTargetTypes, HashSet<XDocument> usedDocumentation)
        {
            foreach (var assemblyInfo in allPackageAssemblies.SelectMany(x => x.AssemblyInfos))
            {
                foreach (var assemblyType in assemblyInfo.Assembly.GetTypes().Where(t => t.IsPublic))
                {
                    foreach (var targetType in targetTypes)
                    {
                        processedTargetTypes.Add(targetType);

                        if (targetType.IsAssignableFrom(assemblyType) && !assemblyType.IsAbstract)
                        {
                            implementations.Add(assemblyType);

                            if (assemblyInfo.Documentation != null)
                                usedDocumentation.Add(assemblyInfo.Documentation);

                            foreach (var parameter in assemblyType.GetConstructors().SelectMany(c => c.GetParameters()))
                                if (parameter.ParameterType.IsAbstract && !processedTargetTypes.Contains(parameter.ParameterType))
                                    additionalTargetTypes.Add(IsListType(parameter.ParameterType, out var itemType) ? itemType : parameter.ParameterType);

                            foreach (var property in assemblyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                if (property.PropertyType.IsAbstract && property.CanRead && property.CanWrite && !processedTargetTypes.Contains(property.PropertyType))
                                    additionalTargetTypes.Add(IsListType(property.PropertyType, out var itemType) ? itemType : property.PropertyType);
                        }
                    }
                }
            }
        }

        private async Task<IReadOnlyCollection<PackageAssemblies>> LoadPackageAssemblies(string path)
        {
            var packageAssemblies = new ConcurrentDictionary<string, PackageAssemblies>();
            var tasks = new List<Task>();

            foreach (var package in GetPackagesFromCsproj(path))
                tasks.Add(LoadPackage(package, packageAssemblies));

            await Task.WhenAll(tasks);
            return packageAssemblies.Values.ToList();
        }

        private IReadOnlyDictionary<string, Type> GetSections(IReadOnlyCollection<PackageAssemblies> allPackageAssemblies)
        {
            var sections = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            var configSectionAttributes =
                from packageAssemblies in allPackageAssemblies
                from assemblyInfo in packageAssemblies.AssemblyInfos
                from customAttribute in assemblyInfo.Assembly.CustomAttributes
                where customAttribute.AttributeType.Name == "ConfigSectionAttribute"
                    && customAttribute.ConstructorArguments != null
                    && customAttribute.ConstructorArguments.Count == 2
                    && customAttribute.ConstructorArguments[0].ArgumentType.FullName == "System.String"
                    && customAttribute.ConstructorArguments[1].ArgumentType.FullName == "System.Type"
                select customAttribute;

            foreach (var configSectionAttribute in configSectionAttributes)
            {
                try
                {
                    var path = (string)configSectionAttribute.ConstructorArguments[0].Value;
                    var type = (Type)configSectionAttribute.ConstructorArguments[1].Value;
                    sections.Add(path, type);
                }
                catch
                {
                }
            }

            return sections;
        }

        private static bool IsOldSchoolCsproj(string path)
        {
            var doc = XDocument.Load(path);
            var ns = doc.Root.GetDefaultNamespace();
            return ns == "http://schemas.microsoft.com/developer/msbuild/2003";
        }

        private IEnumerable<Package> GetPackagesFromCsproj(string path)
        {
            XPathNavigator navigator;
            XmlDocument doc;
            XPathExpression projectReferenceXPath;

            try
            {
                doc = new XmlDocument();
                doc.Load(path);
                navigator = doc.CreateNavigator();
            }
            catch
            {
                yield break;
            }

            if (doc.DocumentElement.NamespaceURI == "http://schemas.microsoft.com/developer/msbuild/2003")
            {
                var namespaceManager = new XmlNamespaceManager(new NameTable());
                namespaceManager.AddNamespace("ms", "http://schemas.microsoft.com/developer/msbuild/2003");
                projectReferenceXPath = navigator.Compile("/ms:Project/ms:ItemGroup/ms:ProjectReference/@Include");
                projectReferenceXPath.SetContext(namespaceManager);

                foreach (var package in GetPackagesFromProjectReferences())
                    yield return package;

                var dir = Path.GetDirectoryName(path);
                var packagesConfig = Path.Combine(dir, "packages.config");
                if (File.Exists(packagesConfig))
                    foreach (var package in GetPackagesFromPackagesConfig(packagesConfig))
                        yield return package;
            }
            else
            {
                projectReferenceXPath = navigator.Compile("/Project/ItemGroup/ProjectReference/@Include");

                foreach (var package in GetPackagesFromProjectReferences())
                    yield return package;

                foreach (XPathNavigator match in navigator.Select("/Project/ItemGroup/PackageReference"))
                    yield return new Package(match.GetAttribute("Include", ""), match.GetAttribute("Version", ""), true);
            }

            IEnumerable<Package> GetPackagesFromProjectReferences()
            {
                foreach (XPathNavigator match in navigator.Select(projectReferenceXPath))
                {
                    var dir = Path.GetDirectoryName(path);
                    var csproj = Path.Combine(dir, match.Value);
                    foreach (var package in GetPackagesFromCsproj(csproj))
                        yield return package;
                }
            }
        }

        private IEnumerable<Package> GetPackagesFromPackagesConfig(string path)
        {
            XPathNavigator navigator;

            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                navigator = doc.CreateNavigator();
            }
            catch
            {
                yield break;
            }

            foreach (XPathNavigator match in navigator.Select("/packages/package"))
                yield return new Package(match.GetAttribute("id", ""), match.GetAttribute("version", ""), false);
        }

        private async Task LoadPackage(Package package, ConcurrentDictionary<string, PackageAssemblies> packageAssemblies)
        {
            if (_assemblyCache.TryGetValue(package.GetKey(), out var item))
            {
                packageAssemblies.TryAdd(package.GetKey(), item);
                return;
            }

            var nuspecDependencies = new Dictionary<string, string>();

            try
            {
                using (var archive = await GetPackageArchive(package))
                {
                    var assemblyCandidates = new Dictionary<string, List<ZipArchiveEntry>>();

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("lib/"))
                        {
                            if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                || entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                            {
                                var key = GetKey(entry.FullName);
                                if (key != null)
                                {
                                    if (!assemblyCandidates.TryGetValue(key, out var candidates))
                                    {
                                        candidates = new List<ZipArchiveEntry>();
                                        assemblyCandidates.Add(key, candidates);
                                    }
                                    candidates.Add(entry);
                                }
                            }
                        }
                        else if (package.Recurse && entry.FullName.EndsWith(".nuspec"))
                        {
                            using (var stream = entry.Open())
                            {
                                var doc = XDocument.Load(stream);
                                var ns = doc.Root.Name.Namespace;
                                var dependenciesElement = doc.Root.Element(ns + "metadata")?.Element(ns + "dependencies");
                                if (dependenciesElement != null)
                                {
                                    foreach (var child in dependenciesElement.Elements())
                                    {
                                        // TODO: pick the "best" group by target framework (use candidates similar to above).
                                        switch (child.Name.LocalName)
                                        {
                                            case "group":
                                                foreach (var dependencyElement in child.Elements(ns + "dependency"))
                                                {
                                                    var key = dependencyElement.Attribute("id").Value;
                                                    var value = Regex.Replace(dependencyElement.Attribute("version")?.Value ?? "0", @"[\[\]()]", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)[0];
                                                    nuspecDependencies[key] = value;
                                                }
                                                break;
                                            case "dependency":
                                                {
                                                    var key = child.Attribute("id").Value;
                                                    var value = Regex.Replace(child.Attribute("version")?.Value ?? "0", @"[\[\]()]", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)[0];
                                                    nuspecDependencies[key] = value;
                                                    break;
                                                }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    var entries = assemblyCandidates.OrderBy(c => c.Key, TargetFrameworkComparer.Instance).FirstOrDefault().Value;

                    if (entries == null)
                        return;

                    var assemblyEntries = entries.Where(e =>
                        e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                    var documentationEntries = entries.Where(e =>
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();

                    var assemblyInfos = new List<AssemblyInfo>();

                    foreach (var assemblyEntry in assemblyEntries)
                    {
                        // TODO: Might not need the memory stream copying here.
                        var memoryStream = new MemoryStream((int)assemblyEntry.Length);
                        using (var zipStream = assemblyEntry.Open())
                        {
                            zipStream.CopyTo(memoryStream);
                            memoryStream.Position = 0;
                        }

                        var assembly = Assembly.Load(memoryStream.ToArray());
                        XDocument documentation = null;

                        var fileName = Path.GetFileNameWithoutExtension(assemblyEntry.FullName);
                        var documentationEntry = documentationEntries.FirstOrDefault(e => fileName == Path.GetFileNameWithoutExtension(e.FullName));

                        if (documentationEntry != null)
                        {
                            using (var stream = documentationEntry.Open())
                            {
                                try { documentation = XDocument.Load(stream); }
                                catch { }
                            }
                        }

                        assemblyInfos.Add(new AssemblyInfo(assembly, documentation));
                    }

                    var packageAssembly = new PackageAssemblies(package, assemblyInfos);
                    _assemblyCache.TryAdd(packageAssembly.Package.GetKey(), packageAssembly);
                    packageAssemblies.TryAdd(packageAssembly.Package.GetKey(), packageAssembly);
                }
            }
            catch
            {
            }

            // TODO: Don't blindly pick the last dependency in the archive entries, pick the best version.
            foreach (var dependency in nuspecDependencies)
            {
                var dependencyPackage = new Package(dependency.Key, dependency.Value, true);
                await LoadPackage(dependencyPackage, packageAssemblies);
            }
        }

        private string GetKey(string entryFullName)
        {
            var split = entryFullName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            switch (split.Length)
            {
                case 3:
                    return split[1];
                default:
                    return "net";
            }
        }

        private async Task<ZipArchive> GetPackageArchive(Package package)
        {
            // TODO: Make the package source(s) configurable.
            return GetPackageArchiveFromCache(package)
                ?? await GetPackageArchiveFromNugetOrg(package);
        }

        private ZipArchive GetPackageArchiveFromCache(Package package)
        {
            try
            {
                var path = GetCachePath(package);

                if (!File.Exists(path))
                    return null;

                var fileStream = File.Open(path, FileMode.Open);
                return new ZipArchive(fileStream, ZipArchiveMode.Read);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private Task<ZipArchive> GetPackageArchiveFromNugetOrg(Package package)
        {
            var url = $"https://www.nuget.org/api/v2/package/{Uri.EscapeDataString(package.Id)}/{Uri.EscapeDataString(package.Version)}";
            return GetPackageArchiveFromNuget(package, url);
        }

        private async Task<ZipArchive> GetPackageArchiveFromNuget(Package package, string url)
        {
            try
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                {
                    var memoryStream = new MemoryStream();
                    await contentStream.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;

                    var path = GetCachePath(package);

                    if (!Directory.Exists(Path.GetDirectoryName(path)))
                        Directory.CreateDirectory(Path.GetDirectoryName(path));

                    using (var fileStream = File.Open(path, FileMode.Create))
                        await memoryStream.CopyToAsync(fileStream);

                    memoryStream.Position = 0;
                    return new ZipArchive(memoryStream);
                }
            }
            catch
            {
                return null;
            }
        }

        private string GetCachePath(Package package) =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RockLib", "Rockifier", "PackageCache", $"{package.GetKey()}.nupkg");

        private class Package
        {
            public Package(string id, string version, bool recurse) => (Id, Version, Recurse) = (id, version, recurse);
            public readonly string Id;
            public readonly string Version;
            public readonly bool Recurse;
            public string GetKey() => $"{Id}.{Version}";
        }

        private class PackageAssemblies
        {
            public PackageAssemblies(Package package, IReadOnlyList<AssemblyInfo> assemblyInfos) => (Package, AssemblyInfos) = (package, assemblyInfos);
            public readonly Package Package;
            public readonly IReadOnlyList<AssemblyInfo> AssemblyInfos;
        }

        private class AssemblyInfo
        {
            public AssemblyInfo(Assembly assembly, XDocument documentation) => (Assembly, Documentation) = (assembly, documentation);
            public Assembly Assembly { get; }
            public XDocument Documentation { get; }
        }

        private class TargetFrameworkComparer : IComparer<string>
        {
            private TargetFrameworkComparer() { }
            public static readonly TargetFrameworkComparer Instance = new TargetFrameworkComparer();

            public int Compare(string x, string y)
            {
                if (x == y)
                    return 0;

                var compare = Compare(x, y, "netstandard");
                if (compare != 0)
                    return compare;

                compare = Compare(x, y, "netcoreapp");
                if (compare != 0)
                    return compare;

                return Compare(x, y, "net");
            }

            private int Compare(string x, string y, string prefix)
            {
                var xStartsWithPrefix = x.StartsWith(prefix);
                var yStartsWithPrefix = y.StartsWith(prefix);

                if (xStartsWithPrefix && !yStartsWithPrefix)
                    return -1;
                if (!xStartsWithPrefix && yStartsWithPrefix)
                    return 1;

                if (xStartsWithPrefix && yStartsWithPrefix)
                    return Compare(x, y, prefix.Length);

                return 0;
            }

            private int Compare(string x, string y, int startIndex)
            {
                var xVersionString = x.Substring(startIndex);
                var yVersionString = y.Substring(startIndex);

                double xVersion, yVersion;
                bool xValid, yValid;
                xValid = double.TryParse(xVersionString, out xVersion);
                yValid = double.TryParse(yVersionString, out yVersion);

                if (!xValid && !yValid)
                    return 0;

                if (!xValid)
                    return 1;

                if (!yValid)
                    return -1;

                // Pick the largest version
                return yVersion.CompareTo(xVersion);
            }
        }
    }
}
