using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RockLib.Configuration.Editor
{
    public sealed class MemberDocumentation
    {
        internal MemberDocumentation(XElement memberElement)
        {
            Summary = GetElementValue(memberElement.Element("summary"));
            var parameters = new Dictionary<string, string>();
            foreach (var parameterElement in memberElement.Elements("param"))
            {
                var name = parameterElement.Attribute("name")?.Value;
                parameters.Add(name, GetElementValue(parameterElement));
            }
            Parameters = parameters;
        }

        public string Summary { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }

        private string GetElementValue(XElement element)
        {
            string value;
            using (var reader = element.CreateReader())
            {
                reader.MoveToContent();
                value = reader.ReadInnerXml();
            }
            return FormatDocumentation(value);
        }

        private static string FormatDocumentation(string documentation) =>
            FormatTags(
                FormatSelfClosingTags(
                    FormatParagraphs(StripNewlines(documentation)),
                    "see cref", "see langword", "paramref name", "typeparamref name"),
                "c", "code");

        private static string FormatTags(string documentation, params string[] tagNames) =>
            tagNames.Aggregate(documentation,
                (doc, tagName) => Regex.Replace(doc, $"<{tagName}[ \t\r\n]*>(.*?)</{tagName}[ \t\r\n]*>", "$1", RegexOptions.Singleline));

        private static string FormatSelfClosingTags(string documentation, params string[] tagNamesAndAttributes)
        {
            return tagNamesAndAttributes.Aggregate(documentation,
                (doc, tagNameAndAttribute) => Regex.Replace(doc, $"<{tagNameAndAttribute}=\"(.*?)\" ?/>", FormatValue));

            string FormatValue(Match match)
            {
                var value = match.Groups[1].Value;
                if (value.Length >= 2 && value[1] == ':')
                {
                    switch (value.Substring(0, 2))
                    {
                        case "M:":
                            var methodName = Regex.Match(value, @"\.([^.(]+)\(");
                            if (methodName.Success)
                                return methodName.Groups[1].Value;
                            break;
                        case "T:":
                        case "P:":
                        case "F:":
                        case "E:":
                            return value.Substring(value.LastIndexOf('.') + 1);
                    }
                }
                return value;
            }
        }

        private static string StripNewlines(string documentation) =>
            Regex.Replace(documentation, @"\n[ \t]*", " ").Trim();

        private static string FormatParagraphs(string documentation)
        {
            var paragraphs = Regex.Matches(documentation, "<para[ \t\r\n]*>(.*?)</para[ \t\r\n]*>", RegexOptions.Singleline);
            if (paragraphs.Count == 0)
                return documentation;
            else
            {
                var sb = new StringBuilder();
                var currentIndex = 0;
                foreach (Match para in paragraphs)
                {
                    if (sb.Length > 0)
                        sb.AppendLine().AppendLine();
                    if (para.Index > currentIndex)
                        sb.Append(documentation.Substring(currentIndex, para.Index - currentIndex)).AppendLine().AppendLine();
                    sb.Append(para.Groups[1].Value);
                    currentIndex = para.Index + para.Length;
                }
                var last = paragraphs[paragraphs.Count - 1];
                if (last.Index + last.Length < documentation.Length)
                    sb.AppendLine().AppendLine().Append(documentation.Substring(last.Index + last.Length));
                return sb.ToString();
            }
        }
    }
}
