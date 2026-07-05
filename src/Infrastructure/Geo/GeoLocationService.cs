using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using EventWOS.Application.Attendance.Geo;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Geo;

/// <summary>
/// In-process reverse geocoder backed by an embedded GeoNames
/// cities15000 dataset (~34k cities worldwide, ~716 KB gzipped, ~1.6 MB
/// in memory). Loads once at startup into a static 2-D KD-tree keyed
/// by lat/lng; per-request lookups are pure CPU (typically &lt; 50µs)
/// with no external network dependency.
///
/// Why an embedded dataset and not a third-party API:
///   • Deterministic latency — no 100-800 ms round-trip variability.
///   • No rate limits, no attribution requirement to render at request
///     time, no CORS concerns, no future-proofing risk if a vendor
///     changes their terms.
///   • Zero PII / coord leakage to third parties — user coordinates
///     never leave our process.
///   • Works offline (Railway deploys, on-prem, etc.).
///
/// Trade-off: only names the nearest populated place ≥ 15k population.
/// This is coarser than a street-level reverse geocoder (we won\'t say
/// "Prabhadevi" for a coord in that neighbourhood — we\'ll say
/// "Mumbai"). Acceptable for attendance auditing: the question is
/// "which venue / which city", not "which building".
///
/// The KD-tree uses squared-degree distance for comparison — no need
/// to convert to metres or run Haversine because nearest-neighbour in
/// degree space picks the same winner as nearest-neighbour in metres
/// for the small local search radii involved.
/// </summary>
public sealed class GeoLocationService : IGeoLocationService
{
    // Static state — dataset is immutable and shared across all instances.
    private static readonly Lazy<KdTree> _tree = new(LoadTree, isThreadSafe: true);

    private readonly ILogger<GeoLocationService> _log;
    public GeoLocationService(ILogger<GeoLocationService> log) => _log = log;

    public string Enrich(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        // Already enriched (idempotent — safe to double-call).
        if (raw.Contains('|')) return raw;
        // Client signalled "no fix" — pass through as-is; the frontend
        // renders these as em-dash.
        if (raw.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase))
            return raw;

        // Parse "lat,lng" — must be exactly two doubles separated by
        // a comma. Anything else we return verbatim to avoid corrupting
        // whatever the caller had (defensive: some legacy paths may
        // hand-write location strings we\'re not aware of).
        var parts = raw.Split(',', 2);
        if (parts.Length != 2) return raw;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var lat)) return raw;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var lng)) return raw;
        if (lat is < -90 or > 90 || lng is < -180 or > 180) return raw;

        try
        {
            var hit = _tree.Value.Nearest(lat, lng);
            if (hit is null) return raw;

            // Format: "City, State, Country" — filter out blanks so we
            // don\'t emit "Mumbai, , India" for records missing admin1.
            var label = string.Join(", ",
                new[] { hit.Name, hit.State, hit.Country }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

            return string.IsNullOrEmpty(label) ? raw : $"{raw}|{label}";
        }
        catch (Exception ex)
        {
            // Never let a geocode failure block a check-in. Log and
            // return the raw coords — the frontend will still render
            // a working map pin, just without a label.
            _log.LogWarning(ex, "GeoLocationService.Enrich failed for {Raw}", raw);
            return raw;
        }
    }

    // ─── Dataset loading ────────────────────────────────────────────
    private static KdTree LoadTree()
    {
        var asm = typeof(GeoLocationService).Assembly;

        // Resource name convention: <DefaultNamespace>.<PathWithDots>
        // Csproj embeds Infrastructure/Geo/geodata.tsv.gz →
        // "EventWOS.Infrastructure.Geo.geodata.tsv.gz"
        const string resourceName = "EventWOS.Infrastructure.Geo.geodata.tsv.gz";
        using var raw = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded geodata resource not found: {resourceName}. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var pts = new List<GeoPoint>(capacity: 40_000);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // lat<TAB>lng<TAB>Name<TAB>State<TAB>Country
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) continue;
            pts.Add(new GeoPoint(lat, lng, f[2], f[3], f[4]));
        }

        return new KdTree(pts);
    }

    internal sealed record GeoPoint(double Lat, double Lng, string Name, string State, string Country);

    // ─── KD-tree — 2-D, alternates split axis (lat/lng) by depth. ──
    private sealed class KdTree
    {
        private readonly Node? _root;

        public KdTree(List<GeoPoint> points)
            => _root = Build(points, 0, points.Count - 1, depth: 0);

        // Recursive median-split build. We sort a *view* of the list
        // in-place — O(n log² n) worst case but only runs once at boot.
        private static Node? Build(List<GeoPoint> pts, int lo, int hi, int depth)
        {
            if (lo > hi) return null;
            var axis = depth % 2;                       // 0 = lat, 1 = lng
            pts.Sort(lo, hi - lo + 1, Comparer<GeoPoint>.Create(
                (a, b) => (axis == 0 ? a.Lat : a.Lng)
                    .CompareTo(axis == 0 ? b.Lat : b.Lng)));
            var mid = (lo + hi) / 2;
            return new Node(pts[mid],
                Build(pts, lo, mid - 1, depth + 1),
                Build(pts, mid + 1, hi, depth + 1));
        }

        public GeoPoint? Nearest(double lat, double lng)
        {
            if (_root is null) return null;
            var best = new Best(null, double.MaxValue);
            NearestRec(_root, lat, lng, 0, best);
            return best.Point;
        }

        // Standard KD nearest-neighbour with axis-aligned bound-check pruning.
        private static void NearestRec(Node node, double lat, double lng, int depth, Best best)
        {
            var d = SqrDist(node.Point, lat, lng);
            if (d < best.Distance) { best.Point = node.Point; best.Distance = d; }

            var axis = depth % 2;
            var diff = axis == 0 ? lat - node.Point.Lat : lng - node.Point.Lng;
            var near = diff < 0 ? node.Left : node.Right;
            var far  = diff < 0 ? node.Right : node.Left;

            if (near is not null) NearestRec(near, lat, lng, depth + 1, best);
            // Only recurse into the far subtree if the splitting plane
            // could contain a closer point. diff² is the min distance
            // from the query point to that plane.
            if (far is not null && diff * diff < best.Distance)
                NearestRec(far, lat, lng, depth + 1, best);
        }

        private static double SqrDist(GeoPoint p, double lat, double lng)
        {
            var dLat = p.Lat - lat;
            var dLng = p.Lng - lng;
            return dLat * dLat + dLng * dLng;
        }

        private sealed record Node(GeoPoint Point, Node? Left, Node? Right);
        private sealed class Best { public GeoPoint? Point; public double Distance;
            public Best(GeoPoint? p, double d) { Point = p; Distance = d; } }
    }
}
