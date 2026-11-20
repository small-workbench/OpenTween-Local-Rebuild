// C# adaptation of iSarabjitDhiman/XClientTransaction.
// Copyright (c) 2025 Sarabjit Dhiman. MIT License.
// Source ref: f33670478bf799c2c10b33894c97347ccb9904a1
// Adapted for OpenTween Small Workbench Edition, 2026-11-21. See ThirdPartyNotices.md.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenTween.Connection
{
    internal sealed class XClientTransactionIdGenerator
    {
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
        private readonly byte[] verificationKey;
        private readonly string animation;

        private XClientTransactionIdGenerator(byte[] verificationKey, string animation)
        {
            this.verificationKey = verificationKey;
            this.animation = animation;
        }

        private static string? Attribute(string tag, string name)
        {
            var match = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
        }

        public static string? FindOnDemandFileUrl(string html)
        {
            var direct = Regex.Match(html, @"https://abs\.twimg\.com/responsive-web/client-web/ondemand\.s\.[A-Za-z0-9_-]+a\.js");
            if (direct.Success)
                return direct.Value;
            var index = Regex.Match(html, @"[,\{]\s*(\d+)\s*:\s*[""']ondemand\.s[""']");
            if (!index.Success)
                return null;
            var hash = Regex.Match(html, @"\b" + Regex.Escape(index.Groups[1].Value) + @"\s*:\s*[""']([A-Za-z0-9_-]+)[""']");
            return hash.Success ? "https://abs.twimg.com/responsive-web/client-web/ondemand.s." + hash.Groups[1].Value + "a.js" : null;
        }

        public static XClientTransactionIdGenerator Create(string html, string script)
        {
            var indices = Regex.Matches(script, @"\(\w\[(\d{1,2})\],\s*1\s*6\)")
                .Cast<Match>().Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            if (indices.Length < 2)
                throw new FormatException("Transaction key indices are missing.");
            var meta = Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase)
                .Cast<Match>().FirstOrDefault(m => Attribute(m.Value, "name") == "twitter-site-verification");
            var encoded = meta == null ? null : Attribute(meta.Value, "content");
            if (string.IsNullOrWhiteSpace(encoded))
                throw new FormatException("Transaction verification key is missing.");
            var key = Convert.FromBase64String(encoded);
            if (key.Length <= Math.Max(5, indices.Max()))
                throw new FormatException("Transaction verification key is too short.");
            var frames = Regex.Matches(html, @"<svg\b[^>]*>[\s\S]*?</svg>", RegexOptions.IgnoreCase)
                .Cast<Match>().Where(m => (Attribute(m.Value.Substring(0, m.Value.IndexOf('>') + 1), "id") ?? "").StartsWith("loading-x-anim", StringComparison.Ordinal)).ToArray();
            if (frames.Length < 4)
                throw new FormatException("Transaction animation frames are missing.");
            var paths = Regex.Matches(frames[key[5] % 4].Value, @"<path\b[^>]*>", RegexOptions.IgnoreCase);
            if (paths.Count < 2)
                throw new FormatException("Transaction animation path is missing.");
            var path = Attribute(paths[1].Value, "d");
            if (path == null || path.Length < 9)
                throw new FormatException("Transaction animation path is invalid.");
            var rows = path.Substring(9).Split('C').Select(row => Regex.Matches(row, @"\d+")
                .Cast<Match>().Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            var selected = key[indices[0]] % 16;
            if (selected >= rows.Length || rows[selected].Length != 11)
                throw new FormatException("Transaction animation row is invalid.");
            var frameTime = indices.Skip(1).Aggregate(1.0, (product, index) => product * (key[index] % 16));
            return new XClientTransactionIdGenerator(key, Animate(rows[selected], Math.Floor(frameTime / 10 + 0.5) * 10 / 4096));
        }

        public string Generate(string method, string path)
        {
            var mask = new byte[1];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(mask);
            return this.Generate(method, path, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1682924400, mask[0]);
        }

        // Deterministic overload allows comparison with the Python reference.
        internal string Generate(string method, string path, long seconds, byte mask)
        {
            var input = method + "!" + path + "!" + seconds.ToString(CultureInfo.InvariantCulture) + "obfiowerehiring" + this.animation;
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var payload = new List<byte>(this.verificationKey);
            for (var index = 0; index < 4; index++)
                payload.Add((byte)((seconds >> (index * 8)) & 255));
            payload.AddRange(digest.Take(16));
            payload.Add(3);
            return Convert.ToBase64String(new[] { mask }.Concat(payload.Select(value => (byte)(value ^ mask))).ToArray()).TrimEnd('=');
        }

        private static double Solve(int value, double minimum, bool floor)
        {
            var result = value * (1 - minimum) / 255 + minimum;
            return floor ? Math.Floor(value * 300.0 / 255 + 60) : Math.Round(result, 2, MidpointRounding.ToEven);
        }

        internal static string Animate(int[] row, double time)
        {
            var curve = row.Skip(7).Select((value, index) => Solve(value, index % 2 == 0 ? 0 : -1, false)).ToArray();
            var fraction = Ease(curve, time);
            var text = new StringBuilder();
            for (var index = 0; index < 3; index++)
            {
                var value = Math.Max(0, Math.Min(255, row[index] * (1 - fraction) + row[index + 3] * fraction));
                text.Append(((int)Math.Round(value, MidpointRounding.ToEven)).ToString("x", CultureInfo.InvariantCulture));
            }
            var radians = Solve(row[6], 60, true) * fraction * Math.PI / 180;
            foreach (var component in new[] { Math.Cos(radians), -Math.Sin(radians), Math.Sin(radians), Math.Cos(radians) })
            {
                var hex = HexFraction(Math.Abs(Math.Round(component, 2, MidpointRounding.ToEven)));
                text.Append(hex.Length == 0 ? "0" : hex.StartsWith(".", StringComparison.Ordinal) ? "0" + hex : hex);
            }
            return text.Append("00").ToString().Replace(".", "").Replace("-", "").ToLowerInvariant();
        }

        private static string HexFraction(double value)
        {
            var integer = (int)value;
            var fraction = value - integer;
            var result = new StringBuilder(integer == 0 ? "" : integer.ToString("x", CultureInfo.InvariantCulture));
            if (fraction > 0)
            {
                result.Append('.');
                while (fraction > 0)
                {
                    fraction *= 16;
                    var digit = (int)fraction;
                    result.Append(digit.ToString("x", CultureInfo.InvariantCulture));
                    fraction -= digit;
                }
            }
            return result.ToString();
        }

        private static double Ease(double[] curve, double time)
        {
            if (time <= 0)
                return time * (curve[0] > 0 ? curve[1] / curve[0] : curve[1] == 0 && curve[2] > 0 ? curve[3] / curve[2] : 0);
            if (time >= 1)
                return 1 + (time - 1) * (curve[2] < 1 ? (curve[3] - 1) / (curve[2] - 1) : curve[2] == 1 && curve[0] < 1 ? (curve[1] - 1) / (curve[0] - 1) : 0);
            double lower = 0, upper = 1;
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var midpoint = (lower + upper) / 2;
                var estimate = Bezier(curve[0], curve[2], midpoint);
                if (Math.Abs(time - estimate) < 0.00001)
                    return Bezier(curve[1], curve[3], midpoint);
                if (estimate < time)
                    lower = midpoint;
                else
                    upper = midpoint;
            }
            throw new FormatException("Transaction animation curve did not converge.");
        }

        private static double Bezier(double a, double b, double position)
            => 3 * a * (1 - position) * (1 - position) * position + 3 * b * (1 - position) * position * position + position * position * position;
    }
}
