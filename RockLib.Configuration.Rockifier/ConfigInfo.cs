using System;
using System.Collections.Generic;
using System.Reflection;

namespace RockLib.Configuration.Editor
{
    public sealed class ConfigInfo
    {
        internal static readonly ConfigInfo Empty = new ConfigInfo(new Dictionary<string, Type>(), new Type[0], new Dictionary<MemberInfo, MemberDocumentation>(), new Dictionary<string, IReadOnlyDictionary<string, string>>());
        internal ConfigInfo(IReadOnlyDictionary<string, Type> sections, IReadOnlyList<Type> implementations, IReadOnlyDictionary<MemberInfo, MemberDocumentation> documentation, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> settings) => (Sections, Implementations, Documentation, Settings) = (sections, implementations, documentation, settings);
        public IReadOnlyDictionary<string, Type> Sections { get; }
        public IReadOnlyList<Type> Implementations { get; }
        public IReadOnlyDictionary<MemberInfo, MemberDocumentation> Documentation { get; }
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings { get; }
    }
}
