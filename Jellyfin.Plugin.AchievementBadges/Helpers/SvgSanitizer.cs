using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.AchievementBadges.Helpers;

// Validates admin-supplied SVG markup before it's persisted to plugin config
// and served back to all users. The rendered path is <img src="data:image/svg+xml,...">
// which already isolates scripts in most browsers, but belt-and-suspenders:
//   - must parse as XML
//   - root element must be <svg>
//   - no <script>, <foreignObject>, <iframe>, <embed>, <object>, <use> (external refs)
//   - no on* event-handler attributes
//   - no javascript: / data:text/html URIs anywhere in attribute values
//   - hard size cap
public static class SvgSanitizer
{
    private const int MaxBytes = 100_000;

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "embed", "object",
        // <use> can load external content via xlink:href — drop it outright rather than
        // trying to distinguish safe same-document references from SSRF-via-SVG.
        "use",
    };

    public static bool TryValidate(string? svg, out string sanitized, out string error)
    {
        sanitized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(svg))
        {
            error = "SVG payload is empty.";
            return false;
        }

        if (svg.Length > MaxBytes)
        {
            error = $"SVG is too large (max {MaxBytes:N0} bytes).";
            return false;
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            };
            using var reader = XmlReader.Create(new System.IO.StringReader(svg), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            error = "Invalid SVG XML: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "Could not parse SVG: " + ex.Message;
            return false;
        }

        if (doc.Root is null || !string.Equals(doc.Root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            error = "Root element must be <svg>.";
            return false;
        }

        foreach (var element in doc.Descendants())
        {
            if (ForbiddenElements.Contains(element.Name.LocalName))
            {
                error = $"Forbidden element: <{element.Name.LocalName}>";
                return false;
            }

            foreach (var attr in element.Attributes())
            {
                if (attr.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Event-handler attribute not allowed: {attr.Name.LocalName}";
                    return false;
                }

                var v = attr.Value ?? string.Empty;
                if (v.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                {
                    error = "javascript: URIs not allowed.";
                    return false;
                }
                if (v.Contains("data:text/html", StringComparison.OrdinalIgnoreCase))
                {
                    error = "data:text/html URIs not allowed.";
                    return false;
                }
            }
        }

        sanitized = doc.ToString(SaveOptions.DisableFormatting);
        return true;
    }
}
