using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.WebSim;

public sealed class XmlAssetParseException : Exception
{
    public XmlAssetParseException(string message, int lineNumber = 1, int linePosition = 1, Exception? innerException = null)
        : base(message, innerException)
    {
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    public int LineNumber { get; }
    public int LinePosition { get; }
}

public sealed record ParsedXmlDef(LumenEffectDef Def, EffectAsset Asset, int LineNumber, int LinePosition);

/// <summary>
/// Zero-reflection, AOT/trim-safe standalone <see cref="LumenEffectDef"/> XML deserializer replicating
/// RimWorld's <c>Verse.DirectXmlToObject</c>, <c>Verse.XmlToObjectUtils</c>, and <c>Verse.ParseHelper</c>
/// rules across the closed set of Core's 17 XML POCO classes, while failing explicitly on unsupported
/// Def features (<c>ParentName</c>, <c>Abstract</c>, <c>PatchOperation</c>, unknown tags).
/// </summary>
public static class WebDirectXmlLoader
{
    public const string FullDefTypeName = "RimLumen.Core.Assets.LumenEffectDef";
    public const string ShortDefTypeName = "LumenEffectDef";

    // Game checks BindingFlags.Public | BindingFlags.NonPublic (Verse.XmlToObjectUtils.cs:175), which allows
    // legacy XML elements to bind to private fields so Core's RejectLegacyFields() reports exact renames/removals.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "startColorLinear")]
    private static extern ref string? EmitterStartColorLinear(LumenEmitterXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "bloomWeight")]
    private static extern ref string? EmitterBloomWeight(LumenEmitterXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "hdrMultiplier")]
    private static extern ref string? EmitterHdrMultiplier(LumenEmitterXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "rgbaLinear")]
    private static extern ref string? GradientStopRgbaLinear(LumenGradientStopXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "colorLinear")]
    private static extern ref string? PointLightColorLinear(LumenPointLightXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "colorRgb")]
    private static extern ref string? PointLightColorRgb(LumenPointLightXml instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "overlightRadius")]
    private static extern ref string? PointLightOverlightRadius(LumenPointLightXml instance);

    public static EffectAsset ParseSingle(string xml, string sourceName = "web-draft.xml")
    {
        IReadOnlyList<ParsedXmlDef> defs = ParseDocument(xml, sourceName);
        if (defs.Count != 1)
        {
            ParsedXmlDef second = defs[1];
            throw new XmlAssetParseException(
                $"{sourceName}: expected a single LumenEffectDef, found {defs.Count}. Open one def per document in standalone mode.",
                second.LineNumber,
                second.LinePosition);
        }
        return defs[0].Asset;
    }

    public static IReadOnlyList<ParsedXmlDef> ParseDocument(string xml, string sourceName = "web-draft.xml")
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new XmlAssetParseException("Effect asset XML is empty.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new XmlAssetParseException($"Effect asset XML is malformed: {ex.Message}", ex.LineNumber, ex.LinePosition, ex);
        }

        XElement root = document.Root ?? throw new XmlAssetParseException("Effect asset XML has no root element.");
        RejectUnsupportedFeatures(root, isDefCandidate: false);

        if (IsLumenEffectDefElement(root))
        {
            ValidateDefRootClassAttribute(root);
            LumenEffectDef def = ParseDefElement(root);
            EffectAsset asset = ConvertToAssetWithLineInfo(def, root);
            GetLineInfo(root, out int line, out int col);
            return new[] { new ParsedXmlDef(def, asset, line, col) };
        }

        if (root.Name.LocalName == "Defs")
        {
            if (root.HasAttributes)
            {
                RejectUnsupportedAttributes(root);
                throw ErrorAt(root, $"Unexpected attribute on <Defs>: '{root.Attributes().First().Name}'.");
            }

            var results = new List<ParsedXmlDef>();
            foreach (XNode node in root.Nodes())
            {
                if (node is XComment) continue;
                if (node is XText text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Value))
                        throw ErrorAt(node, "XML format error: Unexpected raw text inside <Defs>.");
                    continue;
                }
                if (node is not XElement child) continue;
                RejectUnsupportedFeatures(child, isDefCandidate: true);
                if (!IsLumenEffectDefElement(child))
                {
                    throw ErrorAt(child, $"Only standalone <{FullDefTypeName}> elements are supported inside <Defs>; found <{child.Name.LocalName}>.");
                }
                ValidateDefRootClassAttribute(child);
                LumenEffectDef def = ParseDefElement(child);
                EffectAsset asset = ConvertToAssetWithLineInfo(def, child);
                GetLineInfo(child, out int line, out int col);
                results.Add(new ParsedXmlDef(def, asset, line, col));
            }

            if (results.Count == 0)
            {
                throw ErrorAt(root, $"<Defs> contains no <{FullDefTypeName}> element.");
            }
            return results;
        }

        throw ErrorAt(root, $"Expected <Defs> or <{FullDefTypeName}> root element, found <{root.Name.LocalName}>.");
    }

    public static LumenEffectDef ParseDefElement(XElement defElement)
    {
        if (IsNullElement(defElement))
        {
            throw ErrorAt(defElement, $"Root <{defElement.Name.LocalName}> cannot have IsNull=\"true\".");
        }
        return PopulateObject(defElement, new LumenEffectDef(), ReadEffectDefField);
    }

    private static EffectAsset ConvertToAssetWithLineInfo(LumenEffectDef def, XElement element)
    {
        try
        {
            return def.ToEffectAsset();
        }
        catch (InvalidDataException ex)
        {
            XElement target = FindErrorElement(element, ex.Message) ?? element;
            GetLineInfo(target, out int line, out int col);
            throw new XmlAssetParseException(ex.Message, line, col, ex);
        }
    }

    private static XElement? FindErrorElement(XElement defElement, string message)
    {
        XElement searchRoot = defElement;
        const string nodeMarker = "node ";
        int nodePos = message.IndexOf(nodeMarker, StringComparison.Ordinal);
        if (nodePos >= 0)
        {
            int idxStart = nodePos + nodeMarker.Length;
            int idxEnd = idxStart;
            while (idxEnd < message.Length && char.IsDigit(message[idxEnd])) idxEnd++;
            if (idxEnd > idxStart && int.TryParse(message.AsSpan(idxStart, idxEnd - idxStart), NumberStyles.None, CultureInfo.InvariantCulture, out int nodeIdx))
            {
                XElement? nodesEl = defElement.Element("nodes");
                if (nodesEl is not null)
                {
                    XElement? matchedNode = nodesEl.Elements("li").ElementAtOrDefault(nodeIdx);
                    if (matchedNode is not null) searchRoot = matchedNode;
                }
            }
        }

        int q0 = message.IndexOf('\'');
        while (q0 >= 0)
        {
            int q1 = message.IndexOf('\'', q0 + 1);
            if (q1 < 0) break;
            string candidate = message.Substring(q0 + 1, q1 - q0 - 1);
            if (candidate.Length > 0 && candidate.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                XElement? byName = searchRoot.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == candidate);
                if (byName is not null) return byName;
            }
            q0 = message.IndexOf('\'', q1 + 1);
        }

        return searchRoot != defElement ? searchRoot : null;
    }

    private static bool IsLumenEffectDefElement(XElement element)
    {
        string local = element.Name.LocalName;
        if (local is FullDefTypeName or ShortDefTypeName) return true;
        if (local == "Def")
        {
            XAttribute? classAttr = element.Attribute("Class");
            if (classAttr is not null && classAttr.Value is FullDefTypeName or ShortDefTypeName)
                return true;
        }
        return false;
    }

    private static void ValidateDefRootClassAttribute(XElement element)
    {
        foreach (XAttribute attr in element.Attributes())
        {
            if (attr.Name.LocalName == "Class")
            {
                // Verse.DirectXmlToObject.cs:243-262 (ClassTypeOf<T>) resolves runtime subclass via Class attribute.
                if (attr.Value is not (FullDefTypeName or ShortDefTypeName))
                    throw ErrorAt(element, $"Unsupported Class attribute '{attr.Value}' on <{element.Name.LocalName}>; only {FullDefTypeName} is supported.");
                continue;
            }
            if (attr.Name.LocalName.Equals("IsNull", StringComparison.OrdinalIgnoreCase)) continue;
            throw ErrorAt(element, $"Unsupported attribute '{attr.Name.LocalName}' on <{element.Name.LocalName}>.");
        }
    }

    private static void RejectUnsupportedFeatures(XElement element, bool isDefCandidate)
    {
        string name = element.Name.LocalName;
        if (name is "Patch" or "Operation" || name.StartsWith("PatchOperation", StringComparison.Ordinal))
        {
            throw ErrorAt(element, $"PatchOperation XML patches (<{name}>) are not supported in standalone web editing.");
        }

        RejectUnsupportedAttributes(element);

        if (!isDefCandidate && element.Name.LocalName != "Defs")
        {
            XAttribute? classAttr = element.Attribute("Class");
            if (classAttr is not null && classAttr.Value.Contains("PatchOperation", StringComparison.Ordinal))
                throw ErrorAt(element, $"PatchOperation class '{classAttr.Value}' is not supported in standalone web editing.");
        }
    }

    private static void RejectUnsupportedAttributes(XElement element)
    {
        foreach (XAttribute attr in element.Attributes())
        {
            string local = attr.Name.LocalName;
            // Game resolves ParentName/Abstract via XmlInheritance.GetResolvedNodeFor (Verse.DirectXmlToObject.cs:56,171).
            if (local.Equals("ParentName", StringComparison.OrdinalIgnoreCase) ||
                local.Equals("Abstract", StringComparison.OrdinalIgnoreCase))
            {
                throw ErrorAt(element, $"Def inheritance attributes ('{local}') are not supported; paste or export a standalone <LumenEffectDef> without ParentName/Abstract.");
            }
            // Game checks MayRequire/MayRequireAnyOf at Verse.DirectXmlToObject.cs:217-218,297-310.
            if (local.Equals("MayRequire", StringComparison.OrdinalIgnoreCase) ||
                local.Equals("MayRequireAnyOf", StringComparison.OrdinalIgnoreCase) ||
                local.Equals("Inherit", StringComparison.OrdinalIgnoreCase))
            {
                throw ErrorAt(element, $"XML attribute '{local}' is not supported in standalone web editing.");
            }
            // Game supports IgnoreIfNoMatchingField at Verse.XmlToObjectUtils.cs:107-111; standalone editor rejects unknown fields loudly.
            if (local.Equals("IgnoreIfNoMatchingField", StringComparison.OrdinalIgnoreCase))
            {
                throw ErrorAt(element, $"Attribute '{local}' is not supported; standalone validation requires exact known fields.");
            }
        }
    }

    private static void RejectExtraAttributesOnChild(XElement element, bool allowIsNull = true)
    {
        RejectUnsupportedAttributes(element);
        foreach (XAttribute attr in element.Attributes())
        {
            if (allowIsNull && attr.Name.LocalName == "IsNull") continue;
            // Exact attribute name "IsNull" matches Verse.DirectXmlToObject.cs:45,161; Reject Class on fixed POCO fields.
            throw ErrorAt(element, $"Unsupported attribute '{attr.Name.LocalName}' on <{element.Name.LocalName}>.");
        }
    }

    // Verse.DirectXmlToObject.cs:45-49,156-165: IsNull attribute check ("true", InvariantCultureIgnoreCase).
    private static bool IsNullElement(XElement element)
    {
        XAttribute? attr = element.Attribute("IsNull");
        return attr is not null && attr.Value.Equals("true", StringComparison.InvariantCultureIgnoreCase);
    }

    private static T PopulateObject<T>(XElement parent, T instance, Action<T, XElement> dispatch)
    {
        HashSet<string>? seen = null;
        int childElementCount = parent.Elements().Count();
        if (childElementCount > 1) seen = new HashSet<string>(StringComparer.Ordinal);

        // Verse.DirectXmlToObject.cs:190-204: iterate ChildNodes, skip comments, reject duplicate field tags.
        foreach (XNode node in parent.Nodes())
        {
            if (node is XComment) continue;
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                    throw ErrorAt(node, $"Unexpected raw text '{text.Value.Trim()}' inside object <{parent.Name.LocalName}>; expected child field tags.");
                continue;
            }
            if (node is XCData)
            {
                throw ErrorAt(parent, $"CDATA can only be used for string fields, not object <{parent.Name.LocalName}>.");
            }
            if (node is not XElement fieldEl) continue;

            RejectExtraAttributesOnChild(fieldEl);
            string fieldName = fieldEl.Name.LocalName;
            if (seen is not null && !seen.Add(fieldName))
            {
                throw ErrorAt(fieldEl, $"XML {typeof(T).Name} defines the same field twice: '{fieldName}'.");
            }
            dispatch(instance, fieldEl);
        }

        return instance;
    }

    private static T? ParseNestedObject<T>(XElement element, Func<T> factory, Action<T, XElement> dispatch) where T : class
    {
        if (IsNullElement(element)) return null;
        return PopulateObject(element, factory(), dispatch);
    }

    // Extract single text/CDATA child following Verse.DirectXmlToObject.cs:94-114 & 150-177.
    private static bool TryGetScalarText(XElement element, string targetTypeName, bool allowCData, out string? value, out bool isCData)
    {
        isCData = false;
        if (IsNullElement(element))
        {
            value = null;
            return false;
        }

        XNode? contentNode = null;
        foreach (XNode n in element.Nodes())
        {
            if (n is XComment) continue;
            if (n is XElement childEl)
                throw ErrorAt(childEl, $"Expected scalar text for {targetTypeName} in <{element.Name.LocalName}>, found child element <{childEl.Name.LocalName}>.");
            if (contentNode is not null)
                throw ErrorAt(element, $"Malformed mixed content inside <{element.Name.LocalName}>.");
            contentNode = n;
        }

        if (contentNode is null)
        {
            value = null;
            return false;
        }

        if (contentNode is XCData cdata)
        {
            if (!allowCData)
                throw ErrorAt(element, $"CDATA can only be used for strings, not {targetTypeName} in <{element.Name.LocalName}>.");
            isCData = true;
            value = cdata.Value;
            return true;
        }

        if (contentNode is XText text)
        {
            value = text.Value;
            return true;
        }

        throw ErrorAt(element, $"Unsupported XML node inside <{element.Name.LocalName}>.");
    }

    // Verse.DirectXmlToObject.cs:94-114,152-155 + Verse.ParseHelper.cs:39-42 (ParseString replaces "\\n" with "\n").
    private static string? ParseString(XElement element)
    {
        if (IsNullElement(element)) return null;
        if (!TryGetScalarText(element, "string", allowCData: true, out string? raw, out bool isCData))
            return string.Empty;
        return isCData ? raw! : raw!.Replace("\\n", "\n");
    }

    // Verse.ParseHelper.cs:179-182 (ParseBool -> bool.Parse); Verse.DirectXmlToObject.cs:168-177 (empty -> false).
    private static bool ParseBool(XElement element)
    {
        if (!TryGetScalarText(element, "bool", allowCData: false, out string? raw, out _))
            return false;
        if (bool.TryParse(raw, out bool result)) return result;
        throw ErrorAt(element, $"Invalid bool '{raw}' in <{element.Name.LocalName}>; expected 'true' or 'false'.");
    }

    // Verse.ParseHelper.cs:49-57 (ParseIntPermissive -> int.TryParse(NumberStyles.Any, InvariantCulture), else (int)float.Parse).
    private static int ParseIntPermissive(XElement element)
    {
        if (!TryGetScalarText(element, "int", allowCData: false, out string? raw, out _))
            return 0;
        if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        if (float.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float asFloat)
            && float.IsFinite(asFloat))
        {
            Verse.Log.Warning("Parsed " + raw + " as int.");
            return (int)asFloat;
        }
        throw ErrorAt(element, $"Invalid integer '{raw}' in <{element.Name.LocalName}>.");
    }

    private static int? ParseNullableInt(XElement element)
    {
        if (IsNullElement(element)) return null;
        // Verse.DirectXmlToObject.cs:168-177 instantiates underlying int (0) when empty without IsNull="true".
        return ParseIntPermissive(element);
    }

    // Verse.ParseHelper.cs:174-177 (ParseFloat -> float.Parse(str, CultureInfo.InvariantCulture)).
    private static float ParseFloat(XElement element)
    {
        if (!TryGetScalarText(element, "float", allowCData: false, out string? raw, out _))
            return 0f;
        if (float.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float parsed))
            return parsed;
        throw ErrorAt(element, $"Invalid float '{raw}' in <{element.Name.LocalName}>.");
    }

    private static float? ParseNullableFloat(XElement element)
    {
        if (IsNullElement(element)) return null;
        return ParseFloat(element);
    }

    // Verse.ParseHelper.cs:445-461 -> Enum.Parse(itemType, str) (case-sensitive; supports comma-separated [Flags] names).
    // Note: Verse.DirectXmlToObject.cs:115-125 also checks child <li> lists for [Flags], but casts (int)(object)item
    // at line 121 which throws InvalidCastException for byte-backed enums (LumenEnvironmentChannel, LumenSubEmitterInherit).
    private static TEnum ParseEnum<TEnum>(XElement element) where TEnum : struct, Enum
    {
        if (element.HasElements)
        {
            throw ErrorAt(element,
                $"Enum field <{element.Name.LocalName}> must use comma-separated names (e.g. 'Explosion, Cursor'), not <li> child tags.");
        }
        if (!TryGetScalarText(element, typeof(TEnum).Name, allowCData: false, out string? raw, out _))
            return default;
        if (Enum.TryParse<TEnum>(raw, ignoreCase: false, out TEnum value))
            return value;
        throw ErrorAt(element,
            $"'{raw}' is not a valid value for {typeof(TEnum).Name} in <{element.Name.LocalName}> (known: {string.Join(", ", Enum.GetNames<TEnum>())}).");
    }

    private static TEnum? ParseNullableEnum<TEnum>(XElement element) where TEnum : struct, Enum
    {
        if (IsNullElement(element)) return null;
        return ParseEnum<TEnum>(element);
    }

    // Verse.ParseHelper.cs:59-69 (FromStringVector3): TrimStart('('), TrimEnd(')'), Split(','), float.Parse(..., InvariantCulture).
    private static UnityEngine.Vector3 ParseVector3(XElement element)
    {
        if (!TryGetScalarText(element, "Vector3", allowCData: false, out string? raw, out _))
            return default;
        string trimmed = raw!.TrimStart('(').TrimEnd(')');
        string[] parts = trimmed.Split(',');
        if (parts.Length != 3)
        {
            throw ErrorAt(element, $"Invalid Vector3 '{raw}' in <{element.Name.LocalName}>; expected '(x, y, z)'.");
        }
        if (!float.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(parts[2], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float z))
        {
            throw ErrorAt(element, $"Invalid numeric component in Vector3 '{raw}' in <{element.Name.LocalName}>.");
        }
        return new UnityEngine.Vector3 { x = x, y = y, z = z };
    }

    private static UnityEngine.Vector3? ParseNullableVector3(XElement element)
    {
        if (IsNullElement(element)) return null;
        return ParseVector3(element);
    }

    // Verse.ParseHelper.cs:128-164 (FromStringQuaternion): TrimStart('('), TrimEnd(')'), Split(','), fills 1..4 floats, rejects >= 5.
    private static UnityEngine.Quaternion ParseQuaternion(XElement element)
    {
        if (!TryGetScalarText(element, "Quaternion", allowCData: false, out string? raw, out _))
            return default;
        string trimmed = raw!.TrimStart('(').TrimEnd(')');
        string[] parts = trimmed.Split(',');
        if (parts.Length >= 5)
        {
            throw ErrorAt(element, $"Too many elements in Quaternion '{raw}' in <{element.Name.LocalName}>; expected '(x, y, z, w)'.");
        }
        float x = 0f, y = 0f, z = 0f, w = 0f;
        if (parts.Length >= 1 && !TryConvertToSingle(parts[0], out x) ||
            parts.Length >= 2 && !TryConvertToSingle(parts[1], out y) ||
            parts.Length >= 3 && !TryConvertToSingle(parts[2], out z) ||
            parts.Length >= 4 && !TryConvertToSingle(parts[3], out w))
        {
            throw ErrorAt(element, $"Invalid numeric component in Quaternion '{raw}' in <{element.Name.LocalName}>.");
        }
        return new UnityEngine.Quaternion { x = x, y = y, z = z, w = w };
    }

    private static bool TryConvertToSingle(string token, out float value)
        => float.TryParse(token, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);

    // Verse.DirectXmlToObject.cs:285-328,372-389 (ListFromXml & ValidateListNode):
    // skips comments, rejects non-whitespace raw text, requires <li> element tags, respects IsNull="true".
    private static List<T>? ParseList<T>(XElement listRoot, Func<XElement, T> parseItem)
    {
        if (IsNullElement(listRoot)) return null;
        var list = new List<T>();
        foreach (XNode node in listRoot.Nodes())
        {
            if (node is XComment) continue;
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                    throw ErrorAt(node, $"XML format error: Raw text found inside list <{listRoot.Name.LocalName}>. Wrap items in <li> tags.");
                continue;
            }
            if (node is XCData)
            {
                throw ErrorAt(node, $"XML format error: CDATA found directly inside list <{listRoot.Name.LocalName}> without <li> tag.");
            }
            if (node is not XElement itemEl) continue;

            RejectExtraAttributesOnChild(itemEl);
            if (itemEl.Name.LocalName != "li")
            {
                throw ErrorAt(itemEl, $"XML format error: List item in <{listRoot.Name.LocalName}> must be named <li>, found <{itemEl.Name.LocalName}>.");
            }
            list.Add(parseItem(itemEl));
        }
        return list;
    }

    private static XmlAssetParseException UnknownOrCaseMismatchField(XElement el, string typeName, string[] knownFields)
    {
        string name = el.Name.LocalName;
        // Verse.XmlToObjectUtils.cs:147-169 enforces case-sensitive XML field matching.
        foreach (string known in knownFields)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                return ErrorAt(el, $"XML tag <{name}> does not match case-sensitive field '{known}' on {typeName} (RimWorld XML tags are case-sensitive).");
            }
        }
        return ErrorAt(el, $"Unknown XML tag <{name}> on {typeName}.");
    }

    private static readonly string[] EffectDefKnownFields =
    {
        "defName", "defaultDuration", "nodes", "states", "initialState", "stopState", "timelineEvents", "interactionEvents",
    };

    private static void ReadEffectDefField(LumenEffectDef d, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "defName": d.defName = ParseString(el); break;
            case "defaultDuration": d.defaultDuration = ParseFloat(el); break;
            case "nodes": d.nodes = ParseList(el, li => ParseNestedObject(li, static () => new LumenNodeXml(), ReadNodeField)!); break;
            case "states": d.states = ParseList(el, li => ParseNestedObject(li, static () => new LumenStateXml(), ReadStateField)!); break;
            case "initialState": d.initialState = ParseString(el); break;
            case "stopState": d.stopState = ParseString(el); break;
            case "timelineEvents": d.timelineEvents = ParseList(el, li => ParseNestedObject(li, static () => new LumenTimelineEventXml(), ReadTimelineEventField)!); break;
            case "interactionEvents": d.interactionEvents = ParseList(el, li => ParseNestedObject(li, static () => new LumenInteractionEventXml(), ReadInteractionEventField)!); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenEffectDef), EffectDefKnownFields);
        }
    }

    private static readonly string[] NodeKnownFields =
    {
        "name", "parentIndex", "spacePlane", "depthMask", "localPosition", "localYawDegrees", "localScale", "heightOffset",
        "emitter", "pointLight", "distortion", "ribbon", "screenDim",
    };

    private static void ReadNodeField(LumenNodeXml n, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "name": n.name = ParseString(el); break;
            case "parentIndex": n.parentIndex = ParseIntPermissive(el); break;
            case "spacePlane": n.spacePlane = ParseEnum<LumenSpacePlane>(el); break;
            case "depthMask": n.depthMask = ParseEnum<LumenDepthMask>(el); break;
            case "localPosition": n.localPosition = ParseVector3(el); break;
            case "localYawDegrees": n.localYawDegrees = ParseFloat(el); break;
            case "localScale": n.localScale = ParseFloat(el); break;
            case "heightOffset": n.heightOffset = ParseFloat(el); break;
            case "emitter": n.emitter = ParseNestedObject(el, static () => new LumenEmitterXml(), ReadEmitterField); break;
            case "pointLight": n.pointLight = ParseNestedObject(el, static () => new LumenPointLightXml(), ReadPointLightField); break;
            case "distortion": n.distortion = ParseNestedObject(el, static () => new LumenDistortionXml(), ReadDistortionField); break;
            case "ribbon": n.ribbon = ParseNestedObject(el, static () => new LumenRibbonXml(), ReadRibbonField); break;
            case "screenDim": n.screenDim = ParseNestedObject(el, static () => new LumenScreenDimXml(), ReadScreenDimField); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenNodeXml), NodeKnownFields);
        }
    }

    private static readonly string[] EmitterKnownFields =
    {
        "name", "preferredTier", "maxParticles", "duration", "looping", "rateOverTime", "bursts",
        "startLifetimeMin", "startLifetimeMax", "startSpeedMin", "startSpeedMax", "startSizeMin", "startSizeMax",
        "startColor", "colorSpace", "materialTint", "startColorLinear", "bloomWeight", "hdrMultiplier",
        "hdrIntensity", "shape", "shapeExtents", "shapePosition", "shapeRotation", "shapeScale",
        "coneAngleDegrees", "coneLength", "donutRadius", "radiusThickness", "boxThickness",
        "arcDegrees", "arcSpread", "radiusSpread", "randomDirectionAmount", "sphericalDirectionAmount", "randomPositionAmount",
        "billboardMode", "velocityStretchFactor", "lengthScale", "blendMode", "softParticleFadeDistance",
        "textureKey", "flipbookTilesX", "flipbookTilesY", "flipbookCycles", "worldSpaceSimulation", "killBelowFloor",
        "sizeOverLifetime", "colorOverLifetime", "forceFields", "environmentChannels", "environmentForceScale",
        "collision", "subEmitters", "trails",
    };

    private static void ReadEmitterField(LumenEmitterXml e, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "name": e.name = ParseString(el); break;
            case "preferredTier": e.preferredTier = ParseEnum<LumenParticleTier>(el); break;
            case "maxParticles": e.maxParticles = ParseIntPermissive(el); break;
            case "duration": e.duration = ParseFloat(el); break;
            case "looping": e.looping = ParseBool(el); break;
            case "rateOverTime": e.rateOverTime = ParseFloat(el); break;
            case "bursts": e.bursts = ParseList(el, li => ParseNestedObject(li, static () => new LumenBurstXml(), ReadBurstField)!); break;
            case "startLifetimeMin": e.startLifetimeMin = ParseFloat(el); break;
            case "startLifetimeMax": e.startLifetimeMax = ParseFloat(el); break;
            case "startSpeedMin": e.startSpeedMin = ParseFloat(el); break;
            case "startSpeedMax": e.startSpeedMax = ParseFloat(el); break;
            case "startSizeMin": e.startSizeMin = ParseFloat(el); break;
            case "startSizeMax": e.startSizeMax = ParseFloat(el); break;
            case "startColor": e.startColor = ParseString(el) ?? string.Empty; break;
            case "colorSpace": e.colorSpace = ParseEnum<LumenColorSpace>(el); break;
            case "materialTint": e.materialTint = ParseString(el) ?? string.Empty; break;
            case "startColorLinear": EmitterStartColorLinear(e) = ParseString(el); break;
            case "bloomWeight": EmitterBloomWeight(e) = ParseString(el); break;
            case "hdrMultiplier": EmitterHdrMultiplier(e) = ParseString(el); break;
            case "hdrIntensity": e.hdrIntensity = ParseFloat(el); break;
            case "shape": e.shape = ParseEnum<LumenShapeKind>(el); break;
            case "shapeExtents": e.shapeExtents = ParseVector3(el); break;
            case "shapePosition": e.shapePosition = ParseVector3(el); break;
            case "shapeRotation": e.shapeRotation = ParseQuaternion(el); break;
            case "shapeScale": e.shapeScale = ParseVector3(el); break;
            case "coneAngleDegrees": e.coneAngleDegrees = ParseFloat(el); break;
            case "coneLength": e.coneLength = ParseFloat(el); break;
            case "donutRadius": e.donutRadius = ParseFloat(el); break;
            case "radiusThickness": e.radiusThickness = ParseFloat(el); break;
            case "boxThickness": e.boxThickness = ParseVector3(el); break;
            case "arcDegrees": e.arcDegrees = ParseFloat(el); break;
            case "arcSpread": e.arcSpread = ParseFloat(el); break;
            case "radiusSpread": e.radiusSpread = ParseFloat(el); break;
            case "randomDirectionAmount": e.randomDirectionAmount = ParseFloat(el); break;
            case "sphericalDirectionAmount": e.sphericalDirectionAmount = ParseFloat(el); break;
            case "randomPositionAmount": e.randomPositionAmount = ParseFloat(el); break;
            case "billboardMode": e.billboardMode = ParseEnum<LumenBillboardMode>(el); break;
            case "velocityStretchFactor": e.velocityStretchFactor = ParseFloat(el); break;
            case "lengthScale": e.lengthScale = ParseFloat(el); break;
            case "blendMode": e.blendMode = ParseEnum<LumenBlendMode>(el); break;
            case "softParticleFadeDistance": e.softParticleFadeDistance = ParseFloat(el); break;
            case "textureKey": e.textureKey = ParseString(el); break;
            case "flipbookTilesX": e.flipbookTilesX = ParseIntPermissive(el); break;
            case "flipbookTilesY": e.flipbookTilesY = ParseIntPermissive(el); break;
            case "flipbookCycles": e.flipbookCycles = ParseFloat(el); break;
            case "worldSpaceSimulation": e.worldSpaceSimulation = ParseBool(el); break;
            case "killBelowFloor": e.killBelowFloor = ParseBool(el); break;
            case "sizeOverLifetime": e.sizeOverLifetime = ParseCurveKeyList(el); break;
            case "colorOverLifetime": e.colorOverLifetime = ParseGradientStopList(el); break;
            case "forceFields": e.forceFields = ParseList(el, li => ParseNestedObject(li, static () => new LumenForceFieldXml(), ReadForceFieldField)!); break;
            case "environmentChannels": e.environmentChannels = ParseEnum<LumenEnvironmentChannel>(el); break;
            case "environmentForceScale": e.environmentForceScale = ParseFloat(el); break;
            case "collision": e.collision = ParseNestedObject(el, static () => new LumenCollisionXml(), ReadCollisionField); break;
            case "subEmitters": e.subEmitters = ParseList(el, li => ParseNestedObject(li, static () => new LumenSubEmitterXml(), ReadSubEmitterField)!); break;
            case "trails": e.trails = ParseNestedObject(el, static () => new LumenParticleTrailXml(), ReadParticleTrailField); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenEmitterXml), EmitterKnownFields);
        }
    }

    private static List<LumenCurveKeyXml>? ParseCurveKeyList(XElement el)
        => ParseList(el, li => ParseNestedObject(li, static () => new LumenCurveKeyXml(), ReadCurveKeyField)!);

    private static List<LumenGradientStopXml>? ParseGradientStopList(XElement el)
        => ParseList(el, li => ParseNestedObject(li, static () => new LumenGradientStopXml(), ReadGradientStopField)!);

    private static readonly string[] StateKnownFields = { "name", "nodes", "duration", "loop", "next" };
    private static void ReadStateField(LumenStateXml s, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "name": s.name = ParseString(el); break;
            case "nodes": s.nodes = ParseList(el, li => ParseString(li) ?? string.Empty); break;
            case "duration": s.duration = ParseNullableFloat(el); break;
            case "loop": s.loop = ParseBool(el); break;
            case "next": s.next = ParseString(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenStateXml), StateKnownFields);
        }
    }

    private static readonly string[] TimelineEventKnownFields = { "time", "eventName", "targetNodeIndex", "state", "notify" };
    private static void ReadTimelineEventField(LumenTimelineEventXml t, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "time": t.time = ParseFloat(el); break;
            case "eventName": t.eventName = ParseString(el); break;
            case "targetNodeIndex": t.targetNodeIndex = ParseIntPermissive(el); break;
            case "state": t.state = ParseString(el); break;
            case "notify": t.notify = ParseBool(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenTimelineEventXml), TimelineEventKnownFields);
        }
    }

    private static readonly string[] BurstKnownFields = { "time", "count", "probability" };
    private static void ReadBurstField(LumenBurstXml b, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "time": b.time = ParseFloat(el); break;
            case "count": b.count = ParseIntPermissive(el); break;
            case "probability": b.probability = ParseFloat(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenBurstXml), BurstKnownFields);
        }
    }

    private static readonly string[] CurveKeyKnownFields = { "time", "value", "inTangent", "outTangent" };
    private static void ReadCurveKeyField(LumenCurveKeyXml k, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "time": k.time = ParseFloat(el); break;
            case "value": k.value = ParseFloat(el); break;
            case "inTangent": k.inTangent = ParseFloat(el); break;
            case "outTangent": k.outTangent = ParseFloat(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenCurveKeyXml), CurveKeyKnownFields);
        }
    }

    private static readonly string[] GradientStopKnownFields = { "time", "color", "rgbaLinear" };
    private static void ReadGradientStopField(LumenGradientStopXml s, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "time": s.time = ParseFloat(el); break;
            case "color": s.color = ParseString(el) ?? string.Empty; break;
            case "rgbaLinear": GradientStopRgbaLinear(s) = ParseString(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenGradientStopXml), GradientStopKnownFields);
        }
    }

    private static readonly string[] ForceFieldKnownFields = { "kind", "centerOrAxis", "parameters", "strength", "frequency", "radius" };
    private static void ReadForceFieldField(LumenForceFieldXml f, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "kind": f.kind = ParseEnum<ForceFieldKind>(el); break;
            case "centerOrAxis": f.centerOrAxis = ParseVector3(el); break;
            case "parameters": f.parameters = ParseVector3(el); break;
            case "strength": f.strength = ParseNullableFloat(el); break;
            case "frequency": f.frequency = ParseFloat(el); break;
            case "radius": f.radius = ParseFloat(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenForceFieldXml), ForceFieldKnownFields);
        }
    }

    private static readonly string[] PointLightKnownFields =
    {
        "color", "colorLinear", "colorRgb", "overlightRadius", "radiusCells", "intensity", "intensityOverTime",
    };
    private static void ReadPointLightField(LumenPointLightXml l, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "color": l.color = ParseString(el) ?? string.Empty; break;
            case "colorLinear": PointLightColorLinear(l) = ParseString(el); break;
            case "colorRgb": PointLightColorRgb(l) = ParseString(el); break;
            case "overlightRadius": PointLightOverlightRadius(l) = ParseString(el); break;
            case "radiusCells": l.radiusCells = ParseFloat(el); break;
            case "intensity": l.intensity = ParseFloat(el); break;
            case "intensityOverTime": l.intensityOverTime = ParseCurveKeyList(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenPointLightXml), PointLightKnownFields);
        }
    }

    private static readonly string[] CollisionKnownFields = { "mode", "bounce", "dampen", "lifetimeLoss", "killUnderRoof" };
    private static void ReadCollisionField(LumenCollisionXml c, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "mode": c.mode = ParseEnum<LumenCollisionMode>(el); break;
            case "bounce": c.bounce = ParseFloat(el); break;
            case "dampen": c.dampen = ParseFloat(el); break;
            case "lifetimeLoss": c.lifetimeLoss = ParseFloat(el); break;
            case "killUnderRoof": c.killUnderRoof = ParseBool(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenCollisionXml), CollisionKnownFields);
        }
    }

    private static readonly string[] SubEmitterKnownFields = { "trigger", "targetNodeIndex", "count", "probability", "inherit" };
    private static void ReadSubEmitterField(LumenSubEmitterXml s, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "trigger": s.trigger = ParseNullableEnum<LumenSubEmitterTrigger>(el); break;
            case "targetNodeIndex": s.targetNodeIndex = ParseNullableInt(el); break;
            case "count": s.count = ParseNullableInt(el); break;
            case "probability": s.probability = ParseFloat(el); break;
            case "inherit": s.inherit = ParseEnum<LumenSubEmitterInherit>(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenSubEmitterXml), SubEmitterKnownFields);
        }
    }

    private static readonly string[] DistortionKnownFields =
    {
        "kind", "radius", "strength", "thickness", "speed", "frequency", "duration", "strengthOverTime",
    };
    private static void ReadDistortionField(LumenDistortionXml d, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "kind": d.kind = ParseNullableEnum<LumenDistortionKind>(el); break;
            case "radius": d.radius = ParseNullableFloat(el); break;
            case "strength": d.strength = ParseNullableFloat(el); break;
            case "thickness": d.thickness = ParseFloat(el); break;
            case "speed": d.speed = ParseFloat(el); break;
            case "frequency": d.frequency = ParseFloat(el); break;
            case "duration": d.duration = ParseFloat(el); break;
            case "strengthOverTime": d.strengthOverTime = ParseCurveKeyList(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenDistortionXml), DistortionKnownFields);
        }
    }

    private static readonly string[] ScreenDimKnownFields = { "strength", "duration", "strengthOverTime" };
    private static void ReadScreenDimField(LumenScreenDimXml s, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "strength": s.strength = ParseNullableFloat(el); break;
            case "duration": s.duration = ParseFloat(el); break;
            case "strengthOverTime": s.strengthOverTime = ParseCurveKeyList(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenScreenDimXml), ScreenDimKnownFields);
        }
    }

    private static readonly string[] RibbonKnownFields =
    {
        "mode", "lifetime", "minVertexDistance", "maxPoints", "endLocal", "endAtTarget", "segments",
        "noiseAmplitude", "noiseFrequency", "width", "widthOverLength", "colorOverLength", "hdrIntensity",
        "textureKey", "blendMode", "textureMode", "alignment", "softParticleFadeDistance",
    };
    private static void ReadRibbonField(LumenRibbonXml r, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "mode": r.mode = ParseNullableEnum<LumenRibbonMode>(el); break;
            case "lifetime": r.lifetime = ParseFloat(el); break;
            case "minVertexDistance": r.minVertexDistance = ParseFloat(el); break;
            case "maxPoints": r.maxPoints = ParseIntPermissive(el); break;
            case "endLocal": r.endLocal = ParseNullableVector3(el); break;
            case "endAtTarget": r.endAtTarget = ParseBool(el); break;
            case "segments": r.segments = ParseIntPermissive(el); break;
            case "noiseAmplitude": r.noiseAmplitude = ParseFloat(el); break;
            case "noiseFrequency": r.noiseFrequency = ParseFloat(el); break;
            case "width": r.width = ParseFloat(el); break;
            case "widthOverLength": r.widthOverLength = ParseCurveKeyList(el); break;
            case "colorOverLength": r.colorOverLength = ParseGradientStopList(el); break;
            case "hdrIntensity": r.hdrIntensity = ParseFloat(el); break;
            case "textureKey": r.textureKey = ParseString(el); break;
            case "blendMode": r.blendMode = ParseEnum<LumenBlendMode>(el); break;
            case "textureMode": r.textureMode = ParseEnum<LumenRibbonTextureMode>(el); break;
            case "alignment": r.alignment = ParseEnum<LumenRibbonAlignment>(el); break;
            case "softParticleFadeDistance": r.softParticleFadeDistance = ParseFloat(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenRibbonXml), RibbonKnownFields);
        }
    }

    private static readonly string[] ParticleTrailKnownFields =
    {
        "ratio", "lifetime", "minVertexDistance", "maxPoints", "dieWithParticles", "worldSpace", "sizeAffectsWidth",
        "sizeAffectsLifetime", "inheritParticleColor", "width", "widthOverTrail", "colorOverTrail",
        "colorOverLifetime", "textureKey", "materialTint", "colorSpace", "blendMode", "textureMode", "alignment",
        "hdrIntensity", "softParticleFadeDistance",
    };
    private static void ReadParticleTrailField(LumenParticleTrailXml t, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "ratio": t.ratio = ParseFloat(el); break;
            case "lifetime": t.lifetime = ParseFloat(el); break;
            case "minVertexDistance": t.minVertexDistance = ParseFloat(el); break;
            case "maxPoints": t.maxPoints = ParseIntPermissive(el); break;
            case "dieWithParticles": t.dieWithParticles = ParseBool(el); break;
            case "worldSpace": t.worldSpace = ParseBool(el); break;
            case "sizeAffectsWidth": t.sizeAffectsWidth = ParseBool(el); break;
            case "sizeAffectsLifetime": t.sizeAffectsLifetime = ParseBool(el); break;
            case "inheritParticleColor": t.inheritParticleColor = ParseBool(el); break;
            case "width": t.width = ParseFloat(el); break;
            case "widthOverTrail": t.widthOverTrail = ParseCurveKeyList(el); break;
            case "colorOverTrail": t.colorOverTrail = ParseGradientStopList(el); break;
            case "colorOverLifetime": t.colorOverLifetime = ParseGradientStopList(el); break;
            case "textureKey": t.textureKey = ParseString(el); break;
            case "materialTint": t.materialTint = ParseString(el) ?? string.Empty; break;
            case "colorSpace": t.colorSpace = ParseEnum<LumenColorSpace>(el); break;
            case "blendMode": t.blendMode = ParseEnum<LumenBlendMode>(el); break;
            case "textureMode": t.textureMode = ParseEnum<LumenRibbonTextureMode>(el); break;
            case "alignment": t.alignment = ParseEnum<LumenRibbonAlignment>(el); break;
            case "hdrIntensity": t.hdrIntensity = ParseFloat(el); break;
            case "softParticleFadeDistance": t.softParticleFadeDistance = ParseFloat(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenParticleTrailXml), ParticleTrailKnownFields);
        }
    }

    private static readonly string[] InteractionEventKnownFields =
    {
        "trigger", "eventName", "radius", "anchorNodeIndex", "minCursorSpeed", "maxCursorSpeed",
        "holdSeconds", "minExplosionStrength", "cooldownSeconds", "state", "goToState", "targetNodeIndex", "notify",
    };
    private static void ReadInteractionEventField(LumenInteractionEventXml e, XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "trigger": e.trigger = ParseNullableEnum<LumenInteractionTrigger>(el); break;
            case "eventName": e.eventName = ParseString(el); break;
            case "radius": e.radius = ParseFloat(el); break;
            case "anchorNodeIndex": e.anchorNodeIndex = ParseIntPermissive(el); break;
            case "minCursorSpeed": e.minCursorSpeed = ParseFloat(el); break;
            case "maxCursorSpeed": e.maxCursorSpeed = ParseNullableFloat(el); break;
            case "holdSeconds": e.holdSeconds = ParseFloat(el); break;
            case "minExplosionStrength": e.minExplosionStrength = ParseFloat(el); break;
            case "cooldownSeconds": e.cooldownSeconds = ParseFloat(el); break;
            case "state": e.state = ParseString(el); break;
            case "goToState": e.goToState = ParseString(el); break;
            case "targetNodeIndex": e.targetNodeIndex = ParseIntPermissive(el); break;
            case "notify": e.notify = ParseBool(el); break;
            default: throw UnknownOrCaseMismatchField(el, nameof(LumenInteractionEventXml), InteractionEventKnownFields);
        }
    }

    private static void GetLineInfo(XObject obj, out int line, out int col)
    {
        if (obj is IXmlLineInfo info && info.HasLineInfo())
        {
            line = Math.Max(1, info.LineNumber);
            col = Math.Max(1, info.LinePosition);
            return;
        }
        line = 1;
        col = 1;
    }

    private static XmlAssetParseException ErrorAt(XObject obj, string message)
    {
        GetLineInfo(obj, out int line, out int col);
        return new XmlAssetParseException(message, line, col);
    }
}
