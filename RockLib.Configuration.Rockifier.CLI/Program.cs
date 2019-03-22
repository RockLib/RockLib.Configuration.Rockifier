using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace RockLib.Configuration.Editor.CLI
{
    class Program
    {
        static int Main(string[] args)
        {
            var app = new CommandLineApplication();

            app.HelpOption();

            app.Command("load", loadCommand =>
            {
                loadCommand.Description = "Writes config information for the specified project to stdout";

                var projectPath = loadCommand.Argument("projectPath", "The path to the project with config to load");
                var debugProfile = loadCommand.Option("-p|--debugProfile <DEBUGPROFILE>", "The name of the debug profile to load environment variables from", CommandOptionType.SingleValue);
                var indent = loadCommand.Option("-i|--indent", "Whether to indent output", CommandOptionType.NoValue);

                loadCommand.HelpOption();

                loadCommand.OnExecute(() =>
                {
                    var rockifier = new Rockifier(debugProfile.Value());
                    var config = rockifier.Load(projectPath.Value);
                    Console.WriteLine(GetJson(config, indent.HasValue()));

                    return 0;
                });
            });

            app.OnExecute(() =>
            {
                Console.WriteLine("Specify a command");
                app.ShowHelp();
                return 1;
            });

            var result = app.Execute(args);

            if (Debugger.IsAttached)
            {
                Console.Write("Press any key to continue . . . ");
                Console.ReadKey(true);
            }

            return result;
        }

        private static string GetJson(ConfigInfo config, bool indent)
        {
            var jObject = new JObject();

            AddSections(config, jObject);
            AddImplementations(config, jObject);
            AddSettings(config, jObject);

            return jObject.ToString(indent ? Formatting.Indented : Formatting.None, new StringEnumConverter());
        }

        private static void AddSections(ConfigInfo config, dynamic json)
        {
            if (config.Sections.Count > 0)
            {
                json.sections = new JObject();

                foreach (var section in config.Sections)
                {
                    var jsonSection = json.sections[section.Key] = new JObject();
                    AddTypeToJson(section.Value, jsonSection);
                }
            }
        }

        private static void AddImplementations(ConfigInfo config, dynamic json)
        {
            var implementations = json.implementations = new JArray();

            var enumTypes = new HashSet<Type>();

            foreach (var configImplementation in config.Implementations)
            {
                dynamic implementation = new JObject();
                implementations.Add(implementation);

                AddTypeToJson(configImplementation, implementation);
                AddConstructorsToJson(configImplementation, implementation, config, enumTypes);
                AddPropertiesToJson(configImplementation, implementation, config, enumTypes);
            }

            if (enumTypes.Count > 0)
            {
                var enums = json.enums = new JObject();

                foreach (var enumType in enumTypes)
                {
                    var values = enums[GetAssemblyQualifiedName(enumType)] = new JArray();
                    foreach (var enumName in Enum.GetNames(enumType))
                        values.Add(enumName);
                }
            }
        }

        private static void AddPropertiesToJson(Type configImplementation, dynamic implementation, ConfigInfo config, HashSet<Type> enumTypes)
        {
            var readWriteProperties = configImplementation.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite).ToArray();

            if (readWriteProperties.Length > 0)
            {
                var properties = implementation.properties = new JObject();

                foreach (var configProperty in readWriteProperties)
                {
                    if (configProperty.PropertyType.IsEnum)
                        enumTypes.Add(configProperty.PropertyType);

                    dynamic property = properties[configProperty.Name] = new JObject();

                    AddTypeToJson(configProperty.PropertyType, property);

                    if (config.Documentation.TryGetValue(configProperty, out var doc))
                        property.description = doc.Summary;
                }
            }
        }

        private static void AddConstructorsToJson(Type configImplementation, dynamic implementation, ConfigInfo config, HashSet<Type> enumTypes)
        {
            var constructors = implementation.constructors = new JArray();

            foreach (var configConstructor in configImplementation.GetConstructors())
            {
                dynamic constructor = new JObject();
                constructors.Add(constructor);

                if (config.Documentation.TryGetValue(configConstructor, out var doc))
                    constructor.description = doc.Summary;

                var parameters = constructor.parameters = new JObject();

                foreach (var configParameter in configConstructor.GetParameters())
                {
                    if (configParameter.ParameterType.IsEnum)
                        enumTypes.Add(configParameter.ParameterType);

                    dynamic parameter = parameters[configParameter.Name] = new JObject();

                    AddTypeToJson(configParameter.ParameterType, parameter);

                    if (configParameter.HasDefaultValue)
                    {
                        parameter.optional = true;
                        parameter.@default = configParameter.DefaultValue;
                    }

                    if (doc.Parameters.TryGetValue(configParameter.Name, out var parameterDoc))
                        parameter.description = parameterDoc;
                }
            }
        }

        private static void AddSettings(ConfigInfo config, dynamic json)
        {
            var settings = json.settings = new JObject();

            foreach (var configSettingsGroup in config.Settings)
            {
                var settingsGroup = settings[configSettingsGroup.Key] = new JObject();
                foreach (var setting in configSettingsGroup.Value)
                    settingsGroup[setting.Key] = setting.Value;
            }
        }

        private static void AddTypeToJson(Type type, dynamic json)
        {
            if (type.IsGenericType)
            {
                var typeDefinition = type.GetGenericTypeDefinition();
                if (typeDefinition == typeof(List<>)
                    || typeDefinition == typeof(ICollection<>)
                    || typeDefinition == typeof(IEnumerable<>)
                    || typeDefinition == typeof(IList<>)
                    || typeDefinition == typeof(IReadOnlyCollection<>)
                    || typeDefinition == typeof(IReadOnlyList<>))
                {
                    var itemType = type.GetGenericArguments()[0];

                    json.type = "list";
                    json.subtype = GetAssemblyQualifiedName(itemType);
                }
                else if (typeDefinition == typeof(Nullable<>))
                {
                    var nullableType = type.GetGenericArguments()[0];

                    json.type = "nullable";
                    json.subtype = GetAssemblyQualifiedName(nullableType);
                }
                else
                    throw new NotSupportedException();
            }
            else if (type.IsEnum)
            {
                json.type = "enum";
                json.subtype = GetAssemblyQualifiedName(type);
            }
            else
                json.type = GetAssemblyQualifiedName(type);
        }

        private static string GetAssemblyQualifiedName(Type type)
        {
            if (type.Assembly.GetName().Name == "System.Private.CoreLib")
                return type.FullName;

            return $"{type.FullName}, {type.Assembly.GetName().Name}";
        }
    }
}
