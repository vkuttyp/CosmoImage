using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using CosmoFonts.Loader;
using CosmoFonts.Shaping;

namespace CosmoImage.Loaders;

/// <summary>
/// SVG loader, pure-managed (no native deps).
///
/// <para><b>Supported (phases 1–3):</b> basic shapes (<c>&lt;rect&gt;</c>,
/// <c>&lt;circle&gt;</c>, <c>&lt;ellipse&gt;</c>, <c>&lt;line&gt;</c>,
/// <c>&lt;polygon&gt;</c>, <c>&lt;polyline&gt;</c>) plus <c>&lt;path&gt;</c>
/// with the full SVG path-data grammar (M/L/H/V/C/S/Q/T/A/Z, absolute and
/// relative). Cubic and quadratic Béziers are flattened via De Casteljau
/// subdivision; elliptical arcs are converted to cubic Béziers per the
/// SVG implementation note. <c>&lt;g&gt;</c> grouping with style
/// inheritance. The <c>transform=""</c> attribute on any element —
/// <c>matrix / translate / scale / rotate / skewX / skewY</c>, composable
/// left-to-right, nested through groups. Solid-color fill + stroke
/// (named, <c>#RGB</c>, <c>#RRGGBB</c>, <c>rgb()</c>, <c>rgba()</c>).
/// Fill rule: even-odd across multi-subpath paths. Background is
/// transparent.</para>
///
/// <para><b>Not yet supported</b> (each throws <see cref="NotSupportedException"/>
/// with a feature-specific message): <c>&lt;text&gt;</c>, <c>&lt;use&gt;</c>,
/// <c>&lt;image&gt;</c>, gradients, filters, clipping, masking. Tracked
/// as task #25.</para>
/// </summary>
public static class VipsSvgLoader
{
    // ---------- font registry (for SVG <text>) ----------

    private static readonly Dictionary<string, Face> _registeredFonts =
        new(StringComparer.OrdinalIgnoreCase);
    private static Face? _defaultFont;
    private static readonly object _fontsLock = new();

    /// <summary>
    /// Register a font by family name for use with SVG <c>&lt;text&gt;</c>
    /// elements. Call once at startup before loading any SVG that contains
    /// text. The first font registered also becomes the default — used when
    /// a <c>&lt;text&gt;</c> element references an unknown font family.
    /// </summary>
    public static void RegisterFont(string family, byte[] fontBytes)
    {
        if (string.IsNullOrEmpty(family)) throw new ArgumentException("family is required", nameof(family));
        if (fontBytes == null || fontBytes.Length == 0) throw new ArgumentException("fontBytes is empty", nameof(fontBytes));
        var face = Face.Load(fontBytes);
        lock (_fontsLock)
        {
            _registeredFonts[family] = face;
            _defaultFont ??= face;
        }
    }

    /// <summary>Clear all registered fonts. Primarily for tests.</summary>
    public static void ClearRegisteredFonts()
    {
        lock (_fontsLock) { _registeredFonts.Clear(); _defaultFont = null; }
    }

    private static Face? ResolveFont(string? fontFamilyValue)
    {
        if (!string.IsNullOrWhiteSpace(fontFamilyValue))
        {
            foreach (var raw in fontFamilyValue.Split(','))
            {
                string trimmed = raw.Trim().Trim('"', '\'');
                lock (_fontsLock)
                    if (_registeredFonts.TryGetValue(trimmed, out var face)) return face;
            }
        }
        lock (_fontsLock) return _defaultFont;
    }

    public static async ValueTask<bool> IsSvgAsync(IVipsSource source, CancellationToken cancellationToken = default)
    {
        var sniff = await source.SniffAsync(1024, cancellationToken);
        if (sniff.Length < 10) return false;
        string content = System.Text.Encoding.ASCII.GetString(sniff.Span);
        return content.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    public static async ValueTask<VipsImage?> LoadAsync(IVipsSource source, int width = 0, int height = 0, CancellationToken cancellationToken = default)
    {
        if (!await IsSvgAsync(source, cancellationToken)) return null;

        var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }

        return Render(ms.ToArray(), width, height);
    }

    public static async ValueTask<VipsImage?> LoadStreamingAsync(IVipsSource source, int width = 0, int height = 0, CancellationToken cancellationToken = default)
    {
        if (!await IsSvgAsync(source, cancellationToken)) return null;
        await Task.Yield();

        using var stream = source.AsStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return Render(ms.ToArray(), width, height);
    }

    // ---------- core render ----------

    private static VipsImage? Render(byte[] bytes, int requestedWidth, int requestedHeight)
    {
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(bytes);
            // Permissive — SVG in the wild has DTDs, mixed namespaces, etc.
            doc = XDocument.Load(ms, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root == null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            return null;

        // Intrinsic dimensions from width/height attributes, with viewBox fallback.
        int intrinsicW = ParsePxDim(root.Attribute("width")?.Value);
        int intrinsicH = ParsePxDim(root.Attribute("height")?.Value);
        double viewMinX = 0, viewMinY = 0, viewW = intrinsicW, viewH = intrinsicH;
        if (root.Attribute("viewBox")?.Value is { } vb)
        {
            var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out viewMinX) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out viewMinY) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out viewW) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out viewH))
            {
                if (intrinsicW <= 0) intrinsicW = (int)Math.Round(viewW);
                if (intrinsicH <= 0) intrinsicH = (int)Math.Round(viewH);
            }
        }
        if (intrinsicW <= 0) intrinsicW = 100;
        if (intrinsicH <= 0) intrinsicH = 100;

        int outW = requestedWidth > 0 ? requestedWidth : intrinsicW;
        int outH = requestedHeight > 0 ? requestedHeight : intrinsicH;

        // Root CTM: maps SVG user space to output pixels.
        // viewBox→pixel: scale(sx, sy) · translate(-viewMinX, -viewMinY).
        double sx = outW / (viewW > 0 ? viewW : intrinsicW);
        double sy = outH / (viewH > 0 ? viewH : intrinsicH);
        var rootCtm = Matrix2D.Scale(sx, sy).Compose(Matrix2D.Translate(-viewMinX, -viewMinY));

        // Pre-walk for paint-server defs (gradients) and clip-path defs.
        // Defs can be declared anywhere in the tree — typically inside
        // <defs> but the spec allows them at any depth — and may be
        // referenced before they appear in document order, so a single
        // pre-pass is the simplest correct model.
        var gradients = CollectGradients(root);
        var clipPaths = CollectClipPaths(root);
        var filters = CollectFilters(root);
        var masks = CollectMasks(root);

        var canvas = new RgbaBuffer(outW, outH);
        var rootStyle = SvgStyle.Default;
        RenderChildren(root, canvas, rootStyle, rootCtm, gradients, clipPaths, filters, masks);

        var pixels = canvas.Bytes;
        return new VipsImage
        {
            Width = outW,
            Height = outH,
            Bands = 4,
            BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => pixels),
        };
    }

    private static void RenderChildren(XElement parent, RgbaBuffer canvas, SvgStyle parentStyle,
                                       Matrix2D parentCtm,
                                       IReadOnlyDictionary<string, GradientPaintBase> gradients,
                                       IReadOnlyDictionary<string, XElement> clipPaths,
                                       IReadOnlyDictionary<string, XElement> filters,
                                       IReadOnlyDictionary<string, XElement> masks)
    {
        foreach (var el in parent.Elements())
        {
            var style = parentStyle.InheritFrom(el, gradients);
            string name = el.Name.LocalName.ToLowerInvariant();

            // Refuse loudly for features that need their own implementation phase.
            switch (name)
            {
                case "tspan":
                    throw new NotSupportedException("SVG <tspan> not yet supported (phase 4 follow-on; <text> base case is).");
                case "radialgradient":
                    // Collected; non-visual at top level. Falls through to dispatch below.
                    break;
                case "use":
                case "image":
                    throw new NotSupportedException($"SVG <{name}> not yet supported (phase 4).");
            }

            // Compose any element-local transform onto the parent CTM.
            var elCtm = parentCtm;
            if (el.Attribute("transform")?.Value is { } transformAttr)
            {
                var localM = SvgTransformParser.Parse(transformAttr);
                elCtm = parentCtm.Compose(localM);
            }

            // Filter: when present, render the element into an offscreen
            // buffer, apply the filter primitive(s), then composite onto the
            // main canvas. Spec order is "clip first, then filter" — the
            // clip mask we set below applies during the offscreen render.
            XElement? filterDef = null;
            if (TryResolveUrlRef(el.Attribute("filter")?.Value, out var filterId))
                filters.TryGetValue(filterId, out filterDef);

            RgbaBuffer renderTarget = canvas;
            RgbaBuffer? subCanvas = null;
            if (filterDef != null)
            {
                subCanvas = new RgbaBuffer(canvas.Width, canvas.Height);
                renderTarget = subCanvas;
            }

            // clip-path + mask setup on whichever target we'll draw into
            // (sub-canvas for filtered elements, main canvas otherwise).
            // Both contribute to the same per-pixel soft-alpha mask: 255
            // = pass fully, 0 = block. clip-path produces 0/255 from
            // geometry; mask produces 0..255 from rendered luminance × alpha.
            // Both stack multiplicatively, then BlendPixel respects the mask.
            byte[]? prevClip = renderTarget.ClipMask;
            byte[]? composedMask = prevClip;
            if (TryResolveUrlRef(el.Attribute("clip-path")?.Value, out var clipId) &&
                clipPaths.TryGetValue(clipId, out var clipDef))
            {
                var localMask = BuildClipMask(clipDef, renderTarget.Width, renderTarget.Height,
                                              elCtm, gradients, clipPaths, filters, masks);
                composedMask = IntersectClipMasks(composedMask, localMask);
            }
            if (TryResolveUrlRef(el.Attribute("mask")?.Value, out var maskId) &&
                masks.TryGetValue(maskId, out var maskDef))
            {
                var softMask = BuildSoftMask(maskDef, renderTarget.Width, renderTarget.Height,
                                             elCtm, gradients, clipPaths, filters, masks);
                composedMask = IntersectClipMasks(composedMask, softMask);
            }
            if (composedMask != prevClip) renderTarget.ClipMask = composedMask;

            try
            {
                switch (name)
                {
                    case "g":
                    case "svg":
                        RenderChildren(el, renderTarget, style, elCtm, gradients, clipPaths, filters, masks);
                        break;
                    case "rect":      RenderRect(el, renderTarget, style, elCtm); break;
                    case "circle":    RenderCircle(el, renderTarget, style, elCtm); break;
                    case "ellipse":   RenderEllipse(el, renderTarget, style, elCtm); break;
                    case "line":      RenderLine(el, renderTarget, style, elCtm); break;
                    case "polygon":   RenderPolygonEl(el, renderTarget, style, elCtm, closed: true); break;
                    case "polyline":  RenderPolygonEl(el, renderTarget, style, elCtm, closed: false); break;
                    case "path":      RenderPath(el, renderTarget, style, elCtm); break;
                    case "text":      RenderText(el, renderTarget, style, elCtm); break;
                    case "defs":
                    case "title":
                    case "desc":
                    case "metadata":
                    case "lineargradient":
                    case "radialgradient":
                    case "clippath":
                    case "filter":
                    case "mask":
                        // Collected up-front (gradients, clipPaths, filters, masks) or non-visual.
                        break;
                    default:
                        // Unknown / unimplemented element: silently skip rather than
                        // throw. SVGs in the wild often embed editor metadata
                        // (inkscape:*, sodipodi:*) that we don't need to render.
                        break;
                }
            }
            finally
            {
                renderTarget.ClipMask = prevClip;
            }

            if (subCanvas != null)
            {
                ApplyFilter(filterDef!, subCanvas);
                canvas.CompositeOver(subCanvas);
            }
        }
    }

    // ---------- gradient collection ----------

    /// <summary>
    /// Pre-pass: walk the SVG tree and build a lookup of <c>id</c> →
    /// <see cref="GradientPaintBase"/>. Resolves <c>xlink:href</c> /
    /// <c>href</c> chains per the SVG spec: a gradient may inherit
    /// attributes (and, if it has no <c>&lt;stop&gt;</c> children of its
    /// own, the stop list) from a referenced gradient. Closest-set wins
    /// per attribute. Chains beyond a fixed depth are clipped (cycle
    /// protection).
    /// </summary>
    private static IReadOnlyDictionary<string, GradientPaintBase> CollectGradients(XElement root)
    {
        // Step 1: index every linear/radial gradient by id so href targets
        // are resolvable regardless of document order.
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
        {
            string ln = el.Name.LocalName;
            if (!ln.Equals("linearGradient", StringComparison.OrdinalIgnoreCase) &&
                !ln.Equals("radialGradient", StringComparison.OrdinalIgnoreCase)) continue;
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id)) byId[id] = el;
        }

        var dict = new Dictionary<string, GradientPaintBase>(StringComparer.Ordinal);
        foreach (var (id, el) in byId)
        {
            var chain = ResolveGradientChain(el, byId);
            // The chain's *first* element is the gradient being defined;
            // each subsequent one is the (xlink:)href target. Element type
            // (linear vs radial) is taken from the head of the chain — the
            // referenced gradient's element type only contributes stops +
            // attributes, never overrides which "kind" of gradient this is.
            bool isLinear = chain[0].Name.LocalName.Equals(
                "linearGradient", StringComparison.OrdinalIgnoreCase);

            // gradientUnits default is objectBoundingBox per spec; if no
            // gradient in the chain sets it explicitly, treat as bbox.
            string? gu = GetInheritedAttr(chain, "gradientUnits");
            bool obb = gu == null || gu.Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase);

            Matrix2D gradientTransform = GetInheritedAttr(chain, "gradientTransform") is { } gtRaw
                ? SvgTransformParser.Parse(gtRaw)
                : Matrix2D.Identity;

            SpreadMethod spread = GetInheritedAttr(chain, "spreadMethod")?.ToLowerInvariant() switch
            {
                "reflect" => SpreadMethod.Reflect,
                "repeat"  => SpreadMethod.Repeat,
                _          => SpreadMethod.Pad,
            };

            var stops = ParseInheritedStops(chain);
            if (stops.Count < 2) continue;

            if (isLinear)
            {
                // Spec defaults for linearGradient: x1=0, y1=0, x2=1, y2=0
                // (left-to-right in objectBoundingBox; or "1px" in userSpaceOnUse).
                double x1 = ParseGradientCoord(GetInheritedAttr(chain, "x1"), defaultValue: 0);
                double y1 = ParseGradientCoord(GetInheritedAttr(chain, "y1"), defaultValue: 0);
                double x2 = ParseGradientCoord(GetInheritedAttr(chain, "x2"), defaultValue: 1);
                double y2 = ParseGradientCoord(GetInheritedAttr(chain, "y2"), defaultValue: 0);
                dict[id] = new LinearGradientPaint(x1, y1, x2, y2, stops.ToArray(),
                    opacity: 1.0, objectBoundingBox: obb,
                    gradientTransform: gradientTransform, spread: spread);
            }
            else
            {
                // Spec defaults for radialGradient: cx = cy = r = 0.5 (in bbox
                // units); fx/fy default to cx/cy.
                double cx = ParseGradientCoord(GetInheritedAttr(chain, "cx"), defaultValue: 0.5);
                double cy = ParseGradientCoord(GetInheritedAttr(chain, "cy"), defaultValue: 0.5);
                double r  = ParseGradientCoord(GetInheritedAttr(chain, "r"),  defaultValue: 0.5);
                string? fxRaw = GetInheritedAttr(chain, "fx");
                string? fyRaw = GetInheritedAttr(chain, "fy");
                double fx = fxRaw != null ? ParseGradientCoord(fxRaw, defaultValue: cx) : cx;
                double fy = fyRaw != null ? ParseGradientCoord(fyRaw, defaultValue: cy) : cy;
                dict[id] = new RadialGradientPaint(cx, cy, r, fx, fy, stops.ToArray(),
                    opacity: 1.0, objectBoundingBox: obb,
                    gradientTransform: gradientTransform, spread: spread);
            }
        }
        return dict;
    }

    /// <summary>
    /// Walk the <c>xlink:href</c> / <c>href</c> chain starting at
    /// <paramref name="head"/>, returning the resolved chain in order
    /// [head, target, target-of-target, ...]. Stops at unresolvable
    /// references, the first cycle, or a fixed depth (defensive). The
    /// returned list always contains at least <paramref name="head"/>.
    /// </summary>
    private static List<XElement> ResolveGradientChain(
        XElement head, IReadOnlyDictionary<string, XElement> byId)
    {
        const int MaxDepth = 10;
        var chain = new List<XElement>(2) { head };
        var seen = new HashSet<XElement> { head };
        XElement cur = head;
        for (int i = 0; i < MaxDepth; i++)
        {
            string? href = cur.Attribute(XlinkHref)?.Value ?? cur.Attribute("href")?.Value;
            if (string.IsNullOrEmpty(href) || href[0] != '#') break;
            string targetId = href.Substring(1);
            if (!byId.TryGetValue(targetId, out var target)) break;
            if (!seen.Add(target)) break;          // cycle
            chain.Add(target);
            cur = target;
        }
        return chain;
    }

    /// <summary>XLink namespace for the legacy <c>xlink:href</c> attribute.</summary>
    private static readonly System.Xml.Linq.XName XlinkHref =
        System.Xml.Linq.XName.Get("href", "http://www.w3.org/1999/xlink");

    /// <summary>
    /// Closest-set wins: return the first occurrence of <paramref name="name"/>
    /// along the chain, or <c>null</c> if no element sets it.
    /// </summary>
    private static string? GetInheritedAttr(List<XElement> chain, string name)
    {
        foreach (var el in chain)
            if (el.Attribute(name)?.Value is { } v) return v;
        return null;
    }

    /// <summary>
    /// Find the first gradient in the chain that has its own <c>&lt;stop&gt;</c>
    /// children, and parse them. SVG inheritance rule: stops are an
    /// all-or-nothing inheritance — if the referencing gradient defines
    /// any stops, those are used exclusively; otherwise we fall back to
    /// the referenced gradient's stops.
    /// </summary>
    private static List<GradientStop> ParseInheritedStops(List<XElement> chain)
    {
        foreach (var el in chain)
        {
            var stops = new List<GradientStop>();
            foreach (var stop in el.Elements())
            {
                if (!stop.Name.LocalName.Equals("stop", StringComparison.OrdinalIgnoreCase)) continue;
                double offset = ParseStopOffset(stop.Attribute("offset")?.Value);
                string? colorRaw = GetStyleValue(stop, "stop-color");
                var color = colorRaw != null ? (ParseSolidColor(colorRaw.Trim()) ?? new Rgba(0, 0, 0, 255))
                                              : new Rgba(0, 0, 0, 255);
                double stopOpacity = ParseOpacity(GetStyleValue(stop, "stop-opacity"), 1.0);
                if (stopOpacity < 1)
                    color = new Rgba(color.R, color.G, color.B, (byte)Math.Round(color.A * stopOpacity));
                stops.Add(new GradientStop(offset, color));
            }
            if (stops.Count >= 2)
            {
                stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                return stops;
            }
        }
        return new List<GradientStop>();
    }

    /// <summary>Parse a gradient endpoint coordinate (cx/cy/r/x1/y1/x2/y2/fx/fy).
    /// Accepts "50%" → 0.5 — useful for objectBoundingBox-mode gradients where
    /// the spec encourages percentage syntax.</summary>
    private static double ParseGradientCoord(string? raw, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        raw = raw.Trim();
        if (raw.EndsWith("%"))
        {
            if (double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return pct / 100.0;
            return defaultValue;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    // ---------- clip-path collection + mask building ----------

    private static IReadOnlyDictionary<string, XElement> CollectFilters(XElement root)
    {
        var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!el.Name.LocalName.Equals("filter", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id)) dict[id] = el;
        }
        return dict;
    }

    /// <summary>
    /// Apply the filter's primitive chain. Phase 2: supports
    /// <c>feGaussianBlur</c>, <c>feOffset</c>, <c>feFlood</c>, and
    /// <c>feMerge</c>/<c>feMergeNode</c> with full <c>in</c>/<c>result</c>
    /// chain plumbing — enough to express the standard drop-shadow
    /// pattern (blur SourceAlpha → offset → merge with SourceGraphic).
    ///
    /// <para>The named-result map starts with the built-in inputs
    /// <c>SourceGraphic</c> (the buffer the caller passed in) and
    /// <c>SourceAlpha</c> (same alpha channel, RGB forced to 0). Each
    /// primitive resolves its <c>in</c> attribute against the map (or
    /// defaults to the previous primitive's result, which is itself
    /// SourceGraphic for the first primitive). After the chain runs,
    /// the final result's bytes are copied back into the caller's
    /// buffer so the existing in-place composite contract is preserved.</para>
    /// </summary>
    private static void ApplyFilter(XElement filterDef, RgbaBuffer source)
    {
        int w = source.Width, h = source.Height;

        // Named-result map. "SourceGraphic" is a clone of the caller's
        // buffer because feFlood / feMerge / feOffset produce fresh
        // buffers and we don't want them to alias the input.
        var results = new Dictionary<string, RgbaBuffer>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceGraphic"] = CloneBuffer(source),
            ["SourceAlpha"]   = MakeSourceAlphaBuffer(source),
        };
        RgbaBuffer lastResult = results["SourceGraphic"];

        RgbaBuffer ResolveIn(string? key) =>
            (key != null && results.TryGetValue(key, out var b)) ? b : lastResult;

        foreach (var prim in filterDef.Elements())
        {
            string n = prim.Name.LocalName.ToLowerInvariant();
            RgbaBuffer output;
            switch (n)
            {
                case "fegaussianblur":
                {
                    output = CloneBuffer(ResolveIn(prim.Attribute("in")?.Value));
                    ParseStdDeviation(prim.Attribute("stdDeviation")?.Value,
                                      out double sigmaX, out double sigmaY);
                    GaussianBlur(output, sigmaX, sigmaY);
                    break;
                }
                case "feoffset":
                {
                    double dx = ParseNumber(prim.Attribute("dx")?.Value);
                    double dy = ParseNumber(prim.Attribute("dy")?.Value);
                    output = ApplyOffsetPrimitive(ResolveIn(prim.Attribute("in")?.Value), dx, dy);
                    break;
                }
                case "feflood":
                {
                    var color = ParseFloodColor(prim);
                    output = MakeFloodBuffer(w, h, color);
                    break;
                }
                case "femerge":
                {
                    output = ApplyMergePrimitive(prim, w, h, results, lastResult);
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"SVG filter primitive <{n}> not yet supported. " +
                        "Phase 2 covers feGaussianBlur, feOffset, feFlood, feMerge.");
            }

            if (prim.Attribute("result")?.Value is { } resultName)
                results[resultName] = output;
            lastResult = output;
        }

        // Push the final pipeline output into the caller's buffer — the
        // outer dispatch composites that buffer onto the main canvas.
        if (!ReferenceEquals(lastResult, source))
            Buffer.BlockCopy(lastResult.Bytes, 0, source.Bytes, 0, source.Bytes.Length);
    }

    private static RgbaBuffer CloneBuffer(RgbaBuffer src)
    {
        var dst = new RgbaBuffer(src.Width, src.Height);
        Buffer.BlockCopy(src.Bytes, 0, dst.Bytes, 0, src.Bytes.Length);
        return dst;
    }

    /// <summary>
    /// Built-in <c>SourceAlpha</c> input: same shape and alpha as the
    /// source, RGB forced to 0. Used as the typical drop-shadow base —
    /// blur it to get a translucent black silhouette.
    /// </summary>
    private static RgbaBuffer MakeSourceAlphaBuffer(RgbaBuffer src)
    {
        var dst = new RgbaBuffer(src.Width, src.Height);
        for (int i = 0; i < src.Bytes.Length; i += 4)
            dst.Bytes[i + 3] = src.Bytes[i + 3];
        // R, G, B remain zero from the initial allocation.
        return dst;
    }

    /// <summary>
    /// <c>feOffset</c>: shift pixels by <c>(dx, dy)</c>. Pixels mapped
    /// from outside the source area become transparent (alpha = 0).
    /// Integer-rounded offset for phase 2; sub-pixel offsets would need
    /// bilinear filtering and are deferred.
    /// </summary>
    private static RgbaBuffer ApplyOffsetPrimitive(RgbaBuffer src, double dx, double dy)
    {
        int idx = (int)Math.Round(dx);
        int idy = (int)Math.Round(dy);
        var dst = new RgbaBuffer(src.Width, src.Height);
        int w = src.Width, h = src.Height;
        for (int y = 0; y < h; y++)
        {
            int sy = y - idy;
            if (sy < 0 || sy >= h) continue;
            for (int x = 0; x < w; x++)
            {
                int sx = x - idx;
                if (sx < 0 || sx >= w) continue;
                int si = (sy * w + sx) * 4;
                int di = (y  * w + x) * 4;
                dst.Bytes[di + 0] = src.Bytes[si + 0];
                dst.Bytes[di + 1] = src.Bytes[si + 1];
                dst.Bytes[di + 2] = src.Bytes[si + 2];
                dst.Bytes[di + 3] = src.Bytes[si + 3];
            }
        }
        return dst;
    }

    /// <summary>
    /// <c>feFlood</c>: produce a buffer filled entirely with the supplied
    /// colour (modulated by flood-opacity). Phase-2 simplification: fills
    /// the full canvas rather than honouring the filter primitive region's
    /// x/y/width/height — the primary use case (shadow tint paired with
    /// a feComposite/feMerge mask) doesn't depend on the bounding box.
    /// </summary>
    private static RgbaBuffer MakeFloodBuffer(int w, int h, Rgba color)
    {
        var dst = new RgbaBuffer(w, h);
        for (int i = 0; i < dst.Bytes.Length; i += 4)
        {
            dst.Bytes[i + 0] = color.R;
            dst.Bytes[i + 1] = color.G;
            dst.Bytes[i + 2] = color.B;
            dst.Bytes[i + 3] = color.A;
        }
        return dst;
    }

    /// <summary>
    /// <c>feMerge</c>: composite each <c>&lt;feMergeNode in=...&gt;</c>
    /// child onto a fresh transparent buffer in document order (first
    /// child = bottom layer). The result is the standard Porter-Duff
    /// "over" stack of all the named inputs.
    /// </summary>
    private static RgbaBuffer ApplyMergePrimitive(XElement prim, int w, int h,
        IReadOnlyDictionary<string, RgbaBuffer> results, RgbaBuffer fallback)
    {
        var dst = new RgbaBuffer(w, h);
        foreach (var child in prim.Elements())
        {
            if (!child.Name.LocalName.Equals("feMergeNode", StringComparison.OrdinalIgnoreCase))
                continue;
            string? inAttr = child.Attribute("in")?.Value;
            RgbaBuffer layer = (inAttr != null && results.TryGetValue(inAttr, out var b)) ? b : fallback;
            dst.CompositeOver(layer);
        }
        return dst;
    }

    /// <summary>
    /// Parse <c>flood-color</c> + <c>flood-opacity</c> from an
    /// <c>feFlood</c> primitive, applying opacity into the colour's
    /// alpha channel.
    /// </summary>
    private static Rgba ParseFloodColor(XElement prim)
    {
        string? colorRaw = GetStyleValue(prim, "flood-color");
        var color = colorRaw != null
            ? (ParseSolidColor(colorRaw.Trim()) ?? new Rgba(0, 0, 0, 255))
            : new Rgba(0, 0, 0, 255);
        double opacity = ParseOpacity(GetStyleValue(prim, "flood-opacity"), 1.0);
        if (opacity < 1)
            color = new Rgba(color.R, color.G, color.B, (byte)Math.Round(color.A * opacity));
        return color;
    }

    private static void ParseStdDeviation(string? raw, out double sigmaX, out double sigmaY)
    {
        sigmaX = 0; sigmaY = 0;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sx))
            sigmaX = Math.Max(0, sx);
        if (parts.Length >= 2 &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sy))
            sigmaY = Math.Max(0, sy);
        else
            sigmaY = sigmaX;
    }

    /// <summary>
    /// Two-pass separable Gaussian blur on an RGBA buffer. Uses
    /// premultiplied alpha during the convolution so semi-transparent
    /// edges don't bleed color from "off the edge" (which would have
    /// zero alpha but non-zero RGB after color-only blurring).
    /// </summary>
    private static void GaussianBlur(RgbaBuffer buffer, double sigmaX, double sigmaY)
    {
        if (sigmaX <= 0 && sigmaY <= 0) return;

        int w = buffer.Width, h = buffer.Height;
        var pre = new byte[buffer.Bytes.Length];
        for (int i = 0; i < buffer.Bytes.Length; i += 4)
        {
            int a = buffer.Bytes[i + 3];
            pre[i + 0] = (byte)((buffer.Bytes[i + 0] * a + 127) / 255);
            pre[i + 1] = (byte)((buffer.Bytes[i + 1] * a + 127) / 255);
            pre[i + 2] = (byte)((buffer.Bytes[i + 2] * a + 127) / 255);
            pre[i + 3] = (byte)a;
        }

        if (sigmaX > 0)
        {
            var kernel = BuildGaussianKernel(sigmaX);
            var tmp = new byte[pre.Length];
            ConvolveAxis(pre, tmp, w, h, kernel, horizontal: true);
            Array.Copy(tmp, pre, pre.Length);
        }
        if (sigmaY > 0)
        {
            var kernel = BuildGaussianKernel(sigmaY);
            var tmp = new byte[pre.Length];
            ConvolveAxis(pre, tmp, w, h, kernel, horizontal: false);
            Array.Copy(tmp, pre, pre.Length);
        }

        // Un-premultiply.
        for (int i = 0; i < buffer.Bytes.Length; i += 4)
        {
            int a = pre[i + 3];
            if (a == 0)
            {
                buffer.Bytes[i + 0] = 0; buffer.Bytes[i + 1] = 0;
                buffer.Bytes[i + 2] = 0; buffer.Bytes[i + 3] = 0;
            }
            else
            {
                buffer.Bytes[i + 0] = (byte)Math.Min(255, pre[i + 0] * 255 / a);
                buffer.Bytes[i + 1] = (byte)Math.Min(255, pre[i + 1] * 255 / a);
                buffer.Bytes[i + 2] = (byte)Math.Min(255, pre[i + 2] * 255 / a);
                buffer.Bytes[i + 3] = (byte)a;
            }
        }
    }

    private static double[] BuildGaussianKernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var weights = new double[2 * radius + 1];
        double twoSigmaSq = 2 * sigma * sigma;
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double w = Math.Exp(-(i * i) / twoSigmaSq);
            weights[i + radius] = w;
            sum += w;
        }
        double invSum = 1.0 / sum;
        for (int i = 0; i < weights.Length; i++) weights[i] *= invSum;
        return weights;
    }

    private static void ConvolveAxis(byte[] src, byte[] dst, int w, int h, double[] kernel, bool horizontal)
    {
        int radius = kernel.Length / 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int sx = horizontal ? Math.Clamp(x + i, 0, w - 1) : x;
                    int sy = horizontal ? y : Math.Clamp(y + i, 0, h - 1);
                    int idx = (sy * w + sx) * 4;
                    double k = kernel[i + radius];
                    r += src[idx + 0] * k;
                    g += src[idx + 1] * k;
                    b += src[idx + 2] * k;
                    a += src[idx + 3] * k;
                }
                int outIdx = (y * w + x) * 4;
                dst[outIdx + 0] = (byte)Math.Round(Math.Clamp(r, 0, 255));
                dst[outIdx + 1] = (byte)Math.Round(Math.Clamp(g, 0, 255));
                dst[outIdx + 2] = (byte)Math.Round(Math.Clamp(b, 0, 255));
                dst[outIdx + 3] = (byte)Math.Round(Math.Clamp(a, 0, 255));
            }
        }
    }

    private static IReadOnlyDictionary<string, XElement> CollectClipPaths(XElement root)
    {
        var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!el.Name.LocalName.Equals("clipPath", StringComparison.OrdinalIgnoreCase))
                continue;
            if (el.Attribute("clipPathUnits") is { } u &&
                u.Value.Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase))
            {
                // Defer objectBoundingBox to a follow-up; skip this def so its
                // referencer gets the "unresolved" fallback (renders unclipped).
                continue;
            }
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id)) dict[id] = el;
        }
        return dict;
    }

    /// <summary>
    /// Parse <c>url(#id)</c> and yield the bare id. Returns false for any
    /// other syntax (none, currentColor, etc.).
    /// </summary>
    private static bool TryResolveUrlRef(string? raw, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (!raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return false;
        int hash = raw.IndexOf('#'); int close = raw.IndexOf(')');
        if (hash < 0 || close <= hash) return false;
        id = raw[(hash + 1)..close].Trim();
        return id.Length > 0;
    }

    /// <summary>
    /// Render the clipPath's children onto a fresh canvas-sized buffer using
    /// opaque black fill, then collapse the alpha channel into a 1-byte-per-pixel
    /// soft mask: 0 = outside (blocked), 255 = inside (passes fully). The
    /// clipPath shares the referencing element's CTM (userSpaceOnUse, the
    /// default and only supported form).
    /// </summary>
    private static byte[] BuildClipMask(XElement clipDef, int width, int height,
                                       Matrix2D ctm,
                                       IReadOnlyDictionary<string, GradientPaintBase> gradients,
                                       IReadOnlyDictionary<string, XElement> clipPaths,
                                       IReadOnlyDictionary<string, XElement> filters,
                                       IReadOnlyDictionary<string, XElement> masks)
    {
        var off = new RgbaBuffer(width, height);
        // Force opaque black fill regardless of the clip shapes' authored fill —
        // we only care about the coverage mask, not their colors.
        var clipStyle = new SvgStyle(new SolidPaint(new Rgba(0, 0, 0, 255)), null, 1.0);
        RenderChildren(clipDef, off, clipStyle, ctm, gradients, clipPaths, filters, masks);

        var mask = new byte[width * height];
        var bytes = off.Bytes;
        for (int i = 0; i < mask.Length; i++)
            mask[i] = bytes[i * 4 + 3] > 0 ? (byte)255 : (byte)0;
        return mask;
    }

    /// <summary>
    /// Pre-pass: collect <c>&lt;mask&gt;</c> elements by id. Same shape as
    /// <see cref="CollectClipPaths"/> — masks may appear anywhere in the
    /// tree and be referenced before their definition in document order.
    /// </summary>
    private static IReadOnlyDictionary<string, XElement> CollectMasks(XElement root)
    {
        var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!el.Name.LocalName.Equals("mask", StringComparison.OrdinalIgnoreCase)) continue;
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id)) dict[id] = el;
        }
        return dict;
    }

    /// <summary>
    /// Render a mask definition's children to an offscreen buffer using
    /// the *referencing element's* style + CTM, then convert each pixel
    /// to a soft alpha value via the SVG 1.1 luminance formula:
    /// <c>L = 0.2125·R + 0.7154·G + 0.0721·B</c> (Rec. 709 weights), and
    /// the per-pixel mask value is <c>(L × α / 255)</c>. White-opaque
    /// pixels pass through fully (mask = 255); black or transparent pixels
    /// block fully (mask = 0).
    ///
    /// <para>Phase-1 scope: <c>maskContentUnits="userSpaceOnUse"</c>
    /// (default), <c>maskUnits="objectBoundingBox"</c> (default — but we
    /// just render to the full canvas, which is correct when the mask
    /// covers the visible area). x/y/width/height attributes ignored.</para>
    /// </summary>
    private static byte[] BuildSoftMask(XElement maskDef, int width, int height,
                                       Matrix2D ctm,
                                       IReadOnlyDictionary<string, GradientPaintBase> gradients,
                                       IReadOnlyDictionary<string, XElement> clipPaths,
                                       IReadOnlyDictionary<string, XElement> filters,
                                       IReadOnlyDictionary<string, XElement> masks)
    {
        var off = new RgbaBuffer(width, height);
        // Mask content inherits the default style — authors set explicit
        // fill colours inside the <mask>, and the luminance of those colours
        // drives the per-pixel mask value.
        RenderChildren(maskDef, off, SvgStyle.Default, ctm, gradients, clipPaths, filters, masks);

        var result = new byte[width * height];
        var bytes = off.Bytes;
        for (int i = 0; i < result.Length; i++)
        {
            int j = i * 4;
            // Rec. 709 luminance × alpha; rounded to nearest byte.
            int r = bytes[j + 0];
            int g = bytes[j + 1];
            int b = bytes[j + 2];
            int a = bytes[j + 3];
            int l = (54 * r + 183 * g + 19 * b + 128) >> 8;   // ≈ 0.2125/0.7154/0.0721 × 256
            result[i] = (byte)((l * a + 127) / 255);
        }
        return result;
    }

    /// <summary>
    /// Combine two soft alpha masks (0..255) by multiplication, mirroring
    /// the layered semantics of clip-path + mask in SVG: both must pass
    /// a pixel for the underlying paint to land. <c>(a × b) / 255</c>
    /// rounded to nearest.
    /// </summary>
    private static byte[]? IntersectClipMasks(byte[]? outer, byte[]? inner)
    {
        if (outer == null) return inner;
        if (inner == null) return outer;
        if (outer.Length != inner.Length) return outer;  // defensive — shouldn't happen
        var result = new byte[outer.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)((outer[i] * inner[i] + 127) / 255);
        return result;
    }

    private static double ParseStopOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        if (raw.EndsWith("%"))
        {
            if (double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return Math.Clamp(pct / 100.0, 0, 1);
            return 0;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 1) : 0;
    }

    // ---------- shape renderers ----------

    private static void RenderRect(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        double x = ParseNumber(el.Attribute("x")?.Value);
        double y = ParseNumber(el.Attribute("y")?.Value);
        double w = ParseNumber(el.Attribute("width")?.Value);
        double h = ParseNumber(el.Attribute("height")?.Value);
        if (w <= 0 || h <= 0) return;

        if (el.Attribute("rx") != null || el.Attribute("ry") != null)
            throw new NotSupportedException("SVG <rect> rx/ry (rounded corners) not yet supported (phase 2).");

        // 4 corners in user space → transform via CTM. Under non-translation
        // CTMs the rect becomes a parallelogram (or arbitrary quad with skew);
        // FillPolygon handles both. For the common axis-aligned case we use
        // FillRect for a small perf win.
        var corners = new (double X, double Y)[]
        {
            ctm.Apply((x, y)),
            ctm.Apply((x + w, y)),
            ctm.Apply((x + w, y + h)),
            ctm.Apply((x, y + h)),
        };

        if (style.FillColor is SolidPaint sf)
        {
            if (ctm.IsAxisAligned)
                canvas.FillRect(corners[0].X, corners[0].Y, corners[2].X, corners[2].Y, sf.Color);
            else
                canvas.FillPolygon(corners, sf.Color);
        }
        else if (style.FillColor is { } fp)
        {
            var bbox = new ShapeBBox(x, y, x + w, y + h);
            canvas.FillPolygonSampler(corners, fp.PrepareSampler(ctm, bbox));
        }
        if (style.StrokeColor is { } strokePaint && style.StrokeWidth > 0)
        {
            double sw = style.StrokeWidth * ctm.ScaleMagnitude;
            if (strokePaint is SolidPaint ssp)
                StrokePolygon(canvas, corners, ssp.Color, sw, closed: true);
            else
            {
                var bbox = new ShapeBBox(x, y, x + w, y + h);
                StrokePolygonSampler(canvas, corners, strokePaint.PrepareSampler(ctm, bbox), sw, closed: true);
            }
        }
    }

    private static void RenderCircle(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        double cx = ParseNumber(el.Attribute("cx")?.Value);
        double cy = ParseNumber(el.Attribute("cy")?.Value);
        double r  = ParseNumber(el.Attribute("r")?.Value);
        if (r <= 0) return;
        RenderEllipseCore(canvas, style, cx, cy, r, r, ctm);
    }

    private static void RenderEllipse(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        double cx = ParseNumber(el.Attribute("cx")?.Value);
        double cy = ParseNumber(el.Attribute("cy")?.Value);
        double rx = ParseNumber(el.Attribute("rx")?.Value);
        double ry = ParseNumber(el.Attribute("ry")?.Value);
        if (rx <= 0 || ry <= 0) return;
        RenderEllipseCore(canvas, style, cx, cy, rx, ry, ctm);
    }

    private static void RenderEllipseCore(RgbaBuffer canvas, SvgStyle style,
                                          double cx, double cy, double rx, double ry, Matrix2D ctm)
    {
        const int Segments = 64;
        var pts = new (double X, double Y)[Segments];
        for (int i = 0; i < Segments; i++)
        {
            double angle = 2 * Math.PI * i / Segments;
            // Tessellate in user space first, then transform — this naturally
            // turns a circle into the right ellipse shape under any affine CTM.
            pts[i] = ctm.Apply((cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
        }
        if (style.FillColor is SolidPaint solidFill)
            canvas.FillPolygon(pts, solidFill.Color);
        else if (style.FillColor is { } fp)
        {
            var bbox = new ShapeBBox(cx - rx, cy - ry, cx + rx, cy + ry);
            canvas.FillPolygonSampler(pts, fp.PrepareSampler(ctm, bbox));
        }
        if (style.StrokeColor is { } strokePaint && style.StrokeWidth > 0)
        {
            double sw = style.StrokeWidth * ctm.ScaleMagnitude;
            if (strokePaint is SolidPaint solidStroke)
                StrokePolygon(canvas, pts, solidStroke.Color, sw, closed: true);
            else
            {
                var bbox = new ShapeBBox(cx - rx, cy - ry, cx + rx, cy + ry);
                StrokePolygonSampler(canvas, pts, strokePaint.PrepareSampler(ctm, bbox), sw, closed: true);
            }
        }
    }

    private static void RenderLine(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        if (style.StrokeColor is null || style.StrokeWidth <= 0) return;
        double x1 = ParseNumber(el.Attribute("x1")?.Value);
        double y1 = ParseNumber(el.Attribute("y1")?.Value);
        double x2 = ParseNumber(el.Attribute("x2")?.Value);
        double y2 = ParseNumber(el.Attribute("y2")?.Value);
        var p1 = ctm.Apply((x1, y1));
        var p2 = ctm.Apply((x2, y2));
        double sw = style.StrokeWidth * ctm.ScaleMagnitude;
        if (style.StrokeColor is SolidPaint sc)
            StrokeSegment(canvas, p1.X, p1.Y, p2.X, p2.Y, sc.Color, sw);
        else
        {
            var bbox = new ShapeBBox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            StrokeSegmentSampler(canvas, p1.X, p1.Y, p2.X, p2.Y,
                style.StrokeColor.PrepareSampler(ctm, bbox), sw);
        }
    }

    private static void RenderPolygonEl(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm, bool closed)
    {
        var raw = el.Attribute("points")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return;

        var nums = new List<double>();
        foreach (var tok in raw.Split(new[] { ' ', ',', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) nums.Add(v);
        if (nums.Count < 4) return;

        int n = nums.Count / 2;
        var pts = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
            pts[i] = ctm.Apply((nums[i * 2], nums[i * 2 + 1]));

        if (closed && style.FillColor is SolidPaint solidFill)
            canvas.FillPolygon(pts, solidFill.Color);
        else if (closed && style.FillColor is { } fp)
        {
            var bbox = BBoxFromNums(nums);
            canvas.FillPolygonSampler(pts, fp.PrepareSampler(ctm, bbox));
        }
        if (style.StrokeColor is { } strokePaint && style.StrokeWidth > 0)
        {
            double sw = style.StrokeWidth * ctm.ScaleMagnitude;
            if (strokePaint is SolidPaint solidStroke)
                StrokePolygon(canvas, pts, solidStroke.Color, sw, closed);
            else
            {
                var bbox = BBoxFromNums(nums);
                StrokePolygonSampler(canvas, pts, strokePaint.PrepareSampler(ctm, bbox), sw, closed);
            }
        }
    }

    private static ShapeBBox BBoxFromNums(List<double> nums)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            double x = nums[i], y = nums[i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        return new ShapeBBox(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Render an SVG <c>&lt;text&gt;</c> element by shaping its text content
    /// through CosmoFonts and filling each glyph's outline with the
    /// element's fill colour / gradient.
    ///
    /// <para>Phase-1 scope: x/y position, font-family lookup (registered
    /// via <see cref="RegisterFont"/>), font-size, solid + gradient fill.
    /// No tspan, no text-anchor non-default, no text-decoration, no
    /// textPath, no kerning beyond what HarfBuzz gives.</para>
    /// </summary>
    private static void RenderText(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        var face = ResolveFont(GetStyleValue(el, "font-family"));
        if (face == null)
            throw new NotSupportedException(
                "SVG <text>: no font is registered. Call VipsSvgLoader.RegisterFont(family, fontBytes) " +
                "before loading SVGs that contain <text> elements.");

        double x = ParseNumber(el.Attribute("x")?.Value);
        double y = ParseNumber(el.Attribute("y")?.Value);
        double fontSize = ParseNumber(GetStyleValue(el, "font-size"));
        if (fontSize <= 0) fontSize = 16;
        string text = el.Value ?? string.Empty;
        if (text.Length == 0) return;

        // Collect glyph outlines in user space (CosmoFonts emits font units
        // scaled by SizePt; we treat SizePt as user-space units so the
        // output sits on the SVG canvas at the authored position).
        // CosmoFonts treats Origin.Y as the *top* of the line; the baseline
        // lands at Origin.Y + ascender × scale. SVG <text y=...> is the
        // baseline, so back-compute the top-of-line position.
        float scale = (float)fontSize / face.UnitsPerEm;
        float originY = (float)y - face.Ascender * scale;
        var subpaths = new List<List<(double X, double Y)>>();
        var collector = new SvgGlyphCollector(subpaths);
        var options = new TextOptions(face, (float)fontSize)
        {
            Origin = new Vector2((float)x, originY),
        };
        TextRenderer.RenderTextTo(collector, text, options);
        if (subpaths.Count == 0) return;

        // Map subpaths through the CTM.
        var mapped = new List<List<(double X, double Y)>>(subpaths.Count);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var sp in subpaths)
        {
            if (sp.Count < 2) continue;
            var s = new List<(double X, double Y)>(sp.Count);
            foreach (var p in sp)
            {
                s.Add(ctm.Apply(p));
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            mapped.Add(s);
        }
        if (mapped.Count == 0) return;
        var bbox = new ShapeBBox(minX, minY, maxX, maxY);

        // Fill (gradients supported via the existing sampler path).
        if (style.FillColor is SolidPaint solidFill)
            canvas.FillSubpaths(mapped, solidFill.Color);
        else if (style.FillColor is { } fp)
            canvas.FillSubpathsSampler(mapped, fp.PrepareSampler(ctm, bbox));
        // Stroke on text deferred — needs glyph outline offsetting, not just
        // a quad per segment; throw if requested for now.
        if (style.StrokeColor is not null && style.StrokeWidth > 0)
            throw new NotSupportedException("SVG stroke on <text> not yet supported (phase 4 follow-on).");
    }

    /// <summary>
    /// Adapter from CosmoFonts' <see cref="IGlyphRenderer"/> outline-callback
    /// model to our list-of-subpaths polygon-fill format. Béziers are
    /// flattened via the same De Casteljau machinery as &lt;path&gt; data.
    /// </summary>
    private sealed class SvgGlyphCollector : IGlyphRenderer
    {
        private readonly List<List<(double X, double Y)>> _subpaths;
        private List<(double X, double Y)>? _current;

        public SvgGlyphCollector(List<List<(double X, double Y)>> subpaths) { _subpaths = subpaths; }

        public void BeginText(RectF bounds) { }
        public void BeginGlyph(RectF bounds, GlyphRendererParameters parameters) { }
        public void EndGlyph() { }
        public void EndText() { }

        public void BeginFigure() { _current = new List<(double X, double Y)>(8); }
        public void EndFigure()
        {
            if (_current != null && _current.Count >= 2) _subpaths.Add(_current);
            _current = null;
        }

        public void MoveTo(Vector2 p)
        {
            // CosmoFonts emits BeginFigure → MoveTo; if the caller skipped
            // BeginFigure, treat MoveTo as the figure opener.
            if (_current == null) _current = new List<(double X, double Y)>(8);
            // Within an open figure, additional MoveTo would start a new
            // sub-figure (rare in glyphs but spec-allowed).
            else if (_current.Count > 0)
            {
                if (_current.Count >= 2) _subpaths.Add(_current);
                _current = new List<(double X, double Y)>(8);
            }
            _current.Add((p.X, p.Y));
        }

        public void LineTo(Vector2 p)
        {
            _current ??= new List<(double X, double Y)>(8);
            _current.Add((p.X, p.Y));
        }

        public void QuadraticBezierTo(Vector2 c, Vector2 p)
        {
            _current ??= new List<(double X, double Y)>(8);
            if (_current.Count == 0) _current.Add((0, 0));  // defensive
            var prev = _current[^1];
            SvgPathParser.FlattenQuad(_current, prev.X, prev.Y, c.X, c.Y, p.X, p.Y);
        }

        public void CubicBezierTo(Vector2 c1, Vector2 c2, Vector2 p)
        {
            _current ??= new List<(double X, double Y)>(8);
            if (_current.Count == 0) _current.Add((0, 0));
            var prev = _current[^1];
            SvgPathParser.FlattenCubic(_current, prev.X, prev.Y, c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y);
        }
    }

    private static void RenderPath(XElement el, RgbaBuffer canvas, SvgStyle style, Matrix2D ctm)
    {
        var d = el.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(d)) return;

        var subpaths = SvgPathParser.Parse(d);
        if (subpaths.Count == 0) return;

        // Each path point goes through the CTM. Subpaths get transformed
        // independently — even-odd fill across all of them.
        var mapped = new List<List<(double X, double Y)>>(subpaths.Count);
        foreach (var sp in subpaths)
        {
            if (sp.Count < 2) continue;
            var s = new List<(double X, double Y)>(sp.Count);
            foreach (var p in sp) s.Add(ctm.Apply(p));
            mapped.Add(s);
        }
        if (mapped.Count == 0) return;

        if (style.FillColor is SolidPaint solidFill)
            canvas.FillSubpaths(mapped, solidFill.Color);
        else if (style.FillColor is { } fp)
        {
            // bbox from user-space subpaths (pre-CTM).
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var sp in subpaths)
                foreach (var pt in sp)
                {
                    if (pt.X < minX) minX = pt.X; if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y < minY) minY = pt.Y; if (pt.Y > maxY) maxY = pt.Y;
                }
            var bbox = new ShapeBBox(minX, minY, maxX, maxY);
            canvas.FillSubpathsSampler(mapped, fp.PrepareSampler(ctm, bbox));
        }

        if (style.StrokeColor is { } strokePaint && style.StrokeWidth > 0)
        {
            double sw = style.StrokeWidth * ctm.ScaleMagnitude;
            if (strokePaint is SolidPaint solidStroke)
            {
                foreach (var sp in mapped)
                    for (int i = 0; i + 1 < sp.Count; i++)
                        StrokeSegment(canvas, sp[i].X, sp[i].Y, sp[i + 1].X, sp[i + 1].Y, solidStroke.Color, sw);
            }
            else
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var sp in subpaths)
                    foreach (var pt in sp)
                    {
                        if (pt.X < minX) minX = pt.X; if (pt.X > maxX) maxX = pt.X;
                        if (pt.Y < minY) minY = pt.Y; if (pt.Y > maxY) maxY = pt.Y;
                    }
                var bbox = new ShapeBBox(minX, minY, maxX, maxY);
                var sampler = strokePaint.PrepareSampler(ctm, bbox);
                foreach (var sp in mapped)
                    for (int i = 0; i + 1 < sp.Count; i++)
                        StrokeSegmentSampler(canvas, sp[i].X, sp[i].Y, sp[i + 1].X, sp[i + 1].Y, sampler, sw);
            }
        }
    }

    private static void StrokePolygon(RgbaBuffer canvas, (double X, double Y)[] pts,
                                      Rgba color, double width, bool closed)
    {
        for (int i = 0; i + 1 < pts.Length; i++)
            StrokeSegment(canvas, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, color, width);
        if (closed && pts.Length > 2)
            StrokeSegment(canvas, pts[^1].X, pts[^1].Y, pts[0].X, pts[0].Y, color, width);
    }

    /// <summary>
    /// Same per-segment quad envelope as <see cref="StrokePolygon"/>, but
    /// filled with a sampler function (gradient or other paint server)
    /// instead of a solid colour.
    /// </summary>
    private static void StrokePolygonSampler(RgbaBuffer canvas, (double X, double Y)[] pts,
                                             Func<double, double, Rgba> sample, double width, bool closed)
    {
        for (int i = 0; i + 1 < pts.Length; i++)
            StrokeSegmentSampler(canvas, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, sample, width);
        if (closed && pts.Length > 2)
            StrokeSegmentSampler(canvas, pts[^1].X, pts[^1].Y, pts[0].X, pts[0].Y, sample, width);
    }

    /// <summary>Stroke a line segment as a thin filled polygon (a quad along the line normal).</summary>
    private static void StrokeSegment(RgbaBuffer canvas, double x1, double y1, double x2, double y2,
                                      Rgba color, double width)
    {
        if (!TryStrokeQuad(x1, y1, x2, y2, width, out var quad)) return;
        canvas.FillPolygon(quad, color);
    }

    /// <summary>Sampler variant of <see cref="StrokeSegment"/>.</summary>
    private static void StrokeSegmentSampler(RgbaBuffer canvas, double x1, double y1, double x2, double y2,
                                             Func<double, double, Rgba> sample, double width)
    {
        if (!TryStrokeQuad(x1, y1, x2, y2, width, out var quad)) return;
        canvas.FillPolygonSampler(quad, sample);
    }

    private static bool TryStrokeQuad(double x1, double y1, double x2, double y2, double width,
                                      out (double X, double Y)[] quad)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) { quad = Array.Empty<(double, double)>(); return false; }
        double nx = -dy / len, ny = dx / len;
        double hw = width * 0.5;
        quad = new (double X, double Y)[]
        {
            (x1 + nx * hw, y1 + ny * hw),
            (x2 + nx * hw, y2 + ny * hw),
            (x2 - nx * hw, y2 - ny * hw),
            (x1 - nx * hw, y1 - ny * hw),
        };
        return true;
    }

    // ---------- style + color ----------

    /// <summary>Resolved presentation attributes propagating down the SVG tree.</summary>
    private readonly struct SvgStyle
    {
        public readonly SvgPaint? FillColor;
        public readonly SvgPaint? StrokeColor;
        public readonly double StrokeWidth;

        public SvgStyle(SvgPaint? fill, SvgPaint? stroke, double strokeWidth)
        {
            FillColor = fill; StrokeColor = stroke; StrokeWidth = strokeWidth;
        }

        /// <summary>SVG default: fill = black, stroke = none, stroke-width = 1.</summary>
        public static readonly SvgStyle Default = new(new SolidPaint(new Rgba(0, 0, 0, 255)), null, 1.0);

        public SvgStyle InheritFrom(XElement el, IReadOnlyDictionary<string, GradientPaintBase> gradients)
        {
            var fill = ParseColor(GetStyleValue(el, "fill"), FillColor, gradients);
            var stroke = ParseColor(GetStyleValue(el, "stroke"), StrokeColor, gradients);
            double sw = StrokeWidth;
            if (GetStyleValue(el, "stroke-width") is { } swStr &&
                double.TryParse(swStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                sw = p;

            // fill-opacity / stroke-opacity / opacity modulate the alpha — works
            // uniformly across solid and gradient paints via SvgPaint.WithOpacity.
            double fillOpacity = ParseOpacity(GetStyleValue(el, "fill-opacity"), 1.0);
            double strokeOpacity = ParseOpacity(GetStyleValue(el, "stroke-opacity"), 1.0);
            double opacity = ParseOpacity(GetStyleValue(el, "opacity"), 1.0);
            if (fill is not null) fill = fill.WithOpacity(fillOpacity * opacity);
            if (stroke is not null) stroke = stroke.WithOpacity(strokeOpacity * opacity);

            return new SvgStyle(fill, stroke, sw);
        }
    }

    private static string? GetStyleValue(XElement el, string name)
    {
        // First check direct attribute.
        var attr = el.Attribute(name)?.Value;
        if (attr != null) return attr;

        // Then check style="key:value;..." inline.
        var styleAttr = el.Attribute("style")?.Value;
        if (styleAttr == null) return null;
        foreach (var decl in styleAttr.Split(';'))
        {
            int colon = decl.IndexOf(':');
            if (colon < 0) continue;
            if (decl.AsSpan(0, colon).Trim().SequenceEqual(name.AsSpan()))
                return decl[(colon + 1)..].Trim();
        }
        return null;
    }

    private static double ParseOpacity(string? s, double fallback)
        => s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : fallback;

    private static SvgPaint? ParseColor(string? raw, SvgPaint? inherited,
        IReadOnlyDictionary<string, GradientPaintBase> gradients)
    {
        if (raw == null) return inherited;
        raw = raw.Trim();
        if (raw.Length == 0 || raw.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
        if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (raw.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return new SolidPaint(new Rgba(0, 0, 0, 0));
        if (raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return inherited;

        // url(#id) — reference to a <linearGradient> / <radialGradient> def.
        if (raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int hash = raw.IndexOf('#'); int close = raw.IndexOf(')');
            if (hash > 0 && close > hash)
            {
                string id = raw[(hash + 1)..close].Trim();
                if (gradients.TryGetValue(id, out var grad)) return grad;
            }
            return inherited;  // unresolved url() falls back to inherited (SVG spec is "transparent black" but inherited is friendlier)
        }

        var solid = ParseSolidColor(raw);
        if (solid is { } s) return new SolidPaint(s);
        return inherited;
    }

    /// <summary>
    /// Parse a CSS color literal — hex, rgb()/rgba(), or a named-color subset —
    /// to a flat <see cref="Rgba"/>. Returns <c>null</c> when the input
    /// isn't a recognised color form.
    /// </summary>
    private static Rgba? ParseSolidColor(string raw)
    {
        // #RGB or #RRGGBB
        if (raw.StartsWith("#"))
        {
            string h = raw[1..];
            if (h.Length == 3 &&
                TryParseHexByte(h[0], out var rN) && TryParseHexByte(h[1], out var gN) && TryParseHexByte(h[2], out var bN))
                return new Rgba((byte)(rN * 17), (byte)(gN * 17), (byte)(bN * 17), 255);
            if (h.Length == 6 &&
                byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r6) &&
                byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g6) &&
                byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b6))
                return new Rgba(r6, g6, b6, 255);
        }

        // rgb(r, g, b) and rgba(r, g, b, a)
        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = raw.IndexOf('('); int close = raw.IndexOf(')');
            if (open > 0 && close > open)
            {
                var parts = raw[(open + 1)..close].Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is 3 or 4 &&
                    TryParseColorComponent(parts[0], out byte r) &&
                    TryParseColorComponent(parts[1], out byte g) &&
                    TryParseColorComponent(parts[2], out byte b))
                {
                    byte a = 255;
                    if (parts.Length == 4 &&
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var aF))
                        a = (byte)Math.Round(Math.Clamp(aF, 0, 1) * 255);
                    return new Rgba(r, g, b, a);
                }
            }
        }

        // Named colors (small but practical subset).
        return raw.ToLowerInvariant() switch
        {
            "black"   => new Rgba(0, 0, 0, 255),
            "white"   => new Rgba(255, 255, 255, 255),
            "red"     => new Rgba(255, 0, 0, 255),
            "green"   => new Rgba(0, 128, 0, 255),
            "lime"    => new Rgba(0, 255, 0, 255),
            "blue"    => new Rgba(0, 0, 255, 255),
            "yellow"  => new Rgba(255, 255, 0, 255),
            "cyan"    => new Rgba(0, 255, 255, 255),
            "magenta" => new Rgba(255, 0, 255, 255),
            "gray"    => new Rgba(128, 128, 128, 255),
            "silver"  => new Rgba(192, 192, 192, 255),
            "orange"  => new Rgba(255, 165, 0, 255),
            "purple"  => new Rgba(128, 0, 128, 255),
            "brown"   => new Rgba(165, 42, 42, 255),
            "pink"    => new Rgba(255, 192, 203, 255),
            _ => null,
        };
    }

    private static bool TryParseHexByte(char c, out int v)
    {
        v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return v >= 0;
    }

    private static bool TryParseColorComponent(string s, out byte v)
    {
        s = s.Trim();
        if (s.EndsWith("%"))
        {
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                v = (byte)Math.Round(Math.Clamp(pct, 0, 100) * 2.55);
                return true;
            }
            v = 0; return false;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            v = (byte)Math.Round(Math.Clamp(n, 0, 255));
            return true;
        }
        v = 0; return false;
    }

    // ---------- dimension/number parsing ----------

    private static int ParsePxDim(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        // Strip a unit suffix if present (px, pt, em, %, ...). Treat all as px.
        int i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.' || raw[i] == '-' || raw[i] == '+')) i++;
        if (i == 0) return 0;
        return double.TryParse(raw.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v) : 0;
    }

    private static double ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        int i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.' || raw[i] == '-' || raw[i] == '+' || raw[i] == 'e' || raw[i] == 'E')) i++;
        if (i == 0) return 0;
        return double.TryParse(raw.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---------- 2D affine matrix + transform="" parser ----------

    /// <summary>
    /// 2D affine matrix stored as the six free entries of a 3×3 matrix:
    /// <code>
    ///   | A  C  Tx |
    ///   | B  D  Ty |
    ///   | 0  0  1  |
    /// </code>
    /// Names match the SVG matrix() function's argument order (a, b, c, d, e, f).
    /// Composing parent · child gives a CTM where the child's transform applies
    /// to a point first, then the parent's.
    /// </summary>
    private readonly record struct Matrix2D(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static readonly Matrix2D Identity = new(1, 0, 0, 1, 0, 0);

        public static Matrix2D Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
        public static Matrix2D Scale(double sx, double sy)     => new(sx, 0, 0, sy, 0, 0);
        public static Matrix2D Rotate(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new(c, s, -s, c, 0, 0);
        }
        public static Matrix2D SkewX(double radians) => new(1, 0, Math.Tan(radians), 1, 0, 0);
        public static Matrix2D SkewY(double radians) => new(1, Math.Tan(radians), 0, 1, 0, 0);

        /// <summary>Returns <c>this · other</c>. Applying the result to a point
        /// first transforms by <paramref name="other"/>, then by <c>this</c>.</summary>
        public Matrix2D Compose(Matrix2D other) => new(
            A: A * other.A + C * other.B,
            B: B * other.A + D * other.B,
            C: A * other.C + C * other.D,
            D: B * other.C + D * other.D,
            Tx: A * other.Tx + C * other.Ty + Tx,
            Ty: B * other.Tx + D * other.Ty + Ty);

        public (double X, double Y) Apply((double X, double Y) p)
            => (A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);

        /// <summary>True iff the matrix has no rotation/skew (B=C=0); rect
        /// fill can take a fast axis-aligned path.</summary>
        public bool IsAxisAligned => Math.Abs(B) < 1e-12 && Math.Abs(C) < 1e-12;

        /// <summary>Geometric-mean scale magnitude (sqrt of |determinant|).
        /// Used to map user-space stroke widths to pixel-space widths under
        /// arbitrary affine transforms — approximate for non-uniform scales,
        /// exact for rotations + uniform scales.</summary>
        public double ScaleMagnitude => Math.Sqrt(Math.Abs(A * D - B * C));

        /// <summary>Inverse matrix, or null when this is singular (determinant ≈ 0).</summary>
        public Matrix2D? Invert()
        {
            double det = A * D - B * C;
            if (Math.Abs(det) < 1e-12) return null;
            double inv = 1.0 / det;
            return new Matrix2D(
                A:  D * inv,
                B: -B * inv,
                C: -C * inv,
                D:  A * inv,
                Tx: (C * Ty - D * Tx) * inv,
                Ty: (B * Tx - A * Ty) * inv);
        }
    }

    // ---------- paint hierarchy (solid + linear gradient) ----------

    /// <summary>
    /// Paint source for fill/stroke. <see cref="SolidPaint"/> wraps a single
    /// color; <see cref="LinearGradientPaint"/> holds the un-bound gradient
    /// definition (endpoints in SVG user space + stops + opacity). The
    /// shape renderer calls <see cref="PrepareSampler"/> with its CTM to
    /// produce a per-pixel sampler delegate.
    /// </summary>
    /// <summary>SVG gradient spread method per spec.</summary>
    private enum SpreadMethod { Pad, Reflect, Repeat }

    /// <summary>Shape bounding box in user space (pre-shape-CTM). Used by
    /// gradients with <c>gradientUnits="objectBoundingBox"</c> to map their
    /// 0..1 coords onto the shape. Identity (0,0,1,1) when not applicable.</summary>
    private readonly record struct ShapeBBox(double MinX, double MinY, double MaxX, double MaxY)
    {
        public static readonly ShapeBBox Unit = new(0, 0, 1, 1);
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    private abstract class SvgPaint
    {
        /// <summary>Return a new paint with alpha pre-multiplied by <paramref name="factor"/>.</summary>
        public abstract SvgPaint WithOpacity(double factor);

        /// <summary>Bind to a shape's CTM and bbox, produce a per-pixel sampler.
        /// SolidPaint ignores both; gradients use them to compose the
        /// canvas → gradient-frame matrix. <paramref name="bbox"/> is only
        /// consulted for gradients with <c>gradientUnits="objectBoundingBox"</c>.</summary>
        public abstract Func<double, double, Rgba> PrepareSampler(Matrix2D shapeCtm, ShapeBBox bbox);
    }

    private sealed class SolidPaint : SvgPaint
    {
        public readonly Rgba Color;
        public SolidPaint(Rgba c) { Color = c; }

        public override SvgPaint WithOpacity(double factor)
        {
            if (factor >= 1) return this;
            return new SolidPaint(new Rgba(Color.R, Color.G, Color.B,
                (byte)Math.Round(Math.Clamp(Color.A * factor, 0, 255))));
        }

        public override Func<double, double, Rgba> PrepareSampler(Matrix2D _, ShapeBBox __)
        {
            var c = Color;
            return (_, _) => c;
        }
    }

    private readonly record struct GradientStop(double Offset, Rgba Color);

    /// <summary>Shared state between linear and radial paints — stops,
    /// spread method, gradientTransform, objectBoundingBox flag, opacity.</summary>
    private abstract class GradientPaintBase : SvgPaint
    {
        public readonly GradientStop[] Stops;
        public readonly double Opacity;
        public readonly bool ObjectBoundingBox;
        public readonly Matrix2D GradientTransform;
        public readonly SpreadMethod Spread;

        protected GradientPaintBase(GradientStop[] stops, double opacity,
            bool objectBoundingBox, Matrix2D gradientTransform, SpreadMethod spread)
        {
            Stops = stops;
            Opacity = Math.Clamp(opacity, 0, 1);
            ObjectBoundingBox = objectBoundingBox;
            GradientTransform = gradientTransform;
            Spread = spread;
        }

        /// <summary>
        /// Build the canvas → gradient-space matrix that captures every
        /// transform between a canvas pixel and the gradient's authored
        /// coordinate system: inverse shape CTM, inverse bbox-to-user
        /// (when objectBoundingBox), and inverse gradientTransform.
        /// </summary>
        protected Matrix2D BuildCanvasToGradient(Matrix2D shapeCtm, ShapeBBox bbox)
        {
            var canvasToUser = shapeCtm.Invert() ?? Matrix2D.Identity;
            Matrix2D userToTarget = Matrix2D.Identity;
            if (ObjectBoundingBox)
            {
                // bbox-to-user = matrix(w, 0, 0, h, minX, minY).
                // Inverse: user-to-bbox.
                double w = bbox.Width, h = bbox.Height;
                if (Math.Abs(w) < 1e-12 || Math.Abs(h) < 1e-12)
                {
                    // Degenerate bbox; fall back to identity.
                }
                else
                {
                    userToTarget = new Matrix2D(
                        A: 1 / w, B: 0,
                        C: 0,     D: 1 / h,
                        Tx: -bbox.MinX / w, Ty: -bbox.MinY / h);
                }
            }
            var targetToGradient = GradientTransform.Invert() ?? Matrix2D.Identity;
            return targetToGradient.Compose(userToTarget.Compose(canvasToUser));
        }

        /// <summary>Apply spreadMethod to a raw gradient parameter t, returning
        /// a value in [0, 1] suitable for stop lookup.</summary>
        protected static double ApplySpread(double t, SpreadMethod spread) => spread switch
        {
            SpreadMethod.Reflect => ReflectT(t),
            SpreadMethod.Repeat  => RepeatT(t),
            _                    => t,  // pad — caller clamps via stop lookup
        };

        private static double RepeatT(double t)
        {
            double f = t - Math.Floor(t);
            return f < 0 ? f + 1 : f;
        }

        private static double ReflectT(double t)
        {
            double f = t - 2 * Math.Floor(t * 0.5);  // wrap to [0, 2)
            return f > 1 ? 2 - f : f;
        }

        protected Rgba SampleStops(double t)
        {
            Rgba c;
            if (t <= Stops[0].Offset) c = Stops[0].Color;
            else if (t >= Stops[^1].Offset) c = Stops[^1].Color;
            else
            {
                c = Stops[^1].Color;
                for (int i = 1; i < Stops.Length; i++)
                {
                    if (Stops[i].Offset >= t)
                    {
                        var s0 = Stops[i - 1];
                        var s1 = Stops[i];
                        double span = s1.Offset - s0.Offset;
                        double f = span > 1e-9 ? (t - s0.Offset) / span : 0;
                        c = new Rgba(
                            (byte)Math.Round(s0.Color.R + (s1.Color.R - s0.Color.R) * f),
                            (byte)Math.Round(s0.Color.G + (s1.Color.G - s0.Color.G) * f),
                            (byte)Math.Round(s0.Color.B + (s1.Color.B - s0.Color.B) * f),
                            (byte)Math.Round(s0.Color.A + (s1.Color.A - s0.Color.A) * f));
                        break;
                    }
                }
            }
            if (Opacity < 1) c = new Rgba(c.R, c.G, c.B, (byte)Math.Round(c.A * Opacity));
            return c;
        }
    }

    private sealed class LinearGradientPaint : GradientPaintBase
    {
        public readonly double X1, Y1, X2, Y2;

        public LinearGradientPaint(double x1, double y1, double x2, double y2,
            GradientStop[] stops, double opacity,
            bool objectBoundingBox = false,
            Matrix2D? gradientTransform = null,
            SpreadMethod spread = SpreadMethod.Pad)
            : base(stops, opacity, objectBoundingBox, gradientTransform ?? Matrix2D.Identity, spread)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        public override SvgPaint WithOpacity(double factor)
            => new LinearGradientPaint(X1, Y1, X2, Y2, Stops, Opacity * factor,
                ObjectBoundingBox, GradientTransform, Spread);

        public override Func<double, double, Rgba> PrepareSampler(Matrix2D shapeCtm, ShapeBBox bbox)
        {
            // canvas → gradient-authored space, then project onto the line
            // through (x1, y1) → (x2, y2) to get t.
            var canvasToGradient = BuildCanvasToGradient(shapeCtm, bbox);
            double dx = X2 - X1, dy = Y2 - Y1;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12)
            {
                var first = Stops[0].Color;
                return (_, _) => Opacity < 1
                    ? new Rgba(first.R, first.G, first.B, (byte)Math.Round(first.A * Opacity))
                    : first;
            }
            double invLen2 = 1.0 / len2;
            double x1 = X1, y1 = Y1;
            var spread = Spread;
            return (cx, cy) =>
            {
                var p = canvasToGradient.Apply((cx, cy));
                double t = ((p.X - x1) * dx + (p.Y - y1) * dy) * invLen2;
                if (spread != SpreadMethod.Pad) t = ApplySpread(t, spread);
                return SampleStops(t);
            };
        }
    }

    private sealed class RadialGradientPaint : GradientPaintBase
    {
        public readonly double CX, CY, R, FX, FY;

        public RadialGradientPaint(double cx, double cy, double r, double fx, double fy,
            GradientStop[] stops, double opacity,
            bool objectBoundingBox = false,
            Matrix2D? gradientTransform = null,
            SpreadMethod spread = SpreadMethod.Pad)
            : base(stops, opacity, objectBoundingBox, gradientTransform ?? Matrix2D.Identity, spread)
        {
            CX = cx; CY = cy; R = r; FX = fx; FY = fy;
        }

        public override SvgPaint WithOpacity(double factor)
            => new RadialGradientPaint(CX, CY, R, FX, FY, Stops, Opacity * factor,
                ObjectBoundingBox, GradientTransform, Spread);

        public override Func<double, double, Rgba> PrepareSampler(Matrix2D shapeCtm, ShapeBBox bbox)
        {
            var canvasToGradient = BuildCanvasToGradient(shapeCtm, bbox);
            double cx = CX, cy = CY, r = R, fx = FX, fy = FY;
            // Pre-compute focal-vs-center offset.
            double ex = fx - cx, ey = fy - cy;
            double ee = ex * ex + ey * ey;
            double r2 = r * r;
            var spread = Spread;
            return (px, py) =>
            {
                var p = canvasToGradient.Apply((px, py));
                // Direction vector from focal to the sample point.
                double dx = p.X - fx, dy = p.Y - fy;
                double dd = dx * dx + dy * dy;
                double t;
                if (dd < 1e-20)
                {
                    t = 0;
                }
                else if (ee < 1e-20)
                {
                    // Focal coincides with center — fast path: t = |d| / r.
                    t = Math.Sqrt(dd) / r;
                }
                else
                {
                    // Solve |alpha * d + e|² = r² for alpha; t = 1 / alpha.
                    double de = dx * ex + dy * ey;
                    double disc = de * de - dd * (ee - r2);
                    if (disc < 0) { t = double.PositiveInfinity; }
                    else
                    {
                        double alpha = (-de + Math.Sqrt(disc)) / dd;
                        t = alpha > 1e-12 ? 1.0 / alpha : double.PositiveInfinity;
                    }
                }
                if (spread != SpreadMethod.Pad && !double.IsInfinity(t)) t = ApplySpread(t, spread);
                return SampleStops(t);
            };
        }
    }

    /// <summary>
    /// Parser for the SVG <c>transform=""</c> attribute value. Recognises
    /// matrix / translate / scale / rotate / skewX / skewY, chained
    /// left-to-right. Functions can be separated by whitespace or commas;
    /// arguments by whitespace or commas with optional surrounding space.
    /// </summary>
    private static class SvgTransformParser
    {
        public static Matrix2D Parse(string transform)
        {
            var result = Matrix2D.Identity;
            int i = 0;
            while (i < transform.Length)
            {
                while (i < transform.Length && (char.IsWhiteSpace(transform[i]) || transform[i] == ',')) i++;
                if (i >= transform.Length) break;

                int nameStart = i;
                while (i < transform.Length && char.IsLetter(transform[i])) i++;
                if (nameStart == i) { i++; continue; }
                string name = transform.AsSpan(nameStart, i - nameStart).ToString().ToLowerInvariant();

                while (i < transform.Length && char.IsWhiteSpace(transform[i])) i++;
                if (i >= transform.Length || transform[i] != '(') continue;
                i++; // skip '('

                var args = new List<double>(6);
                while (i < transform.Length && transform[i] != ')')
                {
                    while (i < transform.Length && (char.IsWhiteSpace(transform[i]) || transform[i] == ',')) i++;
                    if (i >= transform.Length || transform[i] == ')') break;
                    int numStart = i;
                    if (transform[i] == '+' || transform[i] == '-') i++;
                    bool sawDigit = false, sawDot = false;
                    while (i < transform.Length)
                    {
                        char ch = transform[i];
                        if (char.IsDigit(ch)) { sawDigit = true; i++; }
                        else if (ch == '.' && !sawDot) { sawDot = true; i++; }
                        else break;
                    }
                    if (i < transform.Length && (transform[i] == 'e' || transform[i] == 'E'))
                    {
                        i++;
                        if (i < transform.Length && (transform[i] == '+' || transform[i] == '-')) i++;
                        while (i < transform.Length && char.IsDigit(transform[i])) i++;
                    }
                    if (!sawDigit) { i++; continue; }
                    if (double.TryParse(transform.AsSpan(numStart, i - numStart),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        args.Add(v);
                }
                if (i < transform.Length && transform[i] == ')') i++; // skip ')'

                Matrix2D fn = name switch
                {
                    "matrix" when args.Count == 6 =>
                        new Matrix2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                    "translate" when args.Count >= 1 =>
                        Matrix2D.Translate(args[0], args.Count > 1 ? args[1] : 0),
                    "scale" when args.Count >= 1 =>
                        Matrix2D.Scale(args[0], args.Count > 1 ? args[1] : args[0]),
                    "rotate" when args.Count == 1 =>
                        Matrix2D.Rotate(args[0] * Math.PI / 180.0),
                    "rotate" when args.Count == 3 =>
                        // rotate(angle, cx, cy) = T(cx,cy) · R · T(-cx,-cy)
                        Matrix2D.Translate(args[1], args[2])
                            .Compose(Matrix2D.Rotate(args[0] * Math.PI / 180.0))
                            .Compose(Matrix2D.Translate(-args[1], -args[2])),
                    "skewx" when args.Count == 1 =>
                        Matrix2D.SkewX(args[0] * Math.PI / 180.0),
                    "skewy" when args.Count == 1 =>
                        Matrix2D.SkewY(args[0] * Math.PI / 180.0),
                    _ => Matrix2D.Identity,  // unknown / malformed → no-op
                };
                result = result.Compose(fn);
            }
            return result;
        }
    }

    // ---------- SVG path-data parser + flattener ----------

    /// <summary>
    /// Parses an SVG <c>d</c> attribute into a list of subpaths, each a
    /// polyline approximation of the original (Béziers flattened via De
    /// Casteljau; arcs converted to cubic Béziers and then flattened).
    /// Coordinates are in SVG user space — caller maps to pixels.
    /// </summary>
    private static class SvgPathParser
    {
        private const double FlatnessTol = 0.25; // pixels — close enough for screen rasterization

        public static List<List<(double X, double Y)>> Parse(string d)
        {
            var subpaths = new List<List<(double X, double Y)>>();
            List<(double X, double Y)>? cur = null;
            double cx = 0, cy = 0;          // current point
            double startX = 0, startY = 0;  // subpath start
            char prevCmd = ' ';
            double prevCtrlX = 0, prevCtrlY = 0;
            bool hasPrevCubic = false, hasPrevQuad = false;

            void StartSubpath(double x, double y)
            {
                cur = new List<(double X, double Y)> { (x, y) };
                subpaths.Add(cur);
                startX = x; startY = y;
            }
            void Lineto(double x, double y)
            {
                cur ??= new List<(double X, double Y)>();
                if (cur.Count == 0) subpaths.Add(cur);
                if (cur.Count == 0 || cur[^1] != (x, y))
                    cur.Add((x, y));
            }

            var tokens = Tokenize(d);
            int ti = 0;
            while (ti < tokens.Count)
            {
                if (tokens[ti].Kind != TokenKind.Command) { ti++; continue; }
                char cmd = tokens[ti].Cmd;
                ti++;

                // Number of numeric args per command:
                int argc = cmd switch
                {
                    'M' or 'm' or 'L' or 'l' or 'T' or 't' => 2,
                    'H' or 'h' or 'V' or 'v' => 1,
                    'C' or 'c' => 6,
                    'S' or 's' or 'Q' or 'q' => 4,
                    'A' or 'a' => 7,
                    'Z' or 'z' => 0,
                    _ => -1,
                };
                if (argc < 0) continue;

                // Z has no args — close subpath and return to start.
                if (argc == 0)
                {
                    if (cur != null && cur.Count > 0)
                    {
                        if (cur[^1] != (startX, startY)) cur.Add((startX, startY));
                        cx = startX; cy = startY;
                    }
                    hasPrevCubic = hasPrevQuad = false;
                    prevCmd = cmd;
                    continue;
                }

                // Repeat command for as long as numbers keep coming.
                while (true)
                {
                    if (ti + argc > tokens.Count) break;
                    for (int k = 0; k < argc; k++)
                        if (tokens[ti + k].Kind != TokenKind.Number) { ti = tokens.Count; break; }
                    if (ti >= tokens.Count) break;

                    double[] a = new double[argc];
                    for (int k = 0; k < argc; k++) a[k] = tokens[ti + k].Num;
                    ti += argc;

                    bool rel = char.IsLower(cmd);
                    switch (cmd)
                    {
                        case 'M':
                        case 'm':
                        {
                            double x = a[0], y = a[1];
                            if (rel) { x += cx; y += cy; }
                            StartSubpath(x, y);
                            cx = x; cy = y;
                            // Subsequent implicit command is L/l.
                            cmd = rel ? 'l' : 'L';
                            argc = 2;
                            hasPrevCubic = hasPrevQuad = false;
                            break;
                        }
                        case 'L':
                        case 'l':
                        {
                            double x = a[0], y = a[1];
                            if (rel) { x += cx; y += cy; }
                            Lineto(x, y);
                            cx = x; cy = y;
                            hasPrevCubic = hasPrevQuad = false;
                            break;
                        }
                        case 'H':
                        case 'h':
                        {
                            double x = rel ? cx + a[0] : a[0];
                            Lineto(x, cy);
                            cx = x;
                            hasPrevCubic = hasPrevQuad = false;
                            break;
                        }
                        case 'V':
                        case 'v':
                        {
                            double y = rel ? cy + a[0] : a[0];
                            Lineto(cx, y);
                            cy = y;
                            hasPrevCubic = hasPrevQuad = false;
                            break;
                        }
                        case 'C':
                        case 'c':
                        {
                            double x1 = a[0], y1 = a[1], x2 = a[2], y2 = a[3], x = a[4], y = a[5];
                            if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                            FlattenCubic(cur ??= NewSubpath(subpaths, cx, cy), cx, cy, x1, y1, x2, y2, x, y);
                            prevCtrlX = x2; prevCtrlY = y2;
                            cx = x; cy = y;
                            hasPrevCubic = true; hasPrevQuad = false;
                            break;
                        }
                        case 'S':
                        case 's':
                        {
                            double x2 = a[0], y2 = a[1], x = a[2], y = a[3];
                            if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                            double x1 = hasPrevCubic ? 2 * cx - prevCtrlX : cx;
                            double y1 = hasPrevCubic ? 2 * cy - prevCtrlY : cy;
                            FlattenCubic(cur ??= NewSubpath(subpaths, cx, cy), cx, cy, x1, y1, x2, y2, x, y);
                            prevCtrlX = x2; prevCtrlY = y2;
                            cx = x; cy = y;
                            hasPrevCubic = true; hasPrevQuad = false;
                            break;
                        }
                        case 'Q':
                        case 'q':
                        {
                            double x1 = a[0], y1 = a[1], x = a[2], y = a[3];
                            if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                            FlattenQuad(cur ??= NewSubpath(subpaths, cx, cy), cx, cy, x1, y1, x, y);
                            prevCtrlX = x1; prevCtrlY = y1;
                            cx = x; cy = y;
                            hasPrevQuad = true; hasPrevCubic = false;
                            break;
                        }
                        case 'T':
                        case 't':
                        {
                            double x = a[0], y = a[1];
                            if (rel) { x += cx; y += cy; }
                            double x1 = hasPrevQuad ? 2 * cx - prevCtrlX : cx;
                            double y1 = hasPrevQuad ? 2 * cy - prevCtrlY : cy;
                            FlattenQuad(cur ??= NewSubpath(subpaths, cx, cy), cx, cy, x1, y1, x, y);
                            prevCtrlX = x1; prevCtrlY = y1;
                            cx = x; cy = y;
                            hasPrevQuad = true; hasPrevCubic = false;
                            break;
                        }
                        case 'A':
                        case 'a':
                        {
                            double rx = a[0], ry = a[1], xRot = a[2];
                            bool largeArc = a[3] != 0, sweep = a[4] != 0;
                            double x = a[5], y = a[6];
                            if (rel) { x += cx; y += cy; }
                            FlattenArc(cur ??= NewSubpath(subpaths, cx, cy), cx, cy, rx, ry, xRot, largeArc, sweep, x, y);
                            cx = x; cy = y;
                            hasPrevCubic = hasPrevQuad = false;
                            break;
                        }
                    }
                    prevCmd = cmd;

                    // Continue iterating if more numbers remain.
                    if (ti >= tokens.Count || tokens[ti].Kind == TokenKind.Command) break;
                }
            }

            return subpaths;
        }

        private static List<(double X, double Y)> NewSubpath(
            List<List<(double X, double Y)>> all, double x, double y)
        {
            var s = new List<(double X, double Y)> { (x, y) };
            all.Add(s);
            return s;
        }

        // De Casteljau cubic flattening: subdivide until chord-to-curve
        // distance is below tolerance.
        internal static void FlattenCubic(List<(double X, double Y)> dst,
            double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            const int MaxDepth = 18;
            void Recurse(double a0, double b0, double a1, double b1, double a2, double b2, double a3, double b3, int depth)
            {
                double dx = a3 - a0, dy = b3 - b0;
                double l2 = dx * dx + dy * dy;
                double d1 = l2 > 1e-12
                    ? Math.Abs((a1 - a0) * dy - (b1 - b0) * dx) / Math.Sqrt(l2)
                    : Math.Sqrt((a1 - a0) * (a1 - a0) + (b1 - b0) * (b1 - b0));
                double d2 = l2 > 1e-12
                    ? Math.Abs((a2 - a0) * dy - (b2 - b0) * dx) / Math.Sqrt(l2)
                    : Math.Sqrt((a2 - a0) * (a2 - a0) + (b2 - b0) * (b2 - b0));
                if (depth >= MaxDepth || Math.Max(d1, d2) <= FlatnessTol)
                {
                    if (dst.Count == 0 || dst[^1] != (a3, b3)) dst.Add((a3, b3));
                    return;
                }
                double m01x = (a0 + a1) / 2, m01y = (b0 + b1) / 2;
                double m12x = (a1 + a2) / 2, m12y = (b1 + b2) / 2;
                double m23x = (a2 + a3) / 2, m23y = (b2 + b3) / 2;
                double m012x = (m01x + m12x) / 2, m012y = (m01y + m12y) / 2;
                double m123x = (m12x + m23x) / 2, m123y = (m12y + m23y) / 2;
                double mx = (m012x + m123x) / 2, my = (m012y + m123y) / 2;
                Recurse(a0, b0, m01x, m01y, m012x, m012y, mx, my, depth + 1);
                Recurse(mx, my, m123x, m123y, m23x, m23y, a3, b3, depth + 1);
            }
            Recurse(x0, y0, x1, y1, x2, y2, x3, y3, 0);
        }

        internal static void FlattenQuad(List<(double X, double Y)> dst,
            double x0, double y0, double x1, double y1, double x2, double y2)
        {
            // Quadratic → cubic with control points 1/3 of the way.
            double cx1 = x0 + (2.0 / 3.0) * (x1 - x0);
            double cy1 = y0 + (2.0 / 3.0) * (y1 - y0);
            double cx2 = x2 + (2.0 / 3.0) * (x1 - x2);
            double cy2 = y2 + (2.0 / 3.0) * (y1 - y2);
            FlattenCubic(dst, x0, y0, cx1, cy1, cx2, cy2, x2, y2);
        }

        // SVG elliptical arc → center parameterization → cubic Béziers (one
        // per quarter sweep) → flatten. Per the SVG 1.1 implementation note
        // (Appendix F.6).
        private static void FlattenArc(List<(double X, double Y)> dst,
            double x1, double y1, double rx, double ry, double xRotDeg,
            bool largeArc, bool sweep, double x2, double y2)
        {
            if (rx == 0 || ry == 0)
            {
                if (dst.Count == 0 || dst[^1] != (x2, y2)) dst.Add((x2, y2));
                return;
            }
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double phi = xRotDeg * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            // Step 1: midpoint of (x1,y1)-(x2,y2) rotated to ellipse-aligned space.
            double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
            double x1p =  cosPhi * dx + sinPhi * dy;
            double y1p = -sinPhi * dx + cosPhi * dy;

            // Step 2: scale rx, ry up if they're too small to cover the chord.
            double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; }

            // Step 3: ellipse center in the rotated space.
            double sign = largeArc == sweep ? -1 : 1;
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double factor = sign * Math.Sqrt(Math.Max(0, num / den));
            double cxp = factor * (rx * y1p / ry);
            double cyp = factor * -(ry * x1p / rx);

            // Step 4: back to original space.
            double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2;
            double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2;

            // Step 5: start angle + sweep delta.
            double Angle(double ux, double uy, double vx, double vy)
            {
                double dot = ux * vx + uy * vy;
                double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
                double ang = Math.Acos(Math.Clamp(dot / len, -1.0, 1.0));
                return (ux * vy - uy * vx) < 0 ? -ang : ang;
            }
            double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double dTheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
            else if (sweep && dTheta < 0) dTheta += 2 * Math.PI;

            // Step 6: break sweep into ≤90° cubic Bézier segments.
            int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI / 2)));
            double dAng = dTheta / segs;
            double t = Math.Tan(dAng / 2);
            double alpha = Math.Sin(dAng) * (Math.Sqrt(4 + 3 * t * t) - 1) / 3;

            double prevX = x1, prevY = y1;
            double prevDX = -Math.Sin(theta1), prevDY = Math.Cos(theta1);
            for (int i = 1; i <= segs; i++)
            {
                double ang = theta1 + i * dAng;
                double px = cx + cosPhi * rx * Math.Cos(ang) - sinPhi * ry * Math.Sin(ang);
                double py = cy + sinPhi * rx * Math.Cos(ang) + cosPhi * ry * Math.Sin(ang);
                double dxr = -Math.Sin(ang), dyr = Math.Cos(ang);
                double c1x = prevX + alpha * (cosPhi * rx * prevDX - sinPhi * ry * prevDY);
                double c1y = prevY + alpha * (sinPhi * rx * prevDX + cosPhi * ry * prevDY);
                double c2x = px - alpha * (cosPhi * rx * dxr - sinPhi * ry * dyr);
                double c2y = py - alpha * (sinPhi * rx * dxr + cosPhi * ry * dyr);
                FlattenCubic(dst, prevX, prevY, c1x, c1y, c2x, c2y, px, py);
                prevX = px; prevY = py;
                prevDX = dxr; prevDY = dyr;
            }
        }

        // ---- tokenizer ----

        private enum TokenKind { Command, Number }
        private readonly record struct Token(TokenKind Kind, char Cmd, double Num);

        private static List<Token> Tokenize(string d)
        {
            var tokens = new List<Token>(d.Length / 2);
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if (char.IsLetter(c))
                {
                    tokens.Add(new Token(TokenKind.Command, c, 0));
                    i++;
                    continue;
                }
                // Try to read a number: optional sign, digits, optional '.',
                // more digits, optional exponent. SVG numbers can be tightly
                // packed (e.g. "0-5" = 0 then -5; ".5.5" = .5 then .5).
                int start = i;
                if (c == '+' || c == '-') i++;
                bool sawDigit = false, sawDot = false;
                while (i < d.Length)
                {
                    char ch = d[i];
                    if (char.IsDigit(ch)) { sawDigit = true; i++; }
                    else if (ch == '.' && !sawDot) { sawDot = true; i++; }
                    else break;
                }
                if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
                {
                    i++;
                    if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
                    while (i < d.Length && char.IsDigit(d[i])) i++;
                }
                if (!sawDigit) { i = start + 1; continue; } // garbage; skip 1 char
                var slice = d.AsSpan(start, i - start);
                if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    tokens.Add(new Token(TokenKind.Number, ' ', v));
            }
            return tokens;
        }
    }

    // ---------- RGBA buffer + rasterizer ----------

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private sealed class RgbaBuffer
    {
        public readonly byte[] Bytes;
        public readonly int Width;
        public readonly int Height;

        /// <summary>Per-pixel clip mask (0 = outside clip, non-zero = inside).
        /// Null means no clip is active. <see cref="BlendPixel"/> consults
        /// this; setting it short-circuits all the Fill* methods so they
        /// transparently respect the clip without needing per-method variants.</summary>
        public byte[]? ClipMask { get; set; }

        public RgbaBuffer(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Bytes = new byte[Width * Height * 4]; // zero-initialized = transparent
        }

        /// <summary>
        /// Composite <paramref name="src"/> onto this buffer using Porter-Duff
        /// "over". The destination's <see cref="ClipMask"/> is respected
        /// (parent-clip semantics) since composition goes through BlendPixel.
        /// </summary>
        public void CompositeOver(RgbaBuffer src)
        {
            if (src.Width != Width || src.Height != Height) return;  // shouldn't happen
            for (int y = 0; y < Height; y++)
            {
                int rowOff = y * Width * 4;
                for (int x = 0; x < Width; x++)
                {
                    int i = rowOff + x * 4;
                    int sA = src.Bytes[i + 3];
                    if (sA == 0) continue;
                    BlendPixel(x, y, new Rgba(src.Bytes[i + 0], src.Bytes[i + 1], src.Bytes[i + 2], (byte)sA));
                }
            }
        }

        public void FillRect(double x1, double y1, double x2, double y2, Rgba color)
        {
            int minX = (int)Math.Max(0, Math.Floor(Math.Min(x1, x2)));
            int maxX = (int)Math.Min(Width - 1, Math.Ceiling(Math.Max(x1, x2)));
            int minY = (int)Math.Max(0, Math.Floor(Math.Min(y1, y2)));
            int maxY = (int)Math.Min(Height - 1, Math.Ceiling(Math.Max(y1, y2)));
            if (minX > maxX || minY > maxY) return;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    BlendPixel(x, y, color);
        }

        public void FillPolygon(IReadOnlyList<(double X, double Y)> pts, Rgba color)
        {
            // Single-subpath convenience — defers to the multi-subpath path.
            FillSubpaths(new[] { pts }, color);
        }

        /// <summary>Paint-aware variant of <see cref="FillRect"/>.</summary>
        public void FillRectSampler(double x1, double y1, double x2, double y2, Func<double, double, Rgba> sample)
        {
            int minX = (int)Math.Max(0, Math.Floor(Math.Min(x1, x2)));
            int maxX = (int)Math.Min(Width - 1, Math.Ceiling(Math.Max(x1, x2)));
            int minY = (int)Math.Max(0, Math.Floor(Math.Min(y1, y2)));
            int maxY = (int)Math.Min(Height - 1, Math.Ceiling(Math.Max(y1, y2)));
            if (minX > maxX || minY > maxY) return;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    BlendPixel(x, y, sample(x + 0.5, y + 0.5));
        }

        /// <summary>Paint-aware variant of <see cref="FillPolygon"/>.</summary>
        public void FillPolygonSampler(IReadOnlyList<(double X, double Y)> pts, Func<double, double, Rgba> sample)
        {
            FillSubpathsSampler(new[] { pts }, sample);
        }

        /// <summary>Paint-aware variant of <see cref="FillSubpaths"/> (even-odd rule).</summary>
        public void FillSubpathsSampler(IReadOnlyList<IReadOnlyList<(double X, double Y)>> subpaths,
                                        Func<double, double, Rgba> sample)
        {
            if (subpaths.Count == 0) return;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var sp in subpaths)
            {
                if (sp.Count < 2) continue;
                for (int i = 0; i < sp.Count; i++)
                {
                    if (sp[i].Y < minY) minY = sp[i].Y;
                    if (sp[i].Y > maxY) maxY = sp[i].Y;
                }
            }
            if (minY > maxY) return;

            int yStart = Math.Max(0, (int)Math.Floor(minY));
            int yEnd = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
            var xs = new List<double>(16);

            for (int y = yStart; y <= yEnd; y++)
            {
                double sampleY = y + 0.5;
                xs.Clear();
                foreach (var sp in subpaths)
                {
                    int n = sp.Count;
                    if (n < 2) continue;
                    for (int i = 0; i < n; i++)
                    {
                        var a = sp[i];
                        var b = sp[(i + 1) % n];
                        if (Math.Abs(a.Y - b.Y) < 1e-9) continue;
                        double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                        if (sampleY < lo || sampleY >= hi) continue;
                        double t = (sampleY - a.Y) / (b.Y - a.Y);
                        xs.Add(a.X + (b.X - a.X) * t);
                    }
                }
                xs.Sort();
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    int x0 = Math.Max(0, (int)Math.Round(xs[i]));
                    int x1 = Math.Min(Width - 1, (int)Math.Round(xs[i + 1]));
                    for (int x = x0; x <= x1; x++) BlendPixel(x, y, sample(x + 0.5, y + 0.5));
                }
            }
        }

        /// <summary>
        /// Fill an arbitrary set of subpaths under the even-odd rule. Each
        /// subpath closes implicitly (last → first); inner contours act as
        /// holes. Suitable for SVG <c>&lt;path&gt;</c> output where multiple
        /// <c>M…Z</c> blocks describe a shape with cutouts.
        /// </summary>
        public void FillSubpaths(IReadOnlyList<IReadOnlyList<(double X, double Y)>> subpaths, Rgba color)
        {
            if (subpaths.Count == 0) return;

            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var sp in subpaths)
            {
                if (sp.Count < 2) continue;
                for (int i = 0; i < sp.Count; i++)
                {
                    if (sp[i].Y < minY) minY = sp[i].Y;
                    if (sp[i].Y > maxY) maxY = sp[i].Y;
                }
            }
            if (minY > maxY) return;

            int yStart = Math.Max(0, (int)Math.Floor(minY));
            int yEnd = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
            var xs = new List<double>(16);

            for (int y = yStart; y <= yEnd; y++)
            {
                double sample = y + 0.5;
                xs.Clear();
                foreach (var sp in subpaths)
                {
                    int n = sp.Count;
                    if (n < 2) continue;
                    for (int i = 0; i < n; i++)
                    {
                        var a = sp[i];
                        var b = sp[(i + 1) % n];
                        if (Math.Abs(a.Y - b.Y) < 1e-9) continue;
                        double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                        if (sample < lo || sample >= hi) continue;
                        double t = (sample - a.Y) / (b.Y - a.Y);
                        xs.Add(a.X + (b.X - a.X) * t);
                    }
                }
                xs.Sort();
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    int x0 = Math.Max(0, (int)Math.Round(xs[i]));
                    int x1 = Math.Min(Width - 1, (int)Math.Round(xs[i + 1]));
                    for (int x = x0; x <= x1; x++) BlendPixel(x, y, color);
                }
            }
        }

        private void BlendPixel(int x, int y, Rgba src)
        {
            // Soft clip/mask: multiply source alpha by the per-pixel mask
            // value (0..255 → 0 = fully blocked, 255 = fully passes). When
            // ClipMask is null the pixel writes through unmodulated.
            if (ClipMask != null)
            {
                int m = ClipMask[y * Width + x];
                if (m == 0) return;
                if (m < 255) src = new Rgba(src.R, src.G, src.B, (byte)((src.A * m + 127) / 255));
            }
            int idx = (y * Width + x) * 4;
            if (src.A == 255)
            {
                Bytes[idx + 0] = src.R;
                Bytes[idx + 1] = src.G;
                Bytes[idx + 2] = src.B;
                Bytes[idx + 3] = 255;
                return;
            }
            if (src.A == 0) return;

            // Straight-alpha "over" composite per Porter-Duff.
            double sA = src.A / 255.0;
            double dA = Bytes[idx + 3] / 255.0;
            double outA = sA + dA * (1 - sA);
            if (outA < 1e-6) return;
            double sCo = sA / outA;
            double dCo = dA * (1 - sA) / outA;
            Bytes[idx + 0] = (byte)Math.Round(src.R * sCo + Bytes[idx + 0] * dCo);
            Bytes[idx + 1] = (byte)Math.Round(src.G * sCo + Bytes[idx + 1] * dCo);
            Bytes[idx + 2] = (byte)Math.Round(src.B * sCo + Bytes[idx + 2] * dCo);
            Bytes[idx + 3] = (byte)Math.Round(outA * 255);
        }
    }
}
